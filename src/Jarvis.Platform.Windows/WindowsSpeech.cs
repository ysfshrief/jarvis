using Jarvis.Core.Language;
using Jarvis.Core.Settings;
using Jarvis.Core.Voice;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using Windows.Media.SpeechSynthesis;

namespace Jarvis.Platform.Windows;

/// <summary>
/// Speech output with Windows' built-in neural/OneCore voices (offline, free). Arabic needs an
/// Arabic voice installed (Settings → Time &amp; language → Speech → add voices, e.g. "Microsoft Hoda"
/// or "Microsoft Salma" for Egypt). Swappable for Piper or a cloud voice later via <see cref="ITextToSpeech"/>.
/// </summary>
public sealed class WindowsTextToSpeech : ITextToSpeech, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly ILogger<WindowsTextToSpeech> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WaveOutEvent? _output;
    private TaskCompletionSource? _playback;
    private IReadOnlyList<VoiceInfo>? _voices;

    public WindowsTextToSpeech(ISettingsStore settings, ILogger<WindowsTextToSpeech> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public string EngineName => "Windows speech";

    public bool IsAvailable => Voices.Count > 0;

    public IReadOnlyList<VoiceInfo> Voices
    {
        get
        {
            if (_voices is not null) return _voices;
            try
            {
                _voices = SpeechSynthesizer.AllVoices
                    .Select(v => new VoiceInfo(v.Id, v.DisplayName, v.Language, v.Gender.ToString()))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't enumerate Windows voices");
                _voices = [];
            }
            return _voices;
        }
    }

    public async Task SpeakAsync(string text, Lang lang, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var synth = new SpeechSynthesizer();
            var voice = PickVoice(lang);
            if (voice is not null) synth.Voice = voice;
            synth.Options.SpeakingRate = _settings.Current.Voice.Rate;

            using var stream = await synth.SynthesizeTextToStreamAsync(text).AsTask(ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(ms, ct).ConfigureAwait(false);
            ms.Position = 0;

            using var reader = new WaveFileReader(ms);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var output = new WaveOutEvent();
            output.PlaybackStopped += (_, _) => done.TrySetResult();
            output.Init(reader);
            _output = output;
            _playback = done;
            output.Play();
            using (ct.Register(() => output.Stop()))
                await done.Task.ConfigureAwait(false);
        }
        finally
        {
            _output = null;
            _playback = null;
            _gate.Release();
        }
    }

    public void Stop()
    {
        try { _output?.Stop(); } catch { }
        _playback?.TrySetResult();
    }

    private VoiceInformation? PickVoice(Lang lang)
    {
        var all = SpeechSynthesizer.AllVoices;
        var configured = lang == Lang.Ar ? _settings.Current.Voice.VoiceAr : _settings.Current.Voice.VoiceEn;
        if (!string.IsNullOrEmpty(configured))
        {
            var chosen = all.FirstOrDefault(v => v.Id == configured || v.DisplayName == configured);
            if (chosen is not null) return chosen;
        }
        if (lang == Lang.Ar)
        {
            return all.FirstOrDefault(v => v.Language.StartsWith("ar-EG", StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(v => v.Language.StartsWith("ar", StringComparison.OrdinalIgnoreCase));
        }
        // A British male voice suits the character when present; otherwise any English voice.
        return all.FirstOrDefault(v => v.Language.Equals("en-GB", StringComparison.OrdinalIgnoreCase) && v.Gender == VoiceGender.Male)
            ?? all.FirstOrDefault(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase) && v.Gender == VoiceGender.Male)
            ?? all.FirstOrDefault(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            ?? SpeechSynthesizer.DefaultVoice;
    }

    public void Dispose() => Stop();
}

/// <summary>Microphone capture at 16 kHz mono via the Windows multimedia API (NAudio).</summary>
public sealed class WindowsAudioInput(ILogger<WindowsAudioInput> logger) : IAudioInput, IDisposable
{
    private WaveInEvent? _wave;
    private readonly object _gate = new();

    public bool IsAvailable
    {
        get
        {
            try { return WaveInEvent.DeviceCount > 0; }
            catch { return false; }
        }
    }

    public string? DeviceName { get; private set; }

    public bool IsCapturing { get; private set; }

    public event Action<float[]>? FrameCaptured;

    public void Start(int deviceIndex)
    {
        lock (_gate)
        {
            if (IsCapturing) return;
            var count = WaveInEvent.DeviceCount;
            if (count == 0) throw new InvalidOperationException("No microphone is connected.");
            var index = deviceIndex >= 0 && deviceIndex < count ? deviceIndex : 0;
            DeviceName = WaveInEvent.GetCapabilities(index).ProductName;
            _wave = new WaveInEvent
            {
                DeviceNumber = index,
                WaveFormat = new WaveFormat(IAudioInput.SampleRate, 16, 1),
                BufferMilliseconds = 30,
                NumberOfBuffers = 4,
            };
            _wave.DataAvailable += OnData;
            _wave.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) logger.LogWarning(e.Exception, "Microphone stopped unexpectedly");
                IsCapturing = false;
            };
            _wave.StartRecording();
            IsCapturing = true;
            logger.LogInformation("Microphone opened: {Device}", DeviceName);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_wave is null) return;
            try { _wave.StopRecording(); } catch { }
            _wave.DataAvailable -= OnData;
            _wave.Dispose();
            _wave = null;
            IsCapturing = false;
            logger.LogInformation("Microphone closed");
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var samples = new float[e.BytesRecorded / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
        FrameCaptured?.Invoke(samples);
    }

    public void Dispose() => Stop();
}
