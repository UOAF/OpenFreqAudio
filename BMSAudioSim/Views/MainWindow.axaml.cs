using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Controls.PanAndZoom;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using BMSAudioSim.ViewModels;
using Microsoft.Extensions.Logging;
using OpenFreqAudio;
using OpenFreqAudio.TerrainSampling;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ScottPlot;
using SkiaSharp;
using Color = ScottPlot.Color;
using Colors = ScottPlot.Colors;
using Cursor = Avalonia.Input.Cursor;
using Line = Avalonia.Controls.Shapes.Line;
using Path = System.IO.Path;

namespace BMSAudioSim.Views;

public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
{
    private const int HEIGHTMAP_SIZE = 32768;
    private const int PREVIEW_SIZE = 2048;

    // Pixels-per-(m/s) for velocity vector display at zoom == 1.
    // 300 m/s (≈ Mach 0.9) → ~90 px at zoom 1, which is nicely visible.
    private const double VELOCITY_DISPLAY_SCALE = 0.30;

    // Simulation tick interval in milliseconds.
    private const int SIMULATION_TICK_MS = 100;

    // Map cell size in meters per pixel — mirrors the value used when loading the heightmap.
    private const double CELL_SIZE_METERS = 1024.0 * 1000.0 / HEIGHTMAP_SIZE; // ≈ 31.25 m/px

    private string? _previewImagePath;
    private int clickCount = 0;
    private (int x, int y)? _sender1Pos;
    private (int x, int y)? _sender2Pos;
    private (int x, int y)? _receiverPos;
    private FastPathAudioSim? _fastPathAudioSim;
    private HeightPyramid? _pyramid;
    private AudioParams? _signal1Params;
    private AudioParams? _signal2Params;
    private RadioPlayback _radioPlayback;
    private ILoggerFactory _loggerFactory;
    private MainWindowViewModel _viewModel;

    private readonly string _stream1Id = "stream1";
    private readonly string _stream1File = "countdown.ogg";
    private readonly string _stream2Id = "stream2";
    private readonly string _stream2File = "audio2.ogg";

    // ===== MARKER DISPLAY =====
    private Ellipse? _sender1Marker;
    private Ellipse? _sender2Marker;
    private Ellipse? _receiverMarker;

    // ===== VELOCITY =====
    // Stored in m/s; (0, 0) = stationary
    private (double vx, double vy, double vz) _sender1Vel = (0, 0, 0);
    private (double vx, double vy, double vz) _sender2Vel = (0, 0, 0);
    private (double vx, double vy, double vz) _receiverVel = (0, 0, 0);

    // Velocity handle ellipses (for hit-testing)
    private Ellipse? _sender1VelHandle;
    private Ellipse? _sender2VelHandle;
    private Ellipse? _receiverVelHandle;

    // ===== DRAG STATE =====
    private enum DragTarget { None, Sender1, Sender2, Receiver, Sender1Vel, Sender2Vel, ReceiverVel }
    private DragTarget _currentDrag = DragTarget.None;
    private Point _lastDragPoint;
    // Captured pointer so PointerMoved fires even if pointer leaves the handle
    private IPointer? _capturedPointer;

    // ===== SIMULATION (play/pause per marker) =====
    private bool _sender1Simulating = false;
    private bool _sender2Simulating = false;
    private bool _receiverSimulating = false;
    private DispatcherTimer? _simTimer;
    
    // ===== Radio Playbac =====
    private Guid radioSlotId = Guid.NewGuid();


    public MainWindow(MainWindowViewModel viewModel, ILoggerFactory loggerFactory)
    {
        _viewModel = viewModel;
        _loggerFactory = loggerFactory;
        _radioPlayback = new RadioPlayback(_loggerFactory);
        DataContext = viewModel;
        this.WhenActivated(disposables =>
        {
            /* Handle view activation etc. */
        });
        InitializeComponent();
        ButtonSignal1Ptt.AddHandler(PointerPressedEvent, (sender, e) =>
        {
            if (_signal1Params is null) return;
            _radioPlayback.StartStream(_stream1Id, _stream1File, _signal1Params, _viewModel.AmbientNoiseType);
        }, handledEventsToo: true);

        ButtonSignal1Ptt.AddHandler(PointerReleasedEvent, (sender, e) =>
        {
            _radioPlayback.StopStream(_stream1Id).Wait(300);
        }, handledEventsToo: true);

        ButtonSignal2Ptt.AddHandler(PointerPressedEvent,
            (sender, e) =>
            {
                if (_signal2Params is null) return;
                _radioPlayback.StartStream(_stream2Id, _stream2File, _signal2Params, _viewModel.AmbientNoiseType);
            },
            handledEventsToo: true);

        ButtonSignal2Ptt.AddHandler(PointerReleasedEvent,
            (sender, e) => { _radioPlayback.StopStream(_stream2Id).Wait(300); }, handledEventsToo: true);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _radioPlayback.Initialize();
        _radioPlayback.SetSquelchLevel(_viewModel.FrequencyKhz, radioSlotId, _viewModel.Squelch);

        // Simulation timer — always running; only advances markers that are "playing"
        _simTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SIMULATION_TICK_MS) };
        _simTimer.Tick += OnSimulationTick;
        _simTimer.Start();
    }

    // ===========================
    //  SIMULATION TICK
    // ===========================

    private void OnSimulationTick(object? sender, EventArgs e)
    {
        double dt = SIMULATION_TICK_MS / 1000.0; // seconds per tick
        bool anyMoved = false;

        if (_sender1Simulating && _sender1Pos.HasValue)
        {
            _sender1Pos = AdvancePosition(_sender1Pos.Value, _sender1Vel, dt);
            anyMoved = true;
        }

        if (_sender2Simulating && _sender2Pos.HasValue)
        {
            _sender2Pos = AdvancePosition(_sender2Pos.Value, _sender2Vel, dt);
            anyMoved = true;
        }

        if (_receiverSimulating && _receiverPos.HasValue)
        {
            _receiverPos = AdvancePosition(_receiverPos.Value, _receiverVel, dt);
            anyMoved = true;
        }

        if (anyMoved)
        {
            UpdatePositionDisplay();
            UpdateMarkers();
        }
    }

    /// <summary>
    /// Move a map-pixel position by velocity (m/s) × dt (s), clamped to map bounds.
    /// Velocity direction: +vx = East (increasing X), +vy = South (increasing Y).
    /// </summary>
    private static (int x, int y) AdvancePosition((int x, int y) pos, (double vx, double vy, double vz) vel, double dt)
    {
        double newX = pos.x + vel.vx * dt / CELL_SIZE_METERS;
        double newY = pos.y + vel.vy * dt / CELL_SIZE_METERS;
        return ClampMapPos(((int)Math.Round(newX), (int)Math.Round(newY)));
    }

    // ===========================
    //  PLAY / PAUSE BUTTON HANDLERS
    // ===========================

    private void OnPlay1Clicked(object? sender, RoutedEventArgs e)
    {
        _sender1Simulating = !_sender1Simulating;
        PlayButton1.Content = _sender1Simulating ? "⏸ Pause" : "▶ Simulate";
    }

    private void OnPlay2Clicked(object? sender, RoutedEventArgs e)
    {
        _sender2Simulating = !_sender2Simulating;
        PlayButton2.Content = _sender2Simulating ? "⏸ Pause" : "▶ Simulate";
    }

    private void OnPlayReceiverClicked(object? sender, RoutedEventArgs e)
    {
        _receiverSimulating = !_receiverSimulating;
        PlayButtonReceiver.Content = _receiverSimulating ? "⏸ Pause" : "▶ Simulate";
    }

    // ===========================
    //  CANVAS POINTER HANDLERS
    // ===========================

    /// <summary>
    /// Single entry-point for all pointer presses on the marker canvas.
    /// Determines whether the press lands on a draggable element or should
    /// be treated as a new-marker click.
    /// </summary>
    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_pyramid == null || HeightmapImage.Source == null) return;

        var canvasPos = e.GetPosition(MarkerCanvas);
        var target = FindDragTarget(canvasPos);

        if (target != DragTarget.None)
        {
            // --- Start dragging an existing element ---
            _currentDrag = target;
            _lastDragPoint = canvasPos;
            _capturedPointer = e.Pointer;
            e.Pointer.Capture(MarkerCanvas);
            e.Handled = true;
        }
        else
        {
            // --- Treat as a new-marker placement click ---
            var mapCoords = CanvasToMapCoordinates(canvasPos);
            if (mapCoords == null) return;

            var (mapX, mapY) = mapCoords.Value;

            if (clickCount % 3 == 0)
                _sender1Pos = (mapX, mapY);
            else if (clickCount % 3 == 1)
                _sender2Pos = (mapX, mapY);
            else
                _receiverPos = (mapX, mapY);

            clickCount++;
            UpdatePositionDisplay();
            UpdateMarkers();
            ConfigPanel.IsEnabled = _sender1Pos.HasValue && _sender2Pos.HasValue && _receiverPos.HasValue;
        }
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_currentDrag == DragTarget.None) return;

        var canvasPos = e.GetPosition(MarkerCanvas);
        var delta = canvasPos - _lastDragPoint;
        _lastDragPoint = canvasPos;

        switch (_currentDrag)
        {
            case DragTarget.Sender1:
                _sender1Pos = ClampMapPos(ApplyCanvasDeltaToMapPos(_sender1Pos!.Value, delta));
                UpdatePositionDisplay();
                UpdateMarkers();
                break;

            case DragTarget.Sender2:
                _sender2Pos = ClampMapPos(ApplyCanvasDeltaToMapPos(_sender2Pos!.Value, delta));
                UpdatePositionDisplay();
                UpdateMarkers();
                break;

            case DragTarget.Receiver:
                _receiverPos = ClampMapPos(ApplyCanvasDeltaToMapPos(_receiverPos!.Value, delta));
                UpdatePositionDisplay();
                UpdateMarkers();
                break;

            case DragTarget.Sender1Vel:
                _sender1Vel = UpdateVelocityFromDrag(_sender1Vel, delta);
                UpdateVelocityLabels();
                UpdateParameters();  
                UpdateMarkers();
                break;

            case DragTarget.Sender2Vel:
                _sender2Vel = UpdateVelocityFromDrag(_sender2Vel, delta);
                UpdateVelocityLabels();
                UpdateParameters();  
                UpdateMarkers();
                break;

            case DragTarget.ReceiverVel:
                _receiverVel = UpdateVelocityFromDrag(_receiverVel, delta);
                UpdateVelocityLabels();
                UpdateParameters();  
                UpdateMarkers();
                break;
        }

        e.Handled = true;
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_currentDrag == DragTarget.None) return;

        _currentDrag = DragTarget.None;
        e.Pointer.Capture(null);
        _capturedPointer = null;
        e.Handled = true;
    }

    // ===========================
    //  DRAG HELPERS
    // ===========================

    /// <summary>
    /// Hit radius in canvas pixels (independent of zoom).
    /// </summary>
    private const double HIT_RADIUS_PX = 14.0;

    private DragTarget FindDragTarget(Point canvasPos)
    {
        // Check velocity handles first (they sit on top and are smaller targets)
        if (_sender1Pos.HasValue && _sender1VelHandle != null)
        {
            var hp = VelocityHandleScreenPos(_sender1Pos.Value, _sender1Vel);
            if (Distance(canvasPos, hp) < HIT_RADIUS_PX) return DragTarget.Sender1Vel;
        }
        if (_sender2Pos.HasValue && _sender2VelHandle != null)
        {
            var hp = VelocityHandleScreenPos(_sender2Pos.Value, _sender2Vel);
            if (Distance(canvasPos, hp) < HIT_RADIUS_PX) return DragTarget.Sender2Vel;
        }
        if (_receiverPos.HasValue && _receiverVelHandle != null)
        {
            var hp = VelocityHandleScreenPos(_receiverPos.Value, _receiverVel);
            if (Distance(canvasPos, hp) < HIT_RADIUS_PX) return DragTarget.ReceiverVel;
        }

        // Then check the main markers
        if (_sender1Pos.HasValue)
        {
            var mp = ImageToScreenCoordinates(_sender1Pos.Value.x, _sender1Pos.Value.y);
            if (Distance(canvasPos, mp) < HIT_RADIUS_PX) return DragTarget.Sender1;
        }
        if (_sender2Pos.HasValue)
        {
            var mp = ImageToScreenCoordinates(_sender2Pos.Value.x, _sender2Pos.Value.y);
            if (Distance(canvasPos, mp) < HIT_RADIUS_PX) return DragTarget.Sender2;
        }
        if (_receiverPos.HasValue)
        {
            var mp = ImageToScreenCoordinates(_receiverPos.Value.x, _receiverPos.Value.y);
            if (Distance(canvasPos, mp) < HIT_RADIUS_PX) return DragTarget.Receiver;
        }

        return DragTarget.None;
    }

    /// <summary>
    /// Apply a canvas-space pixel delta to a map position, accounting for the
    /// current image-to-canvas scale.
    /// </summary>
    private (int x, int y) ApplyCanvasDeltaToMapPos((int x, int y) mapPos, Vector delta)
    {
        var scale = GetImageToCanvasScale();
        int newX = mapPos.x + (int)(delta.X / scale.scaleX);
        int newY = mapPos.y + (int)(delta.Y / scale.scaleY);
        return (newX, newY);
    }

    /// <summary>
    /// Apply a canvas-space pixel delta to a velocity vector (m/s).
    /// </summary>
    private (double vx, double vy, double vz) UpdateVelocityFromDrag((double vx, double vy, double vz) vel, Vector delta)
    {
        double scale = VELOCITY_DISPLAY_SCALE; // px per (m/s) at zoom=1
        // Dragging right/down increases positive vx/vy
        return (vel.vx + delta.X / scale, vel.vy + delta.Y / scale, 0);
    }

    private static (int x, int y) ClampMapPos((int x, int y) pos) =>
        (Math.Clamp(pos.x, 0, HEIGHTMAP_SIZE - 1), Math.Clamp(pos.y, 0, HEIGHTMAP_SIZE - 1));

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ===========================
    //  COORDINATE HELPERS
    // ===========================

    /// <summary>
    /// Returns (scaleX, scaleY) in canvas-pixels per map-unit.
    /// </summary>
    private (double scaleX, double scaleY) GetImageToCanvasScale()
    {
        var imageBounds = HeightmapImage.Bounds;
        var bitmap = HeightmapImage.Source as Bitmap;
        if (bitmap == null) return (1, 1);

        double imageAspect = (double)bitmap.PixelSize.Width / bitmap.PixelSize.Height;
        double controlAspect = imageBounds.Width / imageBounds.Height;

        double actualWidth, actualHeight;
        if (controlAspect > imageAspect)
        {
            actualHeight = imageBounds.Height;
            actualWidth = actualHeight * imageAspect;
        }
        else
        {
            actualWidth = imageBounds.Width;
            actualHeight = actualWidth / imageAspect;
        }

        return (actualWidth / HEIGHTMAP_SIZE, actualHeight / HEIGHTMAP_SIZE);
    }

    /// <summary>
    /// Convert a canvas position back to heightmap coordinates, or null if
    /// the position is outside the rendered image area.
    /// </summary>
    private (int mapX, int mapY)? CanvasToMapCoordinates(Point canvasPos)
    {
        var imageBounds = HeightmapImage.Bounds;
        var bitmap = HeightmapImage.Source as Bitmap;
        if (bitmap == null) return null;

        double imageAspect = (double)bitmap.PixelSize.Width / bitmap.PixelSize.Height;
        double controlAspect = imageBounds.Width / imageBounds.Height;

        double actualWidth, actualHeight, offsetX, offsetY;
        if (controlAspect > imageAspect)
        {
            actualHeight = imageBounds.Height;
            actualWidth = actualHeight * imageAspect;
            offsetX = (imageBounds.Width - actualWidth) / 2;
            offsetY = 0;
        }
        else
        {
            actualWidth = imageBounds.Width;
            actualHeight = actualWidth / imageAspect;
            offsetX = 0;
            offsetY = (imageBounds.Height - actualHeight) / 2;
        }

        double imageLeft = imageBounds.Left + offsetX;
        double imageTop  = imageBounds.Top  + offsetY;

        double relX = (canvasPos.X - imageLeft) / actualWidth;
        double relY = (canvasPos.Y - imageTop)  / actualHeight;

        if (relX < 0 || relX > 1 || relY < 0 || relY > 1) return null;

        int mapX = Math.Clamp((int)(relX * HEIGHTMAP_SIZE), 0, HEIGHTMAP_SIZE - 1);
        int mapY = Math.Clamp((int)(relY * HEIGHTMAP_SIZE), 0, HEIGHTMAP_SIZE - 1);
        return (mapX, mapY);
    }

    // ===========================
    //  MARKER + VELOCITY DISPLAY
    // ===========================

    /// <summary>
    /// Canvas-space position of a velocity handle given a map-position and velocity (m/s).
    /// </summary>
    private Point VelocityHandleScreenPos((int x, int y) mapPos, (double vx, double vy, double vz) vel)
    {
        var markerScreen = ImageToScreenCoordinates(mapPos.x, mapPos.y);
        return new Point(
            markerScreen.X + vel.vx * VELOCITY_DISPLAY_SCALE,
            markerScreen.Y + vel.vy * VELOCITY_DISPLAY_SCALE);
    }

    private void UpdateMarkers()
    {
        if (MarkerCanvas == null) return;

        MarkerCanvas.Children.Clear();
        _sender1Marker     = null;
        _sender2Marker     = null;
        _receiverMarker    = null;
        _sender1VelHandle  = null;
        _sender2VelHandle  = null;
        _receiverVelHandle = null;

        if (_sender1Pos.HasValue)
        {
            var screenPos = ImageToScreenCoordinates(_sender1Pos.Value.x, _sender1Pos.Value.y);
            AddVelocityVector(screenPos, _sender1Vel, Brushes.LimeGreen, ref _sender1VelHandle);
            _sender1Marker = CreateMarker(screenPos, Brushes.LimeGreen);
            MarkerCanvas.Children.Add(_sender1Marker);
        }

        if (_sender2Pos.HasValue)
        {
            var screenPos = ImageToScreenCoordinates(_sender2Pos.Value.x, _sender2Pos.Value.y);
            AddVelocityVector(screenPos, _sender2Vel, Brushes.DodgerBlue, ref _sender2VelHandle);
            _sender2Marker = CreateMarker(screenPos, Brushes.DodgerBlue);
            MarkerCanvas.Children.Add(_sender2Marker);
        }

        if (_receiverPos.HasValue)
        {
            var screenPos = ImageToScreenCoordinates(_receiverPos.Value.x, _receiverPos.Value.y);
            AddVelocityVector(screenPos, _receiverVel, Brushes.OrangeRed, ref _receiverVelHandle);
            _receiverMarker = CreateMarker(screenPos, Brushes.Red);
            MarkerCanvas.Children.Add(_receiverMarker);
        }
    }

    /// <summary>
    /// Draws a velocity vector line + arrowhead + draggable handle + speed label.
    /// The handle reference is set so hit-testing can find it later.
    /// </summary>
    private void AddVelocityVector(Point markerScreen, (double vx, double vy, double vz) vel,
                                   IBrush color, ref Ellipse? handleRef)
    {
        double speed = Math.Sqrt(vel.vx * vel.vx + vel.vy * vel.vy); // m/s
        var handlePos = new Point(
            markerScreen.X + vel.vx * VELOCITY_DISPLAY_SCALE,
            markerScreen.Y + vel.vy * VELOCITY_DISPLAY_SCALE);

        double lineThickness = Math.Max(1.5, 2.5 / ZoomBorder.ZoomX);

        // --- Stem line ---
        var line = new Line
        {
            StartPoint = markerScreen,
            EndPoint   = handlePos,
            Stroke     = color,
            StrokeThickness = lineThickness,
            Opacity    = 0.75,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 6, 3 }
        };
        MarkerCanvas.Children.Add(line);

        // --- Arrowhead (small triangle pointing from marker towards handle) ---
        if (speed > 1.0)
        {
            double ux = vel.vx / speed;
            double uy = vel.vy / speed;
            double arrowLen  = Math.Max(8, 12 / ZoomBorder.ZoomX);
            double arrowWing = arrowLen * 0.45;

            // Arrowhead tip is a bit before the handle so it doesn't overlap
            var tip = new Point(handlePos.X - ux * 2, handlePos.Y - uy * 2);
            var left  = new Point(tip.X - ux * arrowLen + uy * arrowWing,
                                  tip.Y - uy * arrowLen - ux * arrowWing);
            var right = new Point(tip.X - ux * arrowLen - uy * arrowWing,
                                  tip.Y - uy * arrowLen + ux * arrowWing);

            var arrow = new Avalonia.Controls.Shapes.Polygon
            {
                Points  = new Avalonia.Collections.AvaloniaList<Point> { tip, left, right },
                Fill    = color,
                Opacity = 0.85,
                IsHitTestVisible = false
            };
            MarkerCanvas.Children.Add(arrow);
        }

        // --- Draggable handle circle ---
        double handleSize = Math.Max(10, 14 / ZoomBorder.ZoomX);
        var handle = new Ellipse
        {
            Width  = handleSize,
            Height = handleSize,
            Fill   = color,
            Stroke = Brushes.White,
            StrokeThickness = Math.Max(1, 1.5 / ZoomBorder.ZoomX),
            Opacity = 0.9
        };
        Canvas.SetLeft(handle, handlePos.X - handleSize / 2);
        Canvas.SetTop (handle, handlePos.Y - handleSize / 2);
        MarkerCanvas.Children.Add(handle);
        handleRef = handle;

        // --- Speed label ---
        double kts = speed * 1.94384; // m/s → knots
        double heading = Math.Atan2(vel.vx, -vel.vy) * 180.0 / Math.PI;
        if (heading < 0) heading += 360;
        var label = new TextBlock
        {
            Text       = $"{kts:F0} kts / {heading:F0}°",
            Foreground = color,
            Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(160, 0, 0, 0)),
            FontSize   = Math.Max(9, 11 / ZoomBorder.ZoomX),
            Padding    = new Thickness(2),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, handlePos.X + handleSize / 2 + 3);
        Canvas.SetTop (label, handlePos.Y - handleSize / 2);
        MarkerCanvas.Children.Add(label);
    }

    private Ellipse CreateMarker(Point position, IBrush fill)
    {
        double markerSize = 20d / ZoomBorder.ZoomX;
        var marker = new Ellipse
        {
            Width  = markerSize,
            Height = markerSize,
            Fill   = fill,
            Stroke = Brushes.White,
            StrokeThickness = 2d / ZoomBorder.ZoomX,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        Canvas.SetLeft(marker, position.X - markerSize / 2);
        Canvas.SetTop (marker, position.Y - markerSize / 2);

        return marker;
    }

    private Point ImageToScreenCoordinates(int mapX, int mapY)
    {
        var imageBounds = HeightmapImage.Bounds;
        var bitmap = HeightmapImage.Source as Bitmap;

        if (bitmap == null)
            return new Point(0, 0);

        double imageAspect = (double)bitmap.PixelSize.Width / bitmap.PixelSize.Height;
        double controlAspect = imageBounds.Width / imageBounds.Height;

        double actualWidth, actualHeight, offsetX, offsetY;

        if (controlAspect > imageAspect)
        {
            actualHeight = imageBounds.Height;
            actualWidth  = actualHeight * imageAspect;
            offsetX = (imageBounds.Width - actualWidth) / 2;
            offsetY = 0;
        }
        else
        {
            actualWidth  = imageBounds.Width;
            actualHeight = actualWidth / imageAspect;
            offsetX = 0;
            offsetY = (imageBounds.Height - actualHeight) / 2;
        }

        double relX = (double)mapX / HEIGHTMAP_SIZE;
        double relY = (double)mapY / HEIGHTMAP_SIZE;

        double canvasX = imageBounds.Left + offsetX + relX * actualWidth;
        double canvasY = imageBounds.Top  + offsetY + relY * actualHeight;

        return new Point(canvasX, canvasY);
    }

    // ===========================
    //  UPDATE METHODS
    // ===========================

    private void UpdateVelocityLabels()
    {
        double SpeedKts((double vx, double vy, double vz) v) => Math.Sqrt(v.vx * v.vx + v.vy * v.vy) * 1.94384;
        double Hdg((double vx, double vy, double vz) v)
        {
            double h = Math.Atan2(v.vx, -v.vy) * 180.0 / Math.PI;
            return h < 0 ? h + 360 : h;
        }

        if (Sender1VelText != null)
            Sender1VelText.Text = _sender1Pos.HasValue
                ? $"Speed: {SpeedKts(_sender1Vel):F0} kts  HDG {Hdg(_sender1Vel):F0}°"
                : "Speed: —";

        if (Sender2VelText != null)
            Sender2VelText.Text = _sender2Pos.HasValue
                ? $"Speed: {SpeedKts(_sender2Vel):F0} kts  HDG {Hdg(_sender2Vel):F0}°"
                : "Speed: —";

        if (ReceiverVelText != null)
            ReceiverVelText.Text = _receiverPos.HasValue
                ? $"Speed: {SpeedKts(_receiverVel):F0} kts  HDG {Hdg(_receiverVel):F0}°"
                : "Speed: —";
    }

    private async void OnLoadClicked(object? sender, RoutedEventArgs e)
    {
        var storage = StorageProvider;
        var filepickerOptions = new FilePickerOpenOptions
        {
            Title = "Open Heightmap File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("BMS NT HeightMap") { Patterns = new[] { "HeightMap.raw" } }
            }
        };

        var file = await storage.OpenFilePickerAsync(filepickerOptions);

        if (file.Count > 0)
        {
            // HeightPyramid's ctor scans the full DEM to build the max-pyramid; keep the UI thread free.
            _pyramid = await Task.Run(() => HeightPyramid.FromFile(file[0].Path.LocalPath, HEIGHTMAP_SIZE, HEIGHTMAP_SIZE));
            await LoadHeightmapAsync(file[0].Path.LocalPath);
        }
    }

    private async Task LoadHeightmapAsync(string filePath)
    {
        try
        {
            StatusText.Text = "Loading heightmap...";

            // Load the raw heightmap data
            var cellSizeM = 1024d * 1000d / HEIGHTMAP_SIZE;
            if (_pyramid != null)
                _fastPathAudioSim = new FastPathAudioSim(_pyramid, originX: 0, originY: 0, cellSizeMeters: cellSizeM,
                    _loggerFactory.CreateLogger<FastPathAudioSim>());

            StatusText.Text = "Creating preview image (this can take a minute)...";
            // Generate preview image path
            var fileDir = Path.GetDirectoryName(filePath) ?? Environment.CurrentDirectory;
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            _previewImagePath = Path.Combine(fileDir, $"{fileName}_preview.jpg");

            // Create preview if it doesn't exist
            if (!File.Exists(_previewImagePath))
            {
                await Task.Run(() => CreatePreviewImage(_previewImagePath));
            }

            // Load and display the preview
            await LoadPreviewImage(_previewImagePath);

            StatusText.Text = $"Heightmap loaded: {HEIGHTMAP_SIZE}x{HEIGHTMAP_SIZE}";

            clickCount = 0;
            _sender1Pos = null;
            _sender2Pos = null;
            _receiverPos = null;

            UpdatePositionDisplay();
            UpdateMarkers();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private void CreatePreviewImage(string outputPath)
    {
        if (_pyramid == null) return;

        int actualWidth  = _pyramid.Width;
        int actualHeight = _pyramid.Height;

        float minHeight = float.MaxValue;
        float maxHeight = float.MinValue;

        for (int y = 0; y < actualHeight; y++)
        {
            for (int x = 0; x < actualWidth; x++)
            {
                float h = (float)(_pyramid.SampleNativeFeet(x, y) * HeightPyramid.FeetToMeters);
                if (h < -12) h = -12;
                if (h < minHeight) minHeight = h;
                if (h > maxHeight) maxHeight = h;
            }
        }

        float range = maxHeight - minHeight;
        if (range == 0) range = 1;

        using var bitmap = new SKBitmap(PREVIEW_SIZE, PREVIEW_SIZE, SKColorType.Rgba8888, SKAlphaType.Opaque);
        IntPtr pixelsAddr = bitmap.GetPixels();
        unsafe
        {
            uint* pixels = (uint*)pixelsAddr.ToPointer();
            for (int py = 0; py < PREVIEW_SIZE; py++)
            {
                for (int px = 0; px < PREVIEW_SIZE; px++)
                {
                    int sx = (int)(px * (actualWidth  - 1) / (float)(PREVIEW_SIZE - 1));
                    int sy = (int)(py * (actualHeight - 1) / (float)(PREVIEW_SIZE - 1));
                    float height = (float)(_pyramid.SampleNativeFeet(sx, sy) * HeightPyramid.FeetToMeters);
                    float normalized = 1.0f - (height - minHeight) / range;
                    SKColor color = GetHeightColor(normalized);
                    pixels[py * PREVIEW_SIZE + px] = (uint)color;
                }
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        using var stream = File.OpenWrite(outputPath);
        data.SaveTo(stream);
    }

    private SKColor GetHeightColor(float value)
    {
        // Create a color gradient from low to high elevation
        // Blue (0) -> Cyan -> Green -> Yellow -> Red (1)
        value = Math.Clamp(value, 0, 1);
        byte r, g, b;

        if (value < 0.25f)
        {
            float t = value / 0.25f;
            r = 0; g = (byte)(255 * t); b = 255;
        }
        else if (value < 0.5f)
        {
            float t = (value - 0.25f) / 0.25f;
            r = 0; g = (byte)(255 - 55 * t); b = (byte)(255 * (1 - t));
        }
        else if (value < 0.75f)
        {
            float t = (value - 0.5f) / 0.25f;
            r = (byte)(255 * t); g = (byte)(200 + 55 * t); b = 0;
        }
        else
        {
            float t = (value - 0.75f) / 0.25f;
            r = 255; g = (byte)(255 * (1 - t)); b = 0;
        }

        return new SKColor(r, g, b);
    }

    private async Task LoadPreviewImage(string path)
    {
        await using var stream = File.OpenRead(path);
        HeightmapImage.Source = new Bitmap(stream);
    }

    private void UpdateParameters()
    {
        if (_fastPathAudioSim is null || !_sender1Pos.HasValue || !_sender2Pos.HasValue || !_receiverPos.HasValue)
            return;

        Debug.Assert(ViewModel != null, nameof(ViewModel) + " != null");

        // Path 1: Sender1 -> Receiver
        var audioParams1 = _fastPathAudioSim.CalculateAudioParams(
            _fastPathAudioSim.PixelsToMeters(_sender1Pos.Value.x),
            _fastPathAudioSim.PixelsToMeters(_sender1Pos.Value.y),
            ViewModel.TX1Altitude,
            _fastPathAudioSim.PixelsToMeters(_receiverPos.Value.x),
            _fastPathAudioSim.PixelsToMeters(_receiverPos.Value.y),
            ViewModel.RXAltitude,
            ViewModel.FrequencyKhz,
            ViewModel.Ppm1,
            ViewModel.TxWatts,
            ViewModel.RxDbm,
            true, 
            txVelocity: _sender1Vel, 
            rxVelocity: _receiverVel
            );

        // Path 2: Sender2 -> Receiver
        var audioParams2 = _fastPathAudioSim.CalculateAudioParams(
            _fastPathAudioSim.PixelsToMeters(_sender2Pos.Value.x),
            _fastPathAudioSim.PixelsToMeters(_sender2Pos.Value.y),
            ViewModel.TX2Altitude,
            _fastPathAudioSim.PixelsToMeters(_receiverPos.Value.x),
            _fastPathAudioSim.PixelsToMeters(_receiverPos.Value.y),
            ViewModel.RXAltitude,
            ViewModel.FrequencyKhz,
            ViewModel.Ppm2,
            ViewModel.TxWatts,
            ViewModel.RxDbm,
            true,
            txVelocity: _sender2Vel,
            rxVelocity: _receiverVel);

        if (audioParams1 == null || audioParams2 == null) throw new Exception("audioParams is null");

        UpdateProfileGraph(audioParams1, audioParams2);

        Power1Text.Text = audioParams1.ReceivedDb.ToString("F1");
        Dropout1Text.Text = audioParams1.DropoutRate.ToString();
        DeepFade1Text.Text = audioParams1.DeepFadeRate.ToString();

        Power2Text.Text = audioParams2.ReceivedDb.ToString("F1");
        Dropout2Text.Text = audioParams2.DropoutRate.ToString();
        DeepFade2Text.Text = audioParams2.DeepFadeRate.ToString();

        _signal1Params = audioParams1;
        _signal2Params = audioParams2;

        _radioPlayback.TuneFrequency(ViewModel.FrequencyKhz, radioSlotId);
        _radioPlayback.SetSquelchLevel(ViewModel.FrequencyKhz, radioSlotId, ViewModel.Squelch);

        _radioPlayback.UpdateStreamParams(_stream1Id, _signal1Params);
        _radioPlayback.UpdateStreamParams(_stream2Id, _signal2Params);
    }

    private void UpdatePositionDisplay()
    {
        if (_sender1Pos.HasValue)
            Sender1PosText.Text = $"X: {_sender1Pos.Value.x}, Y: {_sender1Pos.Value.y}";
        else
            Sender1PosText.Text = "Not set";

        if (_sender2Pos.HasValue)
            Sender2PosText.Text = $"X: {_sender2Pos.Value.x}, Y: {_sender2Pos.Value.y}";
        else
            Sender2PosText.Text = "Not set";

        if (_receiverPos.HasValue)
            ReceiverPosText.Text = $"X: {_receiverPos.Value.x}, Y: {_receiverPos.Value.y}";
        else
            ReceiverPosText.Text = "Not set";

        UpdateVelocityLabels();

        if (_sender1Pos.HasValue && _sender2Pos.HasValue && _receiverPos.HasValue)
            UpdateParameters();
    }

    private void UpdateProfileGraph(AudioParams audioParams1, AudioParams audioParams2)
    {
        HeightProfilePlot.Plot.Clear();

        Debug.Assert(ViewModel != null, nameof(ViewModel) + " != null");

        PixelPadding padding = new(80, 30, 30, 50);
        HeightProfilePlot.Plot.Layout.Fixed(padding);

        double dist1 = audioParams1.TerrainProfile != null && audioParams1.TerrainProfile.Count > 0
            ? audioParams1.TerrainProfile.Last().dist - audioParams1.TerrainProfile.First().dist : 0;
        double dist2 = audioParams2.TerrainProfile != null && audioParams2.TerrainProfile.Count > 0
            ? audioParams2.TerrainProfile.Last().dist - audioParams2.TerrainProfile.First().dist : 0;
        double maxDist = Math.Max(dist1, dist2);

        dist1 = DrawPathOnGraph(audioParams1, ViewModel.TX1Altitude,
            Color.FromHex("#2E86AB"), Color.FromHex("#06D6A0"), Colors.Green, Colors.LimeGreen, "S1", maxDist - dist1);
        dist2 = DrawPathOnGraph(audioParams2, ViewModel.TX2Altitude,
            Color.FromHex("#6B5B95"), Color.FromHex("#4A90D9"), Colors.Blue, Colors.DodgerBlue, "S2", maxDist - dist2);

        HeightProfilePlot.Plot.Title("Height Profiles: Sender 1 & 2 to Receiver");
        HeightProfilePlot.Plot.XLabel("Distance (m)");
        HeightProfilePlot.Plot.YLabel("Elevation (m)");
        HeightProfilePlot.Plot.Axes.Title.Label.FontSize = 14;
        HeightProfilePlot.Plot.Axes.Title.Label.Bold     = true;
        HeightProfilePlot.Plot.Grid.MajorLineColor = Color.FromHex("#E0E0E0");
        HeightProfilePlot.Plot.Grid.MinorLineColor = Color.FromHex("#F0F0F0");
        HeightProfilePlot.Plot.ShowLegend(Alignment.UpperRight);
        HeightProfilePlot.Plot.Axes.AutoScale();
        HeightProfilePlot.Plot.Axes.Margins(0.05, 0.15);
        HeightProfilePlot.Refresh();

        ProfileInfoText.Text = $"S1: {dist1 / 1000:F1} km | S2: {dist2 / 1000:F1} km";
    }

    private double DrawPathOnGraph(AudioParams audioParams, int txAltitude,
        Color terrainColor, Color txMarkerColor, Color losColor, Color fresnelColor, string label,
        double xOffset = 0)
    {
        if (audioParams.TerrainProfile == null || audioParams.TerrainProfile.Count == 0)
            return 0;

        double[] xValues = new double[audioParams.TerrainProfile.Count];
        double[] yValues = new double[audioParams.TerrainProfile.Count];

        for (int i = 0; i < audioParams.TerrainProfile.Count; i++)
        {
            xValues[i] = audioParams.TerrainProfile[i].dist + xOffset;
            yValues[i] = audioParams.TerrainProfile[i].elev;
        }

        // Terrain fill
        var scatter = HeightProfilePlot.Plot.Add.ScatterLine(xValues, yValues);
        scatter.Color     = terrainColor.WithAlpha(0.2);
        scatter.LineWidth = 0;
        scatter.FillY     = true;
        scatter.FillYValue = yValues.Min();

        // Terrain line
        var line = HeightProfilePlot.Plot.Add.ScatterLine(xValues, yValues);
        line.Color      = terrainColor;
        line.LineWidth  = 2.5f;
        line.Smooth     = true;
        line.LegendText = $"{label} Terrain";

        var txAbsoluteHeight = txAltitude + audioParams.TerrainProfile.First().elev;
        var rxAbsoluteHeight = ViewModel!.RXAltitude + audioParams.TerrainProfile.Last().elev;

        var senderMarker   = HeightProfilePlot.Plot.Add.Marker(xValues[0], txAbsoluteHeight);
        senderMarker.Color = txMarkerColor;
        senderMarker.Size  = 12;
        senderMarker.Shape = MarkerShape.FilledCircle;

        var receiverMarker = HeightProfilePlot.Plot.Add.Marker(xValues[audioParams.TerrainProfile.Count - 1], rxAbsoluteHeight);
        receiverMarker.Color = Color.FromHex("#EF476F");
        receiverMarker.Size  = 12;
        receiverMarker.Shape = MarkerShape.FilledCircle;

        // ==================== CURVED LOS PATH WITH EARTH CURVATURE ====================
        double totalDistance = xValues[xValues.Length - 1] - xValues[0];
        const double earthRadius = 6378000.0;
        double kAvg = FastPathAudioSim.CalculateKAvg(txAbsoluteHeight, rxAbsoluteHeight);
        double effectiveEarthRadius = kAvg * earthRadius;

        // Calculate LOS curve
        int losPoints = 200;
        double[] losDist      = new double[losPoints];
        double[] losHeight    = new double[losPoints];
        double[] fresnelUpper = new double[losPoints];
        double[] fresnelLower = new double[losPoints];

        // Wavelength for Fresnel zone
        double lambda = 299792458.0 / (audioParams.RadioFrequencyKHz * 1e3);

        for (int i = 0; i < losPoints; i++)
        {
            double t  = i / (double)(losPoints - 1);
            double d  = totalDistance * t;
            losDist[i] = d + xOffset;

            // Distance from TX and RX
            double d1 = d;
            double d2 = totalDistance - d;

            // Earth curvature at this point
            double curvature = (d1 * d2) / (2.0 * effectiveEarthRadius);

            // LOS height (linear interpolation minus curvature)
            double straightLOS = txAbsoluteHeight + (rxAbsoluteHeight - txAbsoluteHeight) * t;
            losHeight[i] = straightLOS - curvature;

            double F1 = (d1 > 0 && d2 > 0) ? Math.Sqrt(lambda * d1 * d2 / (d1 + d2)) : 0;
            fresnelUpper[i] = losHeight[i] + F1;
            fresnelLower[i] = losHeight[i] - F1;
        }

        // Plot curved LOS line
        var losLine = HeightProfilePlot.Plot.Add.ScatterLine(losDist, losHeight);
        losLine.Color       = losColor.WithAlpha(0.8);
        losLine.LineWidth   = 2.0f;
        losLine.LinePattern = LinePattern.Dashed;
        losLine.LegendText  = $"{label} LOS";

        // Plot Fresnel zone boundaries
        var fresnelUpperLine = HeightProfilePlot.Plot.Add.ScatterLine(losDist, fresnelUpper);
        fresnelUpperLine.Color       = fresnelColor.WithAlpha(0.4);
        fresnelUpperLine.LineWidth   = 1.0f;
        fresnelUpperLine.LinePattern = LinePattern.Dotted;
        fresnelUpperLine.LegendText  = $"{label} Fresnel";

        var fresnelLowerLine = HeightProfilePlot.Plot.Add.ScatterLine(losDist, fresnelLower);
        fresnelLowerLine.Color       = fresnelColor.WithAlpha(0.4);
        fresnelLowerLine.LineWidth   = 1.0f;
        fresnelLowerLine.LinePattern = LinePattern.Dotted;

        // Fill between Fresnel zone boundaries
        var fresnelFill = HeightProfilePlot.Plot.Add.FillY(losDist, fresnelLower, fresnelUpper);
        fresnelFill.FillColor = fresnelColor.WithAlpha(0.1);
        fresnelFill.LineWidth = 0;

        // ==================== TX ALTITUDE REFERENCE LINE ====================
        var lineTXAlt = HeightProfilePlot.Plot.Add.HorizontalLine(txAbsoluteHeight);
        lineTXAlt.Text           = $"{label}: {txAbsoluteHeight:0} m";
        lineTXAlt.LabelAlignment = Alignment.LowerLeft;
        lineTXAlt.Color          = txMarkerColor.WithAlpha(0.3);
        lineTXAlt.LinePattern    = LinePattern.Dotted;

        return totalDistance;
    }

    // ===========================
    //  CONTROL EVENT HANDLERS
    // ===========================

    private void OnAltSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateParameters();
    }

    private void ZoomBorder_OnZoomChanged(object sender, ZoomChangedEventArgs e)
    {
        UpdateMarkers();
    }

    private void OnUHFVHFChanged(object? sender, RoutedEventArgs e)
    {
        _radioPlayback.UntuneFrequency(_viewModel.FrequencyKhz, radioSlotId);
        _viewModel.FrequencyKhz = RadioButtonUhf.IsChecked == true ? 513750 : 85000;
        _radioPlayback.TuneFrequency(_viewModel.FrequencyKhz, radioSlotId);
        UpdateParameters();

        if (_viewModel.Signal1Continuous && _signal1Params is not null)
        {
            _radioPlayback.StartStream(_stream1Id, _stream1File, _signal1Params, _viewModel.AmbientNoiseType);
        }

        if (_viewModel.Signal2Continuous && _signal2Params is not null)
        {
            _radioPlayback.StartStream(_stream2Id, _stream2File, _signal2Params, _viewModel.AmbientNoiseType);
        }
    }

    private void OnSignal1PTTChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radioButton) return;
        if (((RadioButton)sender).IsChecked.GetValueOrDefault())
        {
            _radioPlayback.StopStream(_stream1Id).Wait(100);
        }
        else if (_signal1Params is not null)
        {
            _radioPlayback.StartStream(_stream1Id, _stream1File, _signal1Params, _viewModel.AmbientNoiseType);
        }
    }

    private void OnSignal2PTTChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radioButton) return;
        if (((RadioButton)sender).IsChecked.GetValueOrDefault())
        {
            _radioPlayback.StopStream(_stream2Id).Wait(100);
        }
        else if (_signal2Params is not null)
        {
            _radioPlayback.StartStream(_stream2Id, _stream2File, _signal2Params, _viewModel.AmbientNoiseType);
        }
    }

    private void OnPpmSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (ReferenceEquals(sender, Ppm1Slider))
            _viewModel.Ppm1 = (float)e.NewValue;
        else if (ReferenceEquals(sender, Ppm2Slider))
            _viewModel.Ppm2 = (float)e.NewValue;
        UpdateParameters();
    }

    private void OnSquelchSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _radioPlayback.SetSquelchLevel(_viewModel.FrequencyKhz, radioSlotId, (float)(e.NewValue / 10));
    }

    private void OnEnable3dEffectsChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox)
            _radioPlayback.Apply3dEffects = checkBox.IsChecked.GetValueOrDefault();
    }

    private void OnAmbientNoiseTypeChanged(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.Signal1Continuous && _signal1Params is not null)
        {
            _radioPlayback.StartStream(_stream1Id, _stream1File, _signal1Params, _viewModel.AmbientNoiseType);
        }

        if (_viewModel.Signal2Continuous && _signal2Params is not null)
        {
            _radioPlayback.StartStream(_stream2Id, _stream2File, _signal2Params, _viewModel.AmbientNoiseType);
        }
    }
}
