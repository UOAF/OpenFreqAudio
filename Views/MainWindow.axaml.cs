using System;
using System.Collections.Generic;
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
using ReactiveUI;
using ReactiveUI.Avalonia;
using ScottPlot;
using SkiaSharp;
using Color = ScottPlot.Color;
using Path = System.IO.Path;

namespace BMSAudioSim.Views;

public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
{
    private const int HEIGHTMAP_SIZE = 32768;
    private const int PREVIEW_SIZE = 2048;
    private string? _previewImagePath;
    private int clickCount = 0;
    private (int x, int y)? _senderPos;
    private (int x, int y)? _receiverPos;
    private FastPathAudioSim? _fastPathAudioSim;
    static volatile bool _audioPlaying = false;
    private DEMReader? _demReader;
    
    // Marker display
    private Ellipse? _senderMarker;
    private Ellipse? _receiverMarker;
    
    public MainWindow()
    {
        this.WhenActivated(disposables => { /* Handle view activation etc. */ });
        InitializeComponent();
    }

    private async void OnLoadClicked(object? sender, RoutedEventArgs e)
    {
        var storage = StorageProvider;
        var filepickerOptions = new FilePickerOpenOptions
        {
            Title = "Open Heightmap File",
            AllowMultiple = false,
            FileTypeFilter = new []{ 
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
                _fastPathAudioSim = new FastPathAudioSim(_demReader, originX: 0, originY: 0, cellSizeMeters: cellSizeM);

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
            _senderPos = null;
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

        // Alternate between sender and receiver
        if (clickCount % 2 == 0)
        {
            _senderPos = (mapX, mapY);
        }
        else
        {
            _receiverPos = (mapX, mapY);
        }

        clickCount++;
        UpdatePositionDisplay();
        UpdateMarkers();
    }

    // ===== MARKER DISPLAY =====
    
    private void UpdateMarkers()
    {
        if (MarkerCanvas == null) return;
        
        MarkerCanvas.Children.Clear();
        _senderMarker = null;
        _receiverMarker = null;

        if (_senderPos.HasValue)
        {
            var screenPos = ImageToScreenCoordinates(_senderPos.Value.x, _senderPos.Value.y);
            _senderMarker = CreateMarker(screenPos, Brushes.LimeGreen);
            MarkerCanvas.Children.Add(_senderMarker);
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
        if (_fastPathAudioSim is null || !_senderPos.HasValue || !_receiverPos.HasValue)
        {
            return;
        }


        Debug.Assert(ViewModel != null, nameof(ViewModel) + " != null");
        
        var audioParams = _fastPathAudioSim.ComputeAudioForPath(
            _fastPathAudioSim.PixelsToMeters(_senderPos.Value.x),
            _fastPathAudioSim.PixelsToMeters(_senderPos.Value.y),
            txH: ViewModel.TXAltitude,
            _fastPathAudioSim.PixelsToMeters(_receiverPos.Value.x),
            _fastPathAudioSim.PixelsToMeters(_receiverPos.Value.y), 
            ViewModel.RXAltitude, ViewModel.TxDbm, ViewModel.RxDbm, ViewModel.FrequencyMhz * 10e5);

        if (audioParams == null) throw new Exception("audioParams is null"); 
        if (audioParams.TerrainProfile == null) throw new Exception("terrainProfile is null");
            
        UpdateProfileGraph(audioParams.TerrainProfile);

        GainText.Text = audioParams.Gain.ToString();
        LowPassHzText.Text = audioParams.LowpassHz.ToString();
        NoiseLvlText.Text = audioParams.NoiseLevel.ToString();
        DropoutProbText.Text = audioParams.DropoutProb.ToString();
        FlutterDepthText.Text = audioParams.FlutterDepth.ToString();
            
        if (!_audioPlaying)
        {
            RadioPlayback.Start("countdown.ogg", "audio2.ogg", audioParams, ViewModel.SteppedEnabled, ViewModel.SteppedDiffDbm);
            _audioPlaying = true;
        }
        else
        {
            RadioPlayback.UpdateParams(audioParams, ViewModel.SteppedEnabled, ViewModel.SteppedDiffDbm);
        }
    }

    private void UpdatePositionDisplay()
    {
        if (_senderPos.HasValue)
        {
            var pos = _senderPos.Value;
            SenderPosText.Text = $"X: {pos.x}, Y: {pos.y}";
        }
        else
        {
            SenderPosText.Text = "Not set";
        }

        if (_receiverPos.HasValue)
        {
            var pos = _receiverPos.Value;
            ReceiverPosText.Text = $"X: {pos.x}, Y: {pos.y}";
        }
        else
        {
            ReceiverPosText.Text = "Not set";
        }

        if (_senderPos.HasValue && _receiverPos.HasValue)
        {
            UpdateParameters();
        }
    }

    private void UpdateProfileGraph(List<(double dist, double elev)> profile)
    {
        HeightProfilePlot.Plot.Clear();
        if (profile.Count == 0)
            return;

        double[] xValues = new double[profile.Count];
        double[] yValues = new double[profile.Count];

        for (int i = 0; i < profile.Count; i++)
        {
            xValues[i] = profile[i].dist;
            yValues[i] = profile[i].elev;
        }

        var scatter = HeightProfilePlot.Plot.Add.ScatterLine(xValues, yValues);
        scatter.Color = Color.FromHex("#2E86AB").WithAlpha(0.2);
        scatter.LineWidth = 0;
        scatter.FillY = true;
        scatter.FillYValue = yValues.Min();
        
        PixelPadding padding = new(80, 30, 30, 50);
        HeightProfilePlot.Plot.Layout.Fixed(padding);

        var line = HeightProfilePlot.Plot.Add.ScatterLine(xValues, yValues);
        line.Color = Color.FromHex("#2E86AB");
        line.LineWidth = 2.5f;
        line.Smooth = true;

        Debug.Assert(ViewModel != null, nameof(ViewModel) + " != null");
        
        var txAbsoluteHeight = ViewModel.TXAltitude + profile.First().elev;
        var rxAbsoluteHeight = ViewModel.RXAltitude + profile.Last().elev;
        
        var senderMarker = HeightProfilePlot.Plot.Add.Marker(xValues[0], txAbsoluteHeight);
        senderMarker.Color = Color.FromHex("#06D6A0");
        senderMarker.Size = 12;
        senderMarker.Shape = MarkerShape.FilledCircle;

        var receiverMarker = HeightProfilePlot.Plot.Add.Marker(xValues[profile.Count - 1], rxAbsoluteHeight);
        receiverMarker.Color = Color.FromHex("#EF476F");
        receiverMarker.Size = 12;
        receiverMarker.Shape = MarkerShape.FilledCircle;
        
        var lineTXAlt = HeightProfilePlot.Plot.Add.HorizontalLine(txAbsoluteHeight);
        lineTXAlt.Text = $"{txAbsoluteHeight:0} m";
        lineTXAlt.LabelAlignment = Alignment.LowerLeft;
        lineTXAlt.Color = Color.FromHex("#06D6A0");
        
        var lineRXAlt = HeightProfilePlot.Plot.Add.HorizontalLine(rxAbsoluteHeight);
        lineRXAlt.Text = $"{rxAbsoluteHeight:0} m";
        lineRXAlt.LabelAlignment = Alignment.LowerRight;
        lineRXAlt.LabelOppositeAxis = true;
        lineRXAlt.Color = Color.FromHex("#EF476F");
        
        HeightProfilePlot.Plot.Title("Height Profile: Sender to Receiver");
        HeightProfilePlot.Plot.XLabel("Distance (m)");
        HeightProfilePlot.Plot.YLabel("Elevation (m)");

        HeightProfilePlot.Plot.Axes.Title.Label.FontSize = 14;
        HeightProfilePlot.Plot.Axes.Title.Label.Bold = true;

        HeightProfilePlot.Plot.Grid.MajorLineColor = Color.FromHex("#E0E0E0");
        HeightProfilePlot.Plot.Grid.MinorLineColor = Color.FromHex("#F0F0F0");

        HeightProfilePlot.Plot.Axes.AutoScale();
        HeightProfilePlot.Plot.Axes.Margins(0.05, 0.15);

        HeightProfilePlot.Refresh();

        double totalDistance = xValues[xValues.Length - 1] - xValues[0];
        float minHeight = (float)yValues.Min();
        float maxHeight = (float)yValues.Max();
        float avgHeight = (float)yValues.Average();
        float elevationGain = (float)(yValues[yValues.Length - 1] - yValues[0]);

        ProfileInfoText.Text = $"Samples: {profile.Count} | Distance: {totalDistance / 1000:F1} km | " +
                               $"Min: {minHeight:F1} m | Max: {maxHeight:F1} m | " +
                               $"Avg: {avgHeight:F1} m | Gain: {elevationGain:+0.0;-0.0} m";
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
        Debug.Assert(ViewModel != null, nameof(ViewModel) + " != null");
        if (RadioButtonUhf.IsChecked == true)
        {
            ViewModel.FrequencyMhz = 339.75;
        }
        else // VHF
        {
            ViewModel.FrequencyMhz = 513.75;
        }

        ViewModel.SteppedEnabled = Stepped.IsChecked is true;
        UpdateParameters();
    }
}