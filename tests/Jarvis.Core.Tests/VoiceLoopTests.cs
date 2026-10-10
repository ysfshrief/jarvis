using Jarvis.Core.Events;
using Jarvis.Core.Language;
using Jarvis.Core.Tasks;
using Jarvis.Core.Voice;
using Jarvis.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jarvis.Core.Tests;

/// <summary>A microphone that "hears" a burst of sound followed by silence, in 30 ms frames like the real one.</summary>
public sealed class FakeMicrophone : IAudioInput
{
    public bool IsAvailable => true;
    public string? DeviceName => "Test microphone";
    public bool IsCapturing { get; private set; }
    public event Action<float[]>? FrameCaptured;
    public void Start(int deviceIndex) => IsCapturing = true;
    public void Stop() => IsCapturing = false;

    /// <param name="midway">Runs after the speech and before the silence that ends it.</param>
    public void Utter(double seconds = 0.8, Action? midway = null)
    {
        const int frame = 480;
        void Emit(Func<int, float> sample, double s)
        {
            for (var f = 0; f < (int)(s * 16000 / frame); f++)
            {
                var buf = new float[frame];
                for (var i = 0; i < frame; i++) buf[i] = sample(f * frame + i);
                FrameCaptured?.Invoke(buf);
            }
        }
        Emit(_ => 0.001f, 0.3);
        Emit(i => 0.3f * MathF.Sin(i * 0.12f), seconds);
        midway?.Invoke();
        Emit(_ => 0.001f, 1.2);
    }
}

public sealed class ScriptedTranscripts : ISpeechToText
{
    public Queue<string> Lines { get; } = new();
    public int Calls;
    public string EngineName => "scripted";
    public bool IsReady => true;
    public string? StatusMessage => null;
    public Task<Transcript> TranscribeAsync(float[] samples, Lang? hint, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        var text = Lines.Count > 0 ? Lines.Dequeue() : "";
        return Task.FromResult(new Transcript(text, LanguageDetector.Detect(text), 1, TimeSpan.FromMilliseconds(5)));
    }
}

public sealed class RecordingSpeaker : ITextToSpeech
{
    public List<string> Said { get; } = [];
    public string EngineName => "recorder";
    public bool IsAvailable => true;
    public IReadOnlyList<VoiceInfo> Voices => [];
    public Task SpeakAsync(string text, Lang lang, CancellationToken ct) { lock (Said) Said.Add(text); return Task.CompletedTask; }
    public void Stop() { }
}

/// <summary>The voice state machine (wake word, follow-up, push-to-talk, meetings) with the real agent behind it.</summary>
public sealed class VoiceLoopTests : IDisposable
{
    private readonly TestHost _host;
    private readonly FakeMicrophone _mic = new();
    private readonly ScriptedTranscripts _stt = new();
    private readonly RecordingSpeaker _tts = new();
    private readonly VoiceService _voice;

    public VoiceLoopTests()
    {
        _host = new TestHost(s =>
        {
            s.Voice.WakeWordEnabled = true;
            s.Voice.TtsEnabled = true;
            s.Voice.FollowUpSeconds = 8;
            s.Permissions.AutoApproveSensitive = true;
        });
        _voice = new VoiceService(_mic, _stt, _tts, _host.Agent, _host.Settings, _host.Get<IEventBus>(), NullLogger<VoiceService>.Instance);
        _voice.StartAsync(default).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _voice.StopAsync(default).GetAwaiter().GetResult();
        _host.Dispose();
    }

    private static async Task Until(Func<bool> condition, int ms = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(20);
        Assert.True(condition(), "timed out");
    }

    private IReadOnlyList<TaskItem> Tasks => _host.Get<TaskStore>().List(false);

