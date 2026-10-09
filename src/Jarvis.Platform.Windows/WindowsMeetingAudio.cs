using Jarvis.Core.Meetings;
using Jarvis.Core.Voice;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Jarvis.Platform.Windows;

/// <summary>
/// Meeting audio on Windows: the microphone (your voice) mixed with a loopback capture of the default
/// speakers (the other people on a Teams/Zoom/Meet call), as 16 kHz mono. Both are opened only while a
/// recording the user started is running.
/// </summary>
public sealed class WindowsMeetingAudioSource(ILogger<WindowsMeetingAudioSource> logger) : IMeetingAudioSource, IDisposable
{
    private const int Rate = IAudioInput.SampleRate;
    private readonly object _gate = new();
    private WaveInEvent? _mic;
    private WasapiLoopbackCapture? _loopback;
    private BufferedWaveProvider? _loopBuffer;
    private ISampleProvider? _loop16k;
    private string _description = "";

    public bool IsAvailable
    {
        get
        {
            try { return WaveInEvent.DeviceCount > 0; }
            catch { return false; }
        }
    }

    public string Description => _description.Length > 0 ? _description : IsAvailable ? "Microphone" : "No microphone is connected.";

    public event Action<float[]>? FrameCaptured;

    public void Start(bool includeSystemAudio)
    {
        lock (_gate)
        {
            if (_mic is not null) return;
            if (WaveInEvent.DeviceCount == 0) throw new InvalidOperationException("No microphone is connected.");
            _mic = new WaveInEvent { DeviceNumber = 0, WaveFormat = new WaveFormat(Rate, 16, 1), BufferMilliseconds = 30, NumberOfBuffers = 6 };
            _mic.DataAvailable += OnMic;
            var parts = new List<string> { WaveInEvent.GetCapabilities(0).ProductName };

            if (includeSystemAudio)
            {
                try
                {
                    _loopback = new WasapiLoopbackCapture();
                    _loopBuffer = new BufferedWaveProvider(_loopback.WaveFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(5), ReadFully = false };
                    // Speakers run at 44.1/48 kHz stereo float: fold to mono, then resample to 16 kHz.
                    ISampleProvider samples = _loopBuffer.ToSampleProvider();
                    if (samples.WaveFormat.Channels > 1) samples = new StereoToMonoSampleProvider(samples.WaveFormat.Channels == 2 ? samples : new MultiplexingSampleProvider([samples], 2)) { LeftVolume = 0.5f, RightVolume = 0.5f };
                    _loop16k = new WdlResamplingSampleProvider(samples, Rate);
                    _loopback.DataAvailable += (_, e) => _loopBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    _loopback.StartRecording();
                    parts.Add("speakers");
                }
                catch (Exception ex)
                {
                    // No output device (or exclusive mode): record the microphone alone and say so.
                    logger.LogWarning(ex, "System audio capture unavailable; recording the microphone only");
                    _loopback?.Dispose();
                    _loopback = null;
                    _loop16k = null;
                }
            }
            _mic.StartRecording();
            _description = string.Join(" + ", parts);
            logger.LogInformation("Meeting audio started: {Sources}", _description);
        }
    }

    private void OnMic(object? sender, WaveInEventArgs e)
    {
        var n = e.BytesRecorded / 2;
        var frame = new float[n];
        for (var i = 0; i < n; i++) frame[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
        var loop = _loop16k;
        if (loop is not null)
        {
            var other = new float[n];
            int read;
            try { read = loop.Read(other, 0, n); } catch { read = 0; }
            for (var i = 0; i < read; i++) frame[i] = Math.Clamp(frame[i] + other[i], -1f, 1f);
        }
        FrameCaptured?.Invoke(frame);
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_mic is not null)
            {
                try { _mic.StopRecording(); } catch { }
                _mic.DataAvailable -= OnMic;
                _mic.Dispose();
                _mic = null;
            }
            if (_loopback is not null)
            {
                try { _loopback.StopRecording(); } catch { }
                _loopback.Dispose();
                _loopback = null;
            }
            _loop16k = null;
            _loopBuffer = null;
            logger.LogInformation("Meeting audio stopped");
        }
    }

    public void Dispose() => Stop();
}
