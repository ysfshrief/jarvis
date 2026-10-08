using System.Text.RegularExpressions;
using System.Threading.Channels;
using Jarvis.Core.Agent;
using Jarvis.Core.Events;
using Jarvis.Core.Language;
using Jarvis.Core.Settings;
using Jarvis.Core.Voice;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jarvis.Voice;

public sealed record VoiceStatus(
    VoiceState State,
    bool MicrophoneActive,
    bool WakeWordEnabled,
    bool SttReady,
    string? SttMessage,
    string SttEngine,
    bool TtsAvailable,
    string TtsEngine,
    bool AudioAvailable,
    string? AudioDevice,
    string? LastTranscript);

/// <summary>
/// The voice loop: microphone → speech segmentation → (wake word) → Whisper → agent → speech.
/// Runs inside the always-on runtime. The microphone is only open while wake-word mode is
/// enabled or during push-to-talk, and every state change is published so the orb can show it.
/// </summary>
public sealed partial class VoiceService : BackgroundService
{
    private enum Mode { Off, Wake, Command }

    private readonly IAudioInput _audio;
    private readonly ISpeechToText _stt;
    private readonly ITextToSpeech _tts;
    private readonly AgentOrchestrator _agent;
    private readonly ISettingsStore _settings;
    private readonly IEventBus _events;
    private readonly ILogger<VoiceService> _logger;
    private readonly Channel<(float[] Samples, Mode Mode)> _utterances = Channel.CreateBounded<(float[], Mode)>(4);
    private readonly object _gate = new();

    private SpeechSegmenter _segmenter;
    private Mode _mode = Mode.Off;
    private VoiceState _state = VoiceState.Idle;
    private DateTimeOffset _commandDeadline;
    private DateTimeOffset _followUpUntil;
    private TaskCompletionSource<AgentTurnResult?>? _pushToTalk;
    private bool _paused;
    private string? _lastTranscript;

    public VoiceService(IAudioInput audio, ISpeechToText stt, ITextToSpeech tts, AgentOrchestrator agent,
        ISettingsStore settings, IEventBus events, ILogger<VoiceService> logger)
    {
        _audio = audio;
        _stt = stt;
        _tts = tts;
        _agent = agent;
        _settings = settings;
        _events = events;
        _logger = logger;
        _segmenter = NewSegmenter(settings.Current.Voice);
        _audio.FrameCaptured += OnFrame;
        _settings.Changed += _ => ApplySettings();
        // Typed requests are spoken too when the user turned off "only speak for voice input".
        _events.Subscribe(e =>
        {
            if (e.Type != EventTypes.TurnCompleted || e.Data is not AgentTurnResult r || r.Source == InputSource.Voice) return;
            var v = _settings.Current.Voice;
            if (v.TtsEnabled && !v.SpeakOnlyForVoiceInput)
                _ = SpeakAsync(r.Reply, r.Lang == "ar" ? Lang.Ar : Lang.En, CancellationToken.None);
        });
    }

    public VoiceStatus Status => new(
        _state, _audio.IsCapturing, _settings.Current.Voice.WakeWordEnabled, _stt.IsReady, _stt.StatusMessage, _stt.EngineName,
        _tts.IsAvailable, _tts.EngineName, _audio.IsAvailable, _audio.DeviceName, _lastTranscript);

    public bool IsPaused => _paused;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ApplySettings();
        await foreach (var (samples, mode) in _utterances.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try { await ProcessUtteranceAsync(samples, mode, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Voice processing failed");
                CompletePushToTalk(null);
                SetState(_mode == Mode.Wake ? VoiceState.WakeListening : VoiceState.Idle);
            }
        }
    }