    [Fact]
    public async Task Wake_word_command_runs_and_the_room_is_ignored()
    {
        await Until(() => _mic.IsCapturing && _voice.Status.State == VoiceState.WakeListening);

        _stt.Lines.Enqueue("we should add task call the bank at some point");
        _mic.Utter();
        await Until(() => _stt.Calls == 1);
        await Task.Delay(200);
        Assert.Empty(Tasks); // nobody said "Jarvis"

        _stt.Lines.Enqueue("Jarvis, add task buy milk");
        _mic.Utter();
        await Until(() => Tasks.Any(t => t.Title.Contains("buy milk", StringComparison.OrdinalIgnoreCase)));
        await Until(() => _tts.Said.Count > 0); // the reply is spoken
    }

    [Fact]
    public async Task Follow_up_speech_without_the_wake_word_must_confirm_changes()
    {
        await Until(() => _voice.Status.State == VoiceState.WakeListening);
        _stt.Lines.Enqueue("Jarvis, add task buy milk");
        _mic.Utter();
        await Until(() => Tasks.Count == 1 && _tts.Said.Count == 1);
        await Until(() => _voice.Status.State == VoiceState.WakeListening); // finished speaking (it never listens to itself)

        // Within the follow-up window, someone says something that would change things.
        var dir = Path.Combine(Path.GetTempPath(), "jarvis-voice", Guid.NewGuid().ToString("n"));
        _stt.Lines.Enqueue($"run the command mkdir {dir}");
        _mic.Utter();
        var approval = await _host.AnswerNextApproval(approve: false);
        Assert.Contains("without the wake word", approval.Reason);
        await Task.Delay(300);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task Wake_word_is_off_while_a_meeting_is_recorded()
    {
        await Until(() => _voice.Status.State == VoiceState.WakeListening);
        var events = _host.Get<IEventBus>();
        events.Publish(EventTypes.MeetingChanged, new { id = "m1", recording = true });

        _stt.Lines.Enqueue("Jarvis, add task leak the numbers");
        _mic.Utter();
        await Task.Delay(400);
        Assert.Equal(0, _stt.Calls); // not even transcribed
        Assert.Empty(Tasks);

        events.Publish(EventTypes.MeetingChanged, new { id = "m1", recording = false });
        _mic.Utter();
        await Until(() => Tasks.Count == 1);
    }

    [Fact]
    public async Task Abandoned_push_to_talk_closes_the_microphone()
    {
        _host.Settings.Update(s => s.Voice.WakeWordEnabled = false);
        await Until(() => !_mic.IsCapturing && _voice.Status.State == VoiceState.Idle);
        using var cts = new CancellationTokenSource();
        var listen = _voice.ListenOnceAsync(cts.Token);
        await Until(() => _mic.IsCapturing && _voice.Status.State == VoiceState.Listening);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listen);
        Assert.False(_mic.IsCapturing);
        Assert.Equal(VoiceState.Idle, _voice.Status.State);
    }

    [Fact]
    public async Task Saving_settings_while_a_command_is_heard_does_not_lose_it()
    {
        _host.Settings.Update(s => s.Voice.WakeWordEnabled = false);
        await Until(() => _voice.Status.State == VoiceState.Idle);
        var listen = _voice.ListenOnceAsync(default);
        await Until(() => _mic.IsCapturing);
        _stt.Lines.Enqueue("add task call Ahmed");
        // Someone saves Settings (or JARVIS updates one) while the user is still talking.
        _mic.Utter(midway: () => _host.Settings.Update(s => s.Voice.FollowUpSeconds = 9));
        var result = await listen.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(result);
        Assert.Contains(Tasks, t => t.Title.Contains("call Ahmed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Push_to_talk_handles_one_command()
    {
        _host.Settings.Update(s => s.Voice.WakeWordEnabled = false);
        await Until(() => _voice.Status.State == VoiceState.Idle);
        var listen = _voice.ListenOnceAsync(default);
        await Until(() => _mic.IsCapturing);
        _stt.Lines.Enqueue("add task call Ahmed");
        _mic.Utter();
        var result = await listen.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(result);
        Assert.Contains(Tasks, t => t.Title.Contains("call Ahmed", StringComparison.OrdinalIgnoreCase));
        await Until(() => !_mic.IsCapturing);
    }
}
