using Jarvis.Core.Agent;
using Jarvis.Core.Language;
using Jarvis.Core.Meetings;
using Jarvis.Core.Tools;
using Jarvis.Core.Voice;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Core.Tests;

public class MeetingNotesTests
{
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 10, 8, 10, 0, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 8, 10, 0, 0)));

    [Fact]
    public void Extracts_decisions_action_items_owners_dates_and_questions()
    {
        const string transcript = """
            [00:00] Thanks everyone for joining the CityCrep review. The proposal looks solid overall.
            [00:20] We decided to keep the retainer at 95,000 EGP per month.
            [00:41] Ahmed will send the signed contract by Thursday. I'll prepare the onboarding plan tomorrow at 10am.
            [01:05] What happens if they need a second branch earlier? Let's go with a quarterly review instead of monthly.
            [01:30] اتفقنا إن التسليم يبقى خلال تسعين يوم. سارة هتبعت الفاتورة بكرة.
            """;
        var n = MeetingNotes.Extract(transcript, Now, ["Ahmed Hassan", "Sara"]);

        Assert.Contains(n.Decisions, d => d.Contains("95,000 EGP"));
        Assert.Contains(n.Decisions, d => d.Contains("quarterly review"));
        Assert.Contains(n.Decisions, d => d.Contains("تسعين يوم"));
        var contract = Assert.Single(n.ActionItems, a => a.Text.Contains("signed contract"));
        Assert.Equal("Ahmed Hassan", contract.Owner);
        Assert.Equal(new DateTime(2026, 10, 8), contract.Due!.Value.Date); // "by Thursday" on a Thursday = today
        var plan = Assert.Single(n.ActionItems, a => a.Text.Contains("onboarding plan"));
        Assert.Equal("you", plan.Owner);
        Assert.Equal(new DateTime(2026, 10, 9, 10, 0, 0), plan.Due!.Value.DateTime);
        Assert.Contains(n.ActionItems, a => a.Text.Contains("الفاتورة") && a.Owner != null);
        Assert.Contains(n.OpenQuestions, q => q.Contains("second branch"));
        Assert.DoesNotContain(n.ActionItems, a => a.Text.Contains("looks solid"));
    }
}

/// <summary>Plays prepared audio frames like a microphone would.</summary>
public sealed class FakeMeetingAudio : IMeetingAudioSource
{
    public bool Available { get; set; } = true;
    public bool IsAvailable => Available;
    public string Description => "Test microphone";
    public bool Running { get; private set; }
    public event Action<float[]>? FrameCaptured;
    public void Start(bool includeSystemAudio) => Running = true;
    public void Stop() => Running = false;

    /// <summary>Emits <paramref name="seconds"/> of a quiet tone (above the silence threshold) in 30 ms frames.</summary>
    public void Play(double seconds)
    {
        var frames = (int)(seconds * 1000 / 30);
        for (var f = 0; f < frames; f++)
        {
            var frame = new float[480];
            for (var i = 0; i < frame.Length; i++) frame[i] = 0.2f * MathF.Sin(2 * MathF.PI * 220 * (f * 480 + i) / 16000f);
            FrameCaptured?.Invoke(frame);
        }
    }
}

/// <summary>Returns prepared sentences for each audio chunk, as a speech engine would.</summary>
public sealed class ScriptedSpeech : ISpeechToText
{
    public Queue<string> Lines { get; } = new();
    public int Calls { get; private set; }
    public string EngineName => "scripted";
    public bool IsReady => true;
    public string? StatusMessage => null;
    public Task<Transcript> TranscribeAsync(float[] samples, Lang? hint, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(new Transcript(Lines.Count > 0 ? Lines.Dequeue() : "", Lang.En, 1, TimeSpan.FromSeconds(samples.Length / 16000.0)));
    }
}

public sealed class MeetingRecorderTests
{
    private static (TestHost Host, FakeMeetingAudio Audio, ScriptedSpeech Speech) Host(bool autoApprove = true)
    {
        var audio = new FakeMeetingAudio();
        var speech = new ScriptedSpeech();
        var host = new TestHost(s => s.Permissions.AutoApproveSensitive = autoApprove, services: sc =>
        {
            sc.AddSingleton<IMeetingAudioSource>(audio);
            sc.AddSingleton<ISpeechToText>(speech);
        });
        return (host, audio, speech);
    }

    [Fact]
    public async Task Recording_always_asks_first_and_nothing_records_when_refused()
    {
        var (host, audio, _) = Host();
        using var _ = host;
        var turn = host.Say("record this meeting");
        var ask = await host.AnswerNextApproval(approve: false);
        await turn;
        Assert.Equal("meeting_record_start", ask.Tool);
        Assert.Equal(RiskLevel.Critical, ask.Risk);
        Assert.Contains("should know", ask.Reason);
        Assert.False(audio.Running);
        Assert.False(host.Get<MeetingRecorder>().IsRecording);
    }

    [Fact]
    public async Task Records_transcribes_in_chunks_and_produces_notes()
    {
        var (host, audio, speech) = Host();
        using var _ = host;
        speech.Lines.Enqueue("Thanks for joining the budget review.");
        speech.Lines.Enqueue("We decided to cut the travel budget by ten percent.");
        speech.Lines.Enqueue("Mona will send the revised sheet tomorrow at 3pm.");

        var turn = host.Say("record this meeting");
        await host.AnswerNextApproval(approve: true);
        var started = await turn;
        Assert.True(started.Success, started.Reply);
        var recorder = host.Get<MeetingRecorder>();
        Assert.True(recorder.IsRecording);
        Assert.True(audio.Running);

        audio.Play(45); // three chunks: 40 s, 40 s and the rest when stopped
        audio.Play(45);
        audio.Play(10);
        var stopped = await host.Say("stop recording");
        Assert.True(stopped.Success, stopped.Reply);
        Assert.False(audio.Running);
        Assert.False(recorder.IsRecording);
        Assert.True(speech.Calls >= 2);

        var m = host.Get<MeetingStore>().List().Single();
        Assert.Equal(MeetingStatus.Done, m.Status);
        Assert.Contains("[00:00] Thanks for joining", m.Transcript);
        Assert.Contains(m.Notes!.Decisions, d => d.Contains("travel budget"));
        Assert.Contains(m.Notes.ActionItems, a => a.Owner == "Mona" && a.Due is not null);
        Assert.Contains("travel budget", stopped.Reply);

        var notes = await host.Say("what did we decide?");
        Assert.Contains("travel budget", notes.Reply);
    }

    [Fact]
    public async Task Without_a_microphone_it_says_so()
    {
        var (host, audio, _) = Host();
        using var _ = host;
        audio.Available = false;
        var turn = host.Say("record this meeting");
        await host.AnswerNextApproval(approve: true);
        var r = await turn;
        Assert.False(r.Success);
        Assert.Contains("Test microphone", r.Reply);
        Assert.Empty(host.Get<MeetingStore>().List());
    }
}
