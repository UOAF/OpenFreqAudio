using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
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
using ReactiveUI;
using ReactiveUI.Avalonia;
using ScottPlot;
using SkiaSharp;
using Color = ScottPlot.Color;
using Colors = ScottPlot.Colors;
using Path = System.IO.Path;

namespace BMSAudioSim.Views;

public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
{
    private const int HEIGHTMAP_SIZE = 32768;
    private const int PREVIEW_SIZE = 2048;
    private string? _previewImagePath;
    private int clickCount = 0;
    private (int x, int y)? _sender1Pos;
    private (int x, int y)? _sender2Pos;
    private (int x, int y)? _receiverPos;
    private FastPathAudioSim? _fastPathAudioSim;
    static volatile bool _stream1Playing = false;
    static volatile bool _stream2Playing = false;
    private DEMReader? _demReader;
    private AudioParams? _signal1Params;
    private AudioParams? _signal2Params;
    private RadioPlayback _radioPlayback;
    private ILoggerFactory _loggerFactory;
    private MainWindowViewModel _viewModel;

    private readonly string _stream1Id = "stream1";
    private readonly string _stream1File = "countdown.ogg";
    private readonly string _stream2Id = "stream2";
    private readonly string _stream2File = "audio2.ogg";


    // Marker display
    private Ellipse? _sender1Marker;
    private Ellipse? _sender2Marker;
    private Ellipse? _receiverMarker;

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
            Console.Out.WriteLine("PointerPressedEvent");
            _radioPlayback.StartStream(_stream1Id, _stream1File, _signal1Params);
        }, handledEventsToo: true);

        ButtonSignal1Ptt.AddHandler(PointerReleasedEvent, (sender, e) =>
        {
            Console.Out.WriteLine("PointerReleasedEvent");
            _radioPlayback.StopStream(_stream1Id).Wait(300);
        }, handledEventsToo: true);

        ButtonSignal2Ptt.AddHandler(PointerPressedEvent,
            (sender, e) => { _radioPlayback.StartStream(_stream2Id, _stream2File, _signal2Params); },
            handledEventsToo: true);

        ButtonSignal2Ptt.AddHandler(PointerReleasedEvent,
            (sender, e) => { _radioPlayback.StopStream(_stream2Id).Wait(300); }, handledEventsToo: true);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e); 
        _radioPlayback.Initialize();
        _radioPlayback.SetSquelchLevel(_viewModel.FrequencyKhz, ViewModel.Squelch);
        _radioPlayback.SetFrequencyAudioChannel(85000, RadioPlayback.AudioChannel.Right);
        _radioPlayback.SetFrequencyAudioChannel(513750, RadioPlayback.AudioChannel.Left);
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
            _demReader = new DEMReader(file[0].Path.LocalPath, HEIGHTMAP_SIZE, HEIGHTMAP_SIZE);
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
            if (_demReader != null)
                _fastPathAudioSim = new FastPathAudioSim(_demReader, originX: 0, originY: 0, cellSizeMeters: cellSizeM,
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
        if (_demReader == null) return;

        int actualWidth = _demReader.Width;
        int actualHeight = _demReader.Height;

        // Find min and max values for normalization
        float minHeight = float.MaxValue;
        float maxHeight = float.MinValue;

        for (int y = 0; y < actualHeight; y++)
        {
            for (int x = 0; x < actualWidth; x++)
            {
                float h = _demReader.Sample(y, x);
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
                    // Map to actual DEM coordinates
                    int sx = (int)(px * (actualWidth - 1) / (float)(PREVIEW_SIZE - 1));
                    int sy = (int)(py * (actualHeight - 1) / (float)(PREVIEW_SIZE - 1));
                    float height = _demReader.Sample(sy, sx);

                    float normalized = 1.0f - (height - minHeight) / range;
                    SKColor color = GetHeightColor(normalized);

                    int index = py * PREVIEW_SIZE + px;
                    pixels[index] = (uint)color;
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
            // Blue to Cyan
            float t = value / 0.25f;
            r = (byte)(0 * (1 - t) + 0 * t);
            g = (byte)(0 * (1 - t) + 255 * t);
            b = (byte)(255 * (1 - t) + 255 * t);
        }
        else if (value < 0.5f)
        {
            // Cyan to Green
            float t = (value - 0.25f) / 0.25f;
            r = (byte)(0 * (1 - t) + 0 * t);
            g = (byte)(255 * (1 - t) + 200 * t);
            b = (byte)(255 * (1 - t) + 0 * t);
        }
        else if (value < 0.75f)
        {
            // Green to Yellow
            float t = (value - 0.5f) / 0.25f;
            r = (byte)(0 * (1 - t) + 255 * t);
            g = (byte)(200 * (1 - t) + 255 * t);
            b = (byte)(0 * (1 - t) + 0 * t);
        }
        else
        {
            // Yellow to Red
            float t = (value - 0.75f) / 0.25f;
            r = 255;
            g = (byte)(255 * (1 - t) + 0 * t);
            b = 0;
        }

        return new SKColor(r, g, b);
    }

    private async Task LoadPreviewImage(string path)
    {
        await using var stream = File.OpenRead(path);
        HeightmapImage.Source = new Bitmap(stream);
    }

    private void OnImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_demReader == null || HeightmapImage.Source == null) return;

        var point = e.GetPosition(HeightmapImage);
        var imageBounds = HeightmapImage.Bounds;
        var bitmap = HeightmapImage.Source as Bitmap;

        if (bitmap == null) return;

        // Calculate the actual image area considering Uniform stretch
        double imageAspect = (double)bitmap.PixelSize.Width / bitmap.PixelSize.Height;
        double controlAspect = imageBounds.Width / imageBounds.Height;

        double actualWidth, actualHeight, offsetX, offsetY;

        if (controlAspect > imageAspect)
        {
            // Control is wider - image is limited by height
            actualHeight = imageBounds.Height;
            actualWidth = actualHeight * imageAspect;
            offsetX = (imageBounds.Width - actualWidth) / 2;
            offsetY = 0;
        }
        else
        {
            // Control is taller - image is limited by width
            actualWidth = imageBounds.Width;
            actualHeight = actualWidth / imageAspect;
            offsetX = 0;
            offsetY = (imageBounds.Height - actualHeight) / 2;
        }

        // Check if click is within the actual image bounds
        if (point.X < offsetX || point.X > offsetX + actualWidth ||
            point.Y < offsetY || point.Y > offsetY + actualHeight)
        {
            return;
        }

        // Convert to image coordinates (0-PREVIEW_SIZE)
        double relX = (point.X - offsetX) / actualWidth;
        double relY = (point.Y - offsetY) / actualHeight;

        // Convert to heightmap coordinates (0-HEIGHTMAP_SIZE)
        int mapX = (int)(relX * HEIGHTMAP_SIZE);
        int mapY = (int)(relY * HEIGHTMAP_SIZE);

        // Clamp to valid range
        mapX = Math.Clamp(mapX, 0, HEIGHTMAP_SIZE - 1);
        mapY = Math.Clamp(mapY, 0, HEIGHTMAP_SIZE - 1);

        // Cycle through sender1, sender2, receiver
        if (clickCount % 3 == 0)
        {
            _sender1Pos = (mapX, mapY);
        }
        else if (clickCount % 3 == 1)
        {
            _sender2Pos = (mapX, mapY);
        }
        else
        {
            _receiverPos = (mapX, mapY);
        }

        clickCount++;
        UpdatePositionDisplay();
        UpdateMarkers();
        ConfigPanel.IsEnabled = _sender1Pos.HasValue && _sender2Pos.HasValue && _receiverPos.HasValue;
    }

    // ===== MARKER DISPLAY =====

    private void UpdateMarkers()
    {
        if (MarkerCanvas == null) return;

        MarkerCanvas.Children.Clear();
        _sender1Marker = null;
        _sender2Marker = null;
        _receiverMarker = null;

        if (_sender1Pos.HasValue)
        {
            var screenPos = ImageToScreenCoordinates(_sender1Pos.Value.x, _sender1Pos.Value.y);
            _sender1Marker = CreateMarker(screenPos, Brushes.LimeGreen);
            MarkerCanvas.Children.Add(_sender1Marker);
        }

        if (_sender2Pos.HasValue)
        {
            var screenPos = ImageToScreenCoordinates(_sender2Pos.Value.x, _sender2Pos.Value.y);
            _sender2Marker = CreateMarker(screenPos, Brushes.DodgerBlue);
            MarkerCanvas.Children.Add(_sender2Marker);
        }

        if (_receiverPos.HasValue)
        {
            var screenPos = ImageToScreenCoordinates(_receiverPos.Value.x, _receiverPos.Value.y);
            _receiverMarker = CreateMarker(screenPos, Brushes.Red);
            MarkerCanvas.Children.Add(_receiverMarker);
        }
    }

    private Ellipse CreateMarker(Point position, IBrush fill)
    {
        double markerSize = 20d / ZoomBorder.ZoomX;
        var marker = new Ellipse
        {
            Width = markerSize,
            Height = markerSize,
            Fill = fill,
            Stroke = Brushes.White,
            StrokeThickness = 2d / ZoomBorder.ZoomX
        };

        Canvas.SetLeft(marker, position.X - markerSize / 2);
        Canvas.SetTop(marker, position.Y - markerSize / 2);

        return marker;
    }

    private Point ImageToScreenCoordinates(int mapX, int mapY)
    {
        var imageBounds = HeightmapImage.Bounds;
        var bitmap = HeightmapImage.Source as Bitmap;

        if (bitmap == null)
            return new Point(0, 0);

        // Calculate actual image area considering Uniform stretch
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

        // Convert from heightmap to normalized coordinates
        double relX = (double)mapX / HEIGHTMAP_SIZE;
        double relY = (double)mapY / HEIGHTMAP_SIZE;

        // Convert to Canvas coordinate space
        // Add Image's position within the Grid
        double canvasX = HeightmapImage.Bounds.Left + offsetX + relX * actualWidth;
        double canvasY = HeightmapImage.Bounds.Top + offsetY + relY * actualHeight;

        return new Point(canvasX, canvasY);
    }

    // ===== UPDATE METHODS =====

    private void UpdateParameters()
    {
        if (_fastPathAudioSim is null || !_sender1Pos.HasValue || !_sender2Pos.HasValue || !_receiverPos.HasValue)
        {
            return;
        }

        Debug.Assert(ViewModel != null, nameof(ViewModel) + " != null");

        // Path 1: Sender1 -> Receiver
        var audioParams1 = _fastPathAudioSim.CalculateAudioParams(
            _fastPathAudioSim.PixelsToMeters(_sender1Pos.Value.x),
            _fastPathAudioSim.PixelsToMeters(_sender1Pos.Value.y),
            ViewModel.TX1Altitude,
            _fastPathAudioSim.PixelsToMeters(_receiverPos.Value.x),
            _fastPathAudioSim.PixelsToMeters(_receiverPos.Value.y),
            ViewModel.RXAltitude, ViewModel.FrequencyKhz, ViewModel.TxWatts, ViewModel.RxDbm,
            true);

        // Path 2: Sender2 -> Receiver
        var audioParams2 = _fastPathAudioSim.CalculateAudioParams(
            _fastPathAudioSim.PixelsToMeters(_sender2Pos.Value.x),
            _fastPathAudioSim.PixelsToMeters(_sender2Pos.Value.y),
            ViewModel.TX2Altitude,
            _fastPathAudioSim.PixelsToMeters(_receiverPos.Value.x),
            _fastPathAudioSim.PixelsToMeters(_receiverPos.Value.y),
            ViewModel.RXAltitude, ViewModel.FrequencyKhz, ViewModel.TxWatts, ViewModel.RxDbm,
            true);

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

        _radioPlayback.TuneFrequency(ViewModel.FrequencyKhz);
        _radioPlayback.SetSquelchLevel(ViewModel.FrequencyKhz, ViewModel.Squelch);

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

        if (_sender1Pos.HasValue && _sender2Pos.HasValue && _receiverPos.HasValue)
            UpdateParameters();
    }

    private void UpdateProfileGraph(AudioParams audioParams1, AudioParams audioParams2)
    {
        HeightProfilePlot.Plot.Clear();

        Debug.Assert(ViewModel != null, nameof(ViewModel) + " != null");

        PixelPadding padding = new(80, 30, 30, 50);
        HeightProfilePlot.Plot.Layout.Fixed(padding);

        // Pre-compute distances to right-align both paths on the receiver
        double dist1 = audioParams1.TerrainProfile != null && audioParams1.TerrainProfile.Count > 0
            ? audioParams1.TerrainProfile.Last().dist - audioParams1.TerrainProfile.First().dist
            : 0;
        double dist2 = audioParams2.TerrainProfile != null && audioParams2.TerrainProfile.Count > 0
            ? audioParams2.TerrainProfile.Last().dist - audioParams2.TerrainProfile.First().dist
            : 0;
        double maxDist = Math.Max(dist1, dist2);

        // Draw both paths overlaid, offset so receiver endpoints align at maxDist
        dist1 = DrawPathOnGraph(audioParams1, ViewModel.TX1Altitude,
            Color.FromHex("#2E86AB"), Color.FromHex("#06D6A0"), Colors.Green, Colors.LimeGreen, "S1",
            maxDist - dist1);
        dist2 = DrawPathOnGraph(audioParams2, ViewModel.TX2Altitude,
            Color.FromHex("#6B5B95"), Color.FromHex("#4A90D9"), Colors.Blue, Colors.DodgerBlue, "S2",
            maxDist - dist2);

        HeightProfilePlot.Plot.Title("Height Profiles: Sender 1 & 2 to Receiver");
        HeightProfilePlot.Plot.XLabel("Distance (m)");
        HeightProfilePlot.Plot.YLabel("Elevation (m)");

        HeightProfilePlot.Plot.Axes.Title.Label.FontSize = 14;
        HeightProfilePlot.Plot.Axes.Title.Label.Bold = true;

        HeightProfilePlot.Plot.Grid.MajorLineColor = Color.FromHex("#E0E0E0");
        HeightProfilePlot.Plot.Grid.MinorLineColor = Color.FromHex("#F0F0F0");

        // Show legend
        HeightProfilePlot.Plot.ShowLegend(Alignment.UpperRight);

        HeightProfilePlot.Plot.Axes.AutoScale();
        HeightProfilePlot.Plot.Axes.Margins(0.05, 0.15);

        HeightProfilePlot.Refresh();

        ProfileInfoText.Text = $"S1: {dist1 / 1000:F1} km | S2: {dist2 / 1000:F1} km";
    }

    /// <summary>
    /// Draws a single sender-to-receiver path on the profile graph, including terrain fill,
    /// terrain line, TX/RX markers, curved LOS with Earth curvature, and Fresnel zone.
    /// Returns the total distance in meters.
    /// </summary>
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
        scatter.Color = terrainColor.WithAlpha(0.2);
        scatter.LineWidth = 0;
        scatter.FillY = true;
        scatter.FillYValue = yValues.Min();

        // Terrain line
        var line = HeightProfilePlot.Plot.Add.ScatterLine(xValues, yValues);
        line.Color = terrainColor;
        line.LineWidth = 2.5f;
        line.Smooth = true;
        line.LegendText = $"{label} Terrain";

        var txAbsoluteHeight = txAltitude + audioParams.TerrainProfile.First().elev;
        var rxAbsoluteHeight = ViewModel!.RXAltitude + audioParams.TerrainProfile.Last().elev;

        // TX marker
        var senderMarker = HeightProfilePlot.Plot.Add.Marker(xValues[0], txAbsoluteHeight);
        senderMarker.Color = txMarkerColor;
        senderMarker.Size = 12;
        senderMarker.Shape = MarkerShape.FilledCircle;

        // RX marker (shared red color)
        var receiverMarker =
            HeightProfilePlot.Plot.Add.Marker(xValues[audioParams.TerrainProfile.Count - 1], rxAbsoluteHeight);
        receiverMarker.Color = Color.FromHex("#EF476F");
        receiverMarker.Size = 12;
        receiverMarker.Shape = MarkerShape.FilledCircle;

        // ==================== CURVED LOS PATH WITH EARTH CURVATURE ====================
        double totalDistance = xValues[xValues.Length - 1] - xValues[0];

        const double earthRadius = 6378000.0; // meters
        double kAvg =
            FastPathAudioSim.CalculateKAvg(txAbsoluteHeight, rxAbsoluteHeight); // standard atmospheric refraction
        double effectiveEarthRadius = kAvg * earthRadius;

        // Calculate LOS curve
        int losPoints = 200;
        double[] losDist = new double[losPoints];
        double[] losHeight = new double[losPoints];
        double[] fresnelUpper = new double[losPoints];
        double[] fresnelLower = new double[losPoints];

        // Wavelength for Fresnel zone
        double lambda = 299792458.0 / (audioParams.RadioFrequencyKHz * 1e3);

        for (int i = 0; i < losPoints; i++)
        {
            double t = i / (double)(losPoints - 1);
            double d = totalDistance * t;
            losDist[i] = d + xOffset;

            // Distance from TX and RX
            double d1 = d;
            double d2 = totalDistance - d;

            // Earth curvature at this point
            double curvature = (d1 * d2) / (2.0 * effectiveEarthRadius);

            // LOS height (linear interpolation minus curvature)
            double straightLOS = txAbsoluteHeight + (rxAbsoluteHeight - txAbsoluteHeight) * t;
            losHeight[i] = straightLOS - curvature;

            // First Fresnel zone radius at this point
            double F1 = 0;
            if (d1 > 0 && d2 > 0)
            {
                F1 = Math.Sqrt((lambda * d1 * d2) / (d1 + d2));
            }

            fresnelUpper[i] = losHeight[i] + F1;
            fresnelLower[i] = losHeight[i] - F1;
        }

        // Plot curved LOS line
        var losLine = HeightProfilePlot.Plot.Add.ScatterLine(losDist, losHeight);
        losLine.Color = losColor.WithAlpha(0.8);
        losLine.LineWidth = 2.0f;
        losLine.LinePattern = LinePattern.Dashed;
        losLine.LegendText = $"{label} LOS";

        // Plot Fresnel zone boundaries
        var fresnelUpperLine = HeightProfilePlot.Plot.Add.ScatterLine(losDist, fresnelUpper);
        fresnelUpperLine.Color = fresnelColor.WithAlpha(0.4);
        fresnelUpperLine.LineWidth = 1.0f;
        fresnelUpperLine.LinePattern = LinePattern.Dotted;
        fresnelUpperLine.LegendText = $"{label} Fresnel";

        var fresnelLowerLine = HeightProfilePlot.Plot.Add.ScatterLine(losDist, fresnelLower);
        fresnelLowerLine.Color = fresnelColor.WithAlpha(0.4);
        fresnelLowerLine.LineWidth = 1.0f;
        fresnelLowerLine.LinePattern = LinePattern.Dotted;

        // Fill between Fresnel zone boundaries
        var fresnelFill = HeightProfilePlot.Plot.Add.FillY(losDist, fresnelLower, fresnelUpper);
        fresnelFill.FillColor = fresnelColor.WithAlpha(0.1);
        fresnelFill.LineWidth = 0;

        // ==================== TX ALTITUDE REFERENCE LINE ====================
        var lineTXAlt = HeightProfilePlot.Plot.Add.HorizontalLine(txAbsoluteHeight);
        lineTXAlt.Text = $"{label}: {txAbsoluteHeight:0} m";
        lineTXAlt.LabelAlignment = Alignment.LowerLeft;
        lineTXAlt.Color = txMarkerColor.WithAlpha(0.3);
        lineTXAlt.LinePattern = LinePattern.Dotted;

        return totalDistance;
    }

    private void OnAltSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateParameters();
    }

    private void ZoomBorder_OnZoomChanged(object sender, ZoomChangedEventArgs e)
    {
        UpdateMarkers();
    }

    private void ToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        _radioPlayback.UntuneFrequency(ViewModel.FrequencyKhz);
        if (RadioButtonUhf.IsChecked == true)
        {
            ViewModel.FrequencyKhz = 513750;
        }
        else // VHF
        {
            ViewModel.FrequencyKhz = 85000;
        }

        _radioPlayback.TuneFrequency(ViewModel.FrequencyKhz);
        UpdateParameters();
        if (ViewModel.Signal1Continuous)
        {
            _radioPlayback.StartStream(_stream1Id, _stream1File, _signal1Params);
        }

        if (ViewModel.Signal2Continuous)
        {
            _radioPlayback.StartStream(_stream2Id, _stream2File, _signal2Params);
        }
    }

    private void OnSignal1PTTChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radioButton) return;
        if (((RadioButton)sender).IsChecked.GetValueOrDefault())
        {
            _radioPlayback.StopStream(_stream1Id).Wait(100);
        }
        else
        {
            _radioPlayback.StartStream(_stream1Id, _stream1File, _signal1Params);
        }
    }

    private void OnSignal2PTTChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton radioButton) return;
        if (((RadioButton)sender).IsChecked.GetValueOrDefault())
        {
            _radioPlayback.StopStream(_stream2Id).Wait(100);
        }
        else
        {
            _radioPlayback.StartStream(_stream2Id, _stream2File, _signal2Params);
        }
    }

    private void OnSquelchSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _radioPlayback.SetSquelchLevel(_viewModel.FrequencyKhz, (float)(e.NewValue / 10));
    }

    private void OnEnable3dEffectsChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox)
        {
            _radioPlayback.Apply3dEffects = checkBox.IsChecked.GetValueOrDefault();
        }
    }
}