using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;

namespace HomeworkGuard.UI;

/// <summary>
/// Live camera preview dialog using MediaFrameReader for instant zero-lag photo capture.
/// </summary>
public partial class CameraPreviewWindow : Window
{
    private MediaCapture? _capture;
    private MediaFrameReader? _frameReader;
    private WriteableBitmap? _liveBitmap;
    private readonly object _frameLock = new();
    private bool _isFrozen;
    private bool _isClosing;
    private List<DeviceInformation> _devices = [];

    public byte[]? CapturedJpegBytes { get; private set; }
    public BitmapImage? CapturedPreview { get; private set; }

    public CameraPreviewWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadCamerasAndStartAsync();
        Closing += async (_, _) =>
        {
            _isClosing = true;
            await StopCaptureAsync();
        };
    }

    private async Task LoadCamerasAndStartAsync()
    {
        try
        {
            _devices = (await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture)).ToList();
            if (_devices.Count == 0)
            {
                CameraStatusText.Text = "Bağlı kamera bulunamadı.";
                OverlayText.Text = "Kamera bulunamadı — yerel dosya yüklemeyi kullanabilirsiniz.";
                return;
            }

            if (_devices.Count > 1)
            {
                CameraSelector.ItemsSource = _devices.Select(d => d.Name).ToList();
                CameraSelector.SelectedIndex = 0;
                CameraSelector.Visibility = Visibility.Visible;
            }

            await StartCameraAsync(_devices[0]);
        }
        catch (Exception ex)
        {
            CameraStatusText.Text = $"Kamera hatası: {ex.Message}";
            OverlayText.Text = "Kamera açılamadı.";
        }
    }

    private async void CameraSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isClosing || CameraSelector.SelectedIndex < 0 || CameraSelector.SelectedIndex >= _devices.Count)
            return;

        await StartCameraAsync(_devices[CameraSelector.SelectedIndex]);
    }

    private async Task StartCameraAsync(DeviceInformation device)
    {
        await StopCaptureAsync();
        if (_isClosing) return;

        SnapBtn.IsEnabled = false;
        LoadingOverlay.Visibility = Visibility.Visible;
        OverlayText.Text = "Kamera başlatılıyor...";
        CameraStatusText.Text = $"Kamera hazırlanıyor: {device.Name}";

        try
        {
            _capture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = device.Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu
            };
            await _capture.InitializeAsync(settings);

            var colorSource = _capture.FrameSources.Values
                .FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);

            if (colorSource is null)
            {
                CameraStatusText.Text = "Canlı önizleme akışı desteklenmiyor.";
                OverlayText.Text = "Bu kamera canlı akış sağlamıyor.";
                return;
            }

            _frameReader = await _capture.CreateFrameReaderAsync(colorSource);
            _frameReader.FrameArrived += FrameReader_FrameArrived;
            var status = await _frameReader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                CameraStatusText.Text = $"Kamera başlatılamadı ({status}).";
                OverlayText.Text = "Kamera akışı başlatılamadı.";
                return;
            }

            _isFrozen = false;
            CameraStatusText.Text = $"Canlı Önizleme • {device.Name}";
            LoadingOverlay.Visibility = Visibility.Collapsed;
            SnapBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            CameraStatusText.Text = $"Kamera kullanılamıyor: {ex.Message}";
            OverlayText.Text = "Kamera kullanılamıyor.";
        }
    }

    private void FrameReader_FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (_isFrozen || _isClosing) return;

        using var frame = sender.TryAcquireLatestFrame();
        var rawBitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (rawBitmap is null) return;

        SoftwareBitmap? bgra = null;
        try
        {
            bgra = rawBitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8 &&
                   rawBitmap.BitmapAlphaMode == BitmapAlphaMode.Premultiplied
                ? rawBitmap
                : SoftwareBitmap.Convert(rawBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            int width = bgra.PixelWidth;
            int height = bgra.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[height * stride];
            bgra.CopyToBuffer(pixels.AsBuffer());

            Dispatcher.BeginInvoke(() =>
            {
                if (_isFrozen || _isClosing) return;

                if (_liveBitmap is null || _liveBitmap.PixelWidth != width || _liveBitmap.PixelHeight != height)
                {
                    _liveBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                    LivePreviewImage.Source = _liveBitmap;
                }

                lock (_frameLock)
                {
                    _liveBitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
                }
            });
        }
        finally
        {
            if (bgra is not null && !ReferenceEquals(bgra, rawBitmap))
            {
                bgra.Dispose();
            }
        }
    }

    private void Snap_Click(object sender, RoutedEventArgs e)
    {
        if (_liveBitmap is null) return;

        _isFrozen = true;
        byte[] jpegBytes;
        lock (_frameLock)
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(_liveBitmap));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            jpegBytes = ms.ToArray();
        }

        var preview = new BitmapImage();
        using (var previewMs = new MemoryStream(jpegBytes))
        {
            preview.BeginInit();
            preview.CacheOption = BitmapCacheOption.OnLoad;
            preview.StreamSource = previewMs;
            preview.EndInit();
            preview.Freeze();
        }

        CapturedJpegBytes = jpegBytes;
        CapturedPreview = preview;
        LivePreviewImage.Source = preview;

        CameraStatusText.Text = $"Fotoğraf yakalandı ({jpegBytes.Length / 1024.0:F0} KB) — Netliğini kontrol edip onaylayın.";
        SnapBtn.Visibility = Visibility.Collapsed;
        RetakeBtn.Visibility = Visibility.Visible;
        UsePhotoBtn.Visibility = Visibility.Visible;
    }

    private void Retake_Click(object sender, RoutedEventArgs e)
    {
        CapturedJpegBytes = null;
        CapturedPreview = null;
        _isFrozen = false;
        if (_liveBitmap is not null)
        {
            LivePreviewImage.Source = _liveBitmap;
        }
        CameraStatusText.Text = "Canlı Önizleme — Ödevini kadraja hizalayıp çekebilirsin.";
        RetakeBtn.Visibility = Visibility.Collapsed;
        UsePhotoBtn.Visibility = Visibility.Collapsed;
        SnapBtn.Visibility = Visibility.Visible;
    }

    private void UsePhoto_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async Task StopCaptureAsync()
    {
        if (_frameReader is not null)
        {
            _frameReader.FrameArrived -= FrameReader_FrameArrived;
            try { await _frameReader.StopAsync(); } catch { }
            _frameReader.Dispose();
            _frameReader = null;
        }

        _capture?.Dispose();
        _capture = null;
    }
}
