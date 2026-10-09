using Jarvis.Core.Vision;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace Jarvis.Platform.Windows;

/// <summary>One still photo through the Windows camera API, opened just for the shot and closed right after.</summary>
public sealed class WindowsCamera : ICamera
{
    private string? _name;
    private bool? _available;

    public bool IsAvailable
    {
        get
        {
            if (_available is { } a) return a;
            try
            {
                var devices = DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask().GetAwaiter().GetResult();
                _name = devices.FirstOrDefault()?.Name;
                _available = devices.Count > 0;
            }
            catch { _available = false; }
            return _available.Value;
        }
    }

    public string? Name => IsAvailable ? _name : null;

    public async Task<byte[]> CaptureJpegAsync(CancellationToken ct)
    {
        using var capture = new MediaCapture();
        await capture.InitializeAsync(new MediaCaptureInitializationSettings { StreamingCaptureMode = StreamingCaptureMode.Video, PhotoCaptureSource = PhotoCaptureSource.Auto }).AsTask(ct);
        using var stream = new InMemoryRandomAccessStream();
        await capture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), stream).AsTask(ct);
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size).AsTask(ct);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
