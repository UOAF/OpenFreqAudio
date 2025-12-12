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
    private (int x, int y)? _senderPos;
    private (int x, int y)? _receiverPos;
    private FastPathAudioSim? _fastPathAudioSim;
    static volatile bool _stream1Playing = false;
    static volatile bool _stream2Playing = false;
    private DEMReader? _demReader;
    private AudioParams _signal1Params;
    private AudioParams _signal2Params;
    private RadioPlayback _radioPlayback = new(false);
    private ILoggerFactory _loggerFactory;
    private MainWindowViewModel _viewModel;

    private readonly string _stream1Id = "stream1";
    private readonly string _stream1File = "countdown.ogg";
    private readonly string _stream2Id = "stream2";
    private readonly string _stream2File = "audio2.ogg";


    // Marker display
    private Ellipse? _senderMarker;
    private Ellipse? _receiverMarker;

    public MainWindow(MainWindowViewModel viewModel, ILoggerFactory loggerFactory)
    {
        _viewModel = viewModel;
        _loggerFactory = loggerFactory;
        DataContext = viewModel;
        this.WhenActivated(disposables =>
        {
            /* Handle view activation etc. */
        });
        InitializeComponent();
        ButtonSignal1Ptt.AddHandler(Button.PointerPressedEvent, (sender, e) =>
        {
            Console.Out.WriteLine("PointerPressedEvent");
            _radioPlayback.StartStream(_stream1Id, _stream1File, _signal1Params);
        }, handledEventsToo: true);

        ButtonSignal1Ptt.AddHandler(Button.PointerReleasedEvent, (sender, e) =>
        {
            Console.Out.WriteLine("PointerReleasedEvent");
            _radioPlayback.StopStream(_stream1Id).Wait(300);
        }, handledEventsToo: true);

        ButtonSignal2Ptt.AddHandler(Button.PointerPressedEvent,
            (sender, e) => { _radioPlayback.StartStream(_stream2Id, _stream2File, _signal2Params); },
            handledEventsToo: true);

        ButtonSignal2Ptt.AddHandler(Button.PointerReleasedEvent,
            (sender, e) => { _radioPlayback.StopStream(_stream2Id).Wait(300); }, handledEventsToo: true);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _radioPlayback.Initialize(0);
        _radioPlayback.SetSquelchThreshold(_viewModel.FrequencyMhz, (float) SquelchSliderToDb(ViewModel.Squelch));
        _radioPlayback.SetFrequencyAudioChannel(85.0f, RadioPlayback.AudioChannel.Right);
        _radioPlayback.SetFrequencyAudioChannel(513.75f, RadioPlayback.AudioChannel.Left);
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
                _fastPathAudioSim = new FastPathAudioSim(_demReader, originX: 0, originY: 0, cellSizeMeters: cellSizeM, _loggerFactory.CreateLogger<FastPathAudioSim>());

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
        ConfigPanel.IsEnabled = _senderPos.HasValue && _receiverPos.HasValue;
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
        
        _fastPathAudioSim.ReturnAudioParams(_signal1Params);
        
        var audioParams = _fastPathAudioSim.CalculateAudioParams(
            _fastPathAudioSim.PixelsToMeters(_senderPos.Value.x),
            _fastPathAudioSim.PixelsToMeters(_senderPos.Value.y),
            ViewModel.TXAltitude,
            _fastPathAudioSim.PixelsToMeters(_receiverPos.Value.x),
            _fastPathAudioSim.PixelsToMeters(_receiverPos.Value.y),
            ViewModel.RXAltitude, ViewModel.FrequencyMhz, ViewModel.TxDbm, ViewModel.FrequencyMhz <= 200 ? -113 : -107, true);

        if (audioParams == null) throw new Exception("audioParams is null");
        if (audioParams.TerrainProfile == null) throw new Exception("terrainProfile is null");

        UpdateProfileGraph(audioParams);

        GainText.Text = audioParams.Gain.ToString();
        LowPassHzText.Text = audioParams.LowpassHz.ToString();
        NoiseLvlText.Text = audioParams.NoiseLevel.ToString();
        DropoutRateText.Text = audioParams.DropoutRate.ToString();
        DeepFadeRateText.Text = audioParams.DeepFadeRate.ToString();

        _signal1Params = audioParams.Copy();
        _signal2Params = audioParams.Copy();
        
        _radioPlayback.TuneFrequency((float) ViewModel.FrequencyMhz);
        _radioPlayback.SetSquelchThreshold(ViewModel.FrequencyMhz, (float) SquelchSliderToDb(ViewModel.Squelch));
        
        // Convert dB to linear multiplier: 10^(dB/20)
        float linearMultiplier = MathF.Pow(10, ViewModel.SteppedDiffDbm / 20.0f);

        _signal1Params.Gain = audioParams.Gain / linearMultiplier;        
        _signal2Params.Gain = audioParams.Gain * linearMultiplier;        

        _radioPlayback.UpdateStreamParams(_stream1Id, _signal1Params);
        _radioPlayback.UpdateStreamParams(_stream2Id, _signal2Params);
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

   private void UpdateProfileGraph(AudioParams audioParams)
{
    HeightProfilePlot.Plot.Clear();
    if (audioParams.TerrainProfile?.Count == 0)
        return;

    double[] xValues = new double[audioParams.TerrainProfile.Count];
    double[] yValues = new double[audioParams.TerrainProfile.Count];

    for (int i = 0; i < audioParams.TerrainProfile.Count; i++)
    {
        xValues[i] = audioParams.TerrainProfile[i].dist;
        yValues[i] = audioParams.TerrainProfile[i].elev;
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

    var txAbsoluteHeight = ViewModel.TXAltitude + audioParams.TerrainProfile.First().elev;
    var rxAbsoluteHeight = ViewModel.RXAltitude + audioParams.TerrainProfile.Last().elev;

    var senderMarker = HeightProfilePlot.Plot.Add.Marker(xValues[0], txAbsoluteHeight);
    senderMarker.Color = Color.FromHex("#06D6A0");
    senderMarker.Size = 12;
    senderMarker.Shape = MarkerShape.FilledCircle;

    var receiverMarker = HeightProfilePlot.Plot.Add.Marker(xValues[audioParams.TerrainProfile.Count - 1], rxAbsoluteHeight);
    receiverMarker.Color = Color.FromHex("#EF476F");
    receiverMarker.Size = 12;
    receiverMarker.Shape = MarkerShape.FilledCircle;

    // ==================== CURVED LOS PATH WITH EARTH CURVATURE ====================
    double totalDistance = xValues[xValues.Length - 1] - xValues[0];
    
    const double earthRadius = 6378000.0; // meters
    double kAvg = FastPathAudioSim.CalculateKAvg(txAbsoluteHeight, rxAbsoluteHeight); // standard atmospheric refraction
    double effectiveEarthRadius = kAvg * earthRadius;
    
    // Calculate LOS curve
    int losPoints = 200;
    double[] losDist = new double[losPoints];
    double[] losHeight = new double[losPoints];
    double[] fresnelUpper = new double[losPoints];
    double[] fresnelLower = new double[losPoints];
    
    // Wavelength for Fresnel zone
    double lambda = 299792458.0 / (audioParams.RadioFrequencyMHz * 1e6);
    
    for (int i = 0; i < losPoints; i++)
    {
        double t = i / (double)(losPoints - 1);
        double d = totalDistance * t;
        losDist[i] = d;
        
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
    losLine.Color = Colors.Red.WithAlpha(0.8);
    losLine.LineWidth = 2.0f;
    losLine.LinePattern = LinePattern.Dashed;
    losLine.LegendText = "Radio LOS";
    
    // Plot Fresnel zone boundaries
    var fresnelUpperLine = HeightProfilePlot.Plot.Add.ScatterLine(losDist, fresnelUpper);
    fresnelUpperLine.Color = Colors.Orange.WithAlpha(0.4);
    fresnelUpperLine.LineWidth = 1.0f;
    fresnelUpperLine.LinePattern = LinePattern.Dotted;
    fresnelUpperLine.LegendText = "1st Fresnel Zone";
    
    var fresnelLowerLine = HeightProfilePlot.Plot.Add.ScatterLine(losDist, fresnelLower);
    fresnelLowerLine.Color = Colors.Orange.WithAlpha(0.4);
    fresnelLowerLine.LineWidth = 1.0f;
    fresnelLowerLine.LinePattern = LinePattern.Dotted;
    
    // Optional: Fill between Fresnel zone boundaries
    var fresnelFill = HeightProfilePlot.Plot.Add.FillY(losDist, fresnelLower, fresnelUpper);
    fresnelFill.FillColor = Colors.Orange.WithAlpha(0.1);
    fresnelFill.LineWidth = 0;
    
    // ==================== ORIGINAL MARKERS (keep for reference) ====================
    var lineTXAlt = HeightProfilePlot.Plot.Add.HorizontalLine(txAbsoluteHeight);
    lineTXAlt.Text = $"{txAbsoluteHeight:0} m";
    lineTXAlt.LabelAlignment = Alignment.LowerLeft;
    lineTXAlt.Color = Color.FromHex("#06D6A0").WithAlpha(0.3); // Make more transparent
    lineTXAlt.LinePattern = LinePattern.Dotted;

    var lineRXAlt = HeightProfilePlot.Plot.Add.HorizontalLine(rxAbsoluteHeight);
    lineRXAlt.Text = $"{rxAbsoluteHeight:0} m";
    lineRXAlt.LabelAlignment = Alignment.LowerRight;
    lineRXAlt.LabelOppositeAxis = true;
    lineRXAlt.Color = Color.FromHex("#EF476F").WithAlpha(0.3); // Make more transparent
    lineRXAlt.LinePattern = LinePattern.Dotted;

    HeightProfilePlot.Plot.Title("Height Profile: Sender to Receiver");
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

    float minHeight = (float)yValues.Min();
    float maxHeight = (float)yValues.Max();
    float avgHeight = (float)yValues.Average();
    float elevationGain = (float)(yValues[yValues.Length - 1] - yValues[0]);

    ProfileInfoText.Text = $"Samples: {audioParams.TerrainProfile.Count} | Distance: {totalDistance / 1000:F1} km | " +
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

        _radioPlayback.StopAll().Wait(500);
        
        _radioPlayback.UntuneFrequency((float) ViewModel.FrequencyMhz);
        if (RadioButtonUhf.IsChecked == true)
        {
            ViewModel.FrequencyMhz = 513.75;
        }
        else // VHF
        {
            ViewModel.FrequencyMhz = 85.0;
        }

        _radioPlayback.TuneFrequency((float) ViewModel.FrequencyMhz);
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
        var squelchValue = SquelchSliderToDb(e.NewValue);
        _radioPlayback.SetSquelchThreshold(_viewModel.FrequencyMhz, (float)squelchValue);
    }

    private double SquelchSliderToDb(double sliderValue)
    {
        double dB = -40 + (sliderValue * 4.0);     // map 0–10 to -40 dB → 0 dB
        return Math.Pow(10, dB / 20.0);   // convert dB to linear
    }
}