using System.IO;
using System.Windows.Media.Imaging;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace HomeworkGuard.UI.Services;

/// <summary>
/// Captures a photo from an attached webcam using Windows.Media.Capture.MediaCapture.
/// Gracefully reports availability when running in a VM or PC without a camera.
/// </summary>
public sealed class WebcamCaptureHelper : IAsyncDisposable
{
    private MediaCapture? _capture;
    private bool _initialized;

    public bool IsCameraAvailable { get; private set; }
    public string StatusMessage { get; private set; } = "Kamera henüz başlatılmadı.";

    public async Task<bool> InitializeAsync()
    {
        if (_initialized) return IsCameraAvailable;

        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            if (devices.Count == 0)
            {
                IsCameraAvailable = false;
                StatusMessage = "Kamera bulunamadı — yerel dosya yüklemeyi kullanabilirsiniz.";
                _initialized = true;
                return false;
            }

            _capture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = devices[0].Id,
                StreamingCaptureMode = StreamingCaptureMode.Video
            };
            await _capture.InitializeAsync(settings);
            IsCameraAvailable = true;
            StatusMessage = $"Kamera hazır: {devices[0].Name}";
            _initialized = true;
            return true;
        }
        catch (Exception ex)
        {
            IsCameraAvailable = false;
            StatusMessage = $"Kamera kullanılamıyor ({ex.Message}) — dosya yüklemeyi kullanabilirsiniz.";
            _initialized = true;
            return false;
        }
    }

    public async Task<(byte[] JpegBytes, BitmapImage Preview)?> CapturePhotoAsync()
    {
        if (!await InitializeAsync() || _capture is null) return null;

        using var stream = new InMemoryRandomAccessStream();
        await _capture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), stream);
        stream.Seek(0);

        byte[] bytes = new byte[stream.Size];
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
        }

        var bitmap = new BitmapImage();
        using (var ms = new MemoryStream(bytes))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
        }

        return (bytes, bitmap);
    }

    public ValueTask DisposeAsync()
    {
        _capture?.Dispose();
        _capture = null;
        return ValueTask.CompletedTask;
    }
}