    /// <summary>Push-to-talk: listen for one command now, handle it, and speak the answer.</summary>
    public async Task<AgentTurnResult?> ListenOnceAsync(CancellationToken ct)
    {
        EnsureUsable();
        TaskCompletionSource<AgentTurnResult?> tcs;
        lock (_gate)
        {
            _pushToTalk?.TrySetResult(null);
            tcs = _pushToTalk = new TaskCompletionSource<AgentTurnResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _tts.Stop();
            StartCommandWindow(TimeSpan.FromSeconds(_settings.Current.Voice.MaxUtteranceSeconds + 5));
        }
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task.ConfigureAwait(false);
    }

    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
            _tts.Stop();
            StopCapture();
            _mode = Mode.Off;
            SetState(VoiceState.Paused);
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
            ApplySettings();
        }
    }

    public void StopSpeaking() => _tts.Stop();

    /// <summary>Speak text if TTS is available. Used for replies and spoken notifications.</summary>
    public async Task SpeakAsync(string text, Lang lang, CancellationToken ct)
    {
        if (!_tts.IsAvailable || !_settings.Current.Voice.TtsEnabled || _paused || string.IsNullOrWhiteSpace(text)) return;
        var previous = _state;
        var wasCapturing = _audio.IsCapturing;
        // Don't let JARVIS hear (and react to) its own voice.
        if (wasCapturing) StopCapture();
        SetState(VoiceState.Speaking);
        try
        {
            await _tts.SpeakAsync(ForSpeech(text), lang, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Speech output failed");
        }
        finally
        {
            lock (_gate)
            {
                if (!_paused && (wasCapturing || _mode != Mode.Off)) StartCapture();
                SetState(_mode switch
                {
                    Mode.Command => VoiceState.Listening,
                    Mode.Wake => VoiceState.WakeListening,
                    _ => previous == VoiceState.Speaking ? VoiceState.Idle : previous is VoiceState.Thinking ? VoiceState.Idle : previous,
                });
            }
        }
    }

    private async Task ProcessUtteranceAsync(float[] samples, Mode mode, CancellationToken ct)
    {
        SetState(VoiceState.Transcribing);
        var s = _settings.Current.Voice;
        var transcript = await _stt.TranscribeAsync(samples, null, ct).ConfigureAwait(false);
        var text = transcript.Text.Trim();
        _lastTranscript = text;
        _events.Publish(EventTypes.VoiceTranscript, new { text, language = transcript.Language?.ToString().ToLowerInvariant(), mode = mode.ToString(), ms = (long)transcript.Duration.TotalMilliseconds });

        if (text.Length == 0 || IsNoise(text))
        {
            if (mode == Mode.Command && _pushToTalk is not null && DateTimeOffset.Now < _commandDeadline)
            {
                SetState(VoiceState.Listening); // keep waiting for the real command
                return;
            }
            CompletePushToTalk(null);
            ReturnToBaseMode();
            return;
        }

        string command;
        if (mode == Mode.Wake && DateTimeOffset.Now > _followUpUntil)
        {
            if (!WakeWordMatcher.TryMatch(text, s.WakeWords, out command))
            {
                SetState(VoiceState.WakeListening);
                return; // ordinary conversation in the room; ignore
            }
            if (command.Length < 2)
            {
                // Just "Jarvis": acknowledge and listen for the command.
                var lang = transcript.Language ?? Lang.En;
                lock (_gate) StartCommandWindow(TimeSpan.FromSeconds(s.MaxUtteranceSeconds));
                await SpeakAsync(lang == Lang.Ar ? "أيوه يا فندم؟" : "Yes, Sir?", lang, ct).ConfigureAwait(false);
                return;
            }
        }
        else
        {
            command = WakeWordMatcher.TryMatch(text, s.WakeWords, out var stripped) && stripped.Length > 1 ? stripped : text;
        }

        SetState(VoiceState.Thinking);
        var result = await _agent.HandleAsync(new UserInput(command, null, InputSource.Voice), ct).ConfigureAwait(false);
        CompletePushToTalk(result);

        lock (_gate)
        {
            _followUpUntil = DateTimeOffset.Now.AddSeconds(s.FollowUpSeconds);
            ReturnToBaseMode();
        }
        await SpeakAsync(result.Reply, result.Lang == "ar" ? Lang.Ar : Lang.En, ct).ConfigureAwait(false);
        lock (_gate)
        {
            // Keep the mic open briefly for a natural follow-up ("and close it after").
            if (s.FollowUpSeconds > 0 && !_paused && _mode == Mode.Wake) _followUpUntil = DateTimeOffset.Now.AddSeconds(s.FollowUpSeconds);
        }
    }

    private void OnFrame(float[] frame)
    {
        float[]? utterance;
        Mode mode;
        lock (_gate)
        {
            if (_mode == Mode.Off || _state is VoiceState.Speaking) return;
            if (_mode == Mode.Command && DateTimeOffset.Now > _commandDeadline && !_segmenter.InSpeech)
            {
                _segmenter.Reset();
                CompletePushToTalk(null);
                ReturnToBaseMode();
                return;
            }
            utterance = _segmenter.Process(frame);
            mode = _mode;
        }
        if (utterance is not null && !_utterances.Writer.TryWrite((utterance, mode)))
            _logger.LogWarning("Dropped an utterance: voice processing is busy");
    }

    private void ApplySettings()
    {
        lock (_gate)
        {
            _segmenter = NewSegmenter(_settings.Current.Voice);
            if (_paused) return;
            if (!_audio.IsAvailable || !_stt.IsReady)
            {
                StopCapture();
                _mode = Mode.Off;
                SetState(VoiceState.Unavailable);
                return;
            }
            if (_mode == Mode.Command) return; // finish the current command first
            ReturnToBaseMode();
        }
    }

    private void ReturnToBaseMode()
    {
        if (_paused) return;
        if (_settings.Current.Voice.WakeWordEnabled && _audio.IsAvailable && _stt.IsReady)
        {
            _mode = Mode.Wake;
            StartCapture();
            SetState(VoiceState.WakeListening);
        }
        else
        {
            _mode = Mode.Off;
            StopCapture();
            SetState(_audio.IsAvailable && _stt.IsReady ? VoiceState.Idle : VoiceState.Unavailable);
        }
    }

    private void StartCommandWindow(TimeSpan duration)
    {
        _mode = Mode.Command;
        _commandDeadline = DateTimeOffset.Now + duration;
        _segmenter.Reset();
        StartCapture();
        SetState(VoiceState.Listening);
    }

    private void StartCapture()
    {
        if (_audio.IsCapturing || !_audio.IsAvailable) return;
        try { _audio.Start(_settings.Current.Voice.InputDeviceIndex); }
        catch (Exception ex) { _logger.LogWarning(ex, "Microphone start failed"); }
    }

    private void StopCapture()
    {
        if (_audio.IsCapturing) _audio.Stop();
    }

    private void CompletePushToTalk(AgentTurnResult? result)
    {
        var p = _pushToTalk;
        _pushToTalk = null;
        p?.TrySetResult(result);
    }

    private void EnsureUsable()
    {
        if (_paused) throw new InvalidOperationException("Voice is paused.");
        if (!_audio.IsAvailable) throw new InvalidOperationException("No microphone is available on this system.");
        if (!_stt.IsReady) throw new InvalidOperationException(_stt.StatusMessage ?? "Speech recognition isn't ready.");
    }

    private void SetState(VoiceState state)
    {
        if (_state == state) return;
        _state = state;
        _events.Publish(EventTypes.VoiceState, new { state, microphoneActive = _audio.IsCapturing });
    }

    private static SpeechSegmenter NewSegmenter(VoiceSettings v) =>
        new(16000, v.VadSensitivity, maxSeconds: v.MaxUtteranceSeconds);

    private static bool IsNoise(string text) =>
        text.Length < 2 || NoiseRegex().IsMatch(text);

    /// <summary>Strip markdown and code so the text sounds natural when spoken.</summary>
    internal static string ForSpeech(string text)
    {
        var t = CodeBlock().Replace(text, " ");
        t = Link().Replace(t, "$1");
        t = Markdown().Replace(t, "");
        t = Bullets().Replace(t, ". ");
        return Spaces().Replace(t, " ").Trim();
    }

    [GeneratedRegex(@"^(?:you|thank you\.?|thanks for watching!?|bye\.?|\.+|شكرا\.?|ترجمة.*|اشترك.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseRegex();

    [GeneratedRegex(@"```[\s\S]*?```")]
    private static partial Regex CodeBlock();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"[*_#`>]+")]
    private static partial Regex Markdown();

    [GeneratedRegex(@"[:.]?\s*\n\s*[•\-]\s*")]
    private static partial Regex Bullets();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
