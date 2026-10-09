using Jarvis.Core.Meetings;

namespace Jarvis.Core.Tools.Builtin;

public sealed class MeetingRecordStartTool(MeetingRecorder recorder) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "meeting_record_start",
        Category = "meetings",
        Risk = RiskLevel.Critical,
        Description = "Start recording and transcribing a meeting (microphone and what the user hears). Always asks the user first; recording is shown on screen until stopped.",
        Parameters = [new("title", "string", "Meeting name (default: the calendar event happening now).")],
    };

    // Recording other people is a privacy decision the user must make every time.
    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx) =>
        new(RiskLevel.Critical, $"Record the meeting{(args.GetString("title") is { Length: > 0 } t ? $" “{t}”" : "")}",
            "Everyone in the meeting should know it's being recorded. Audio stays on this PC and is deleted after transcription.");

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        if (recorder.IsRecording) return Task.FromResult(ToolResult.Ok(ctx.T($"Already recording “{recorder.Current!.Title}”.", $"بسجل بالفعل «{recorder.Current!.Title}».")));
        try
        {
            var m = recorder.Start(args.GetString("title"));
            return Task.FromResult(ToolResult.Ok(
                ctx.T($"Recording “{m.Title}” ({recorder.SourceDescription}). Say “stop recording” when you're done.", $"بسجل «{m.Title}» ({recorder.SourceDescription}). قول «وقف التسجيل» لما تخلص."),
                new { meetingId = m.Id, m.Title }));
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(ToolResult.Fail(ctx.T($"I can't record: {ex.Message}", $"مش هقدر أسجل: {ex.Message}"), status: ToolStatus.NotFound));
        }
    }
}

public sealed class MeetingRecordStopTool(MeetingRecorder recorder) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "meeting_record_stop",
        Category = "meetings",
        Description = "Stop the meeting recording and produce the notes (decisions, action items, open questions).",
    };

    protected override string Describe(ToolArgs args) => "Stop recording the meeting";

    public override async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var m = await recorder.StopAsync().ConfigureAwait(false);
        if (m is null) return ToolResult.Ok(ctx.T("Nothing is being recorded.", "مفيش حاجة بتتسجل."));
        return ToolResult.Ok(ctx.T("Stopped recording. ", "وقفت التسجيل. ") + MeetingText.Render(m, ctx), new { meetingId = m.Id, m.Notes });
    }
}

public sealed class MeetingNotesTool(MeetingStore store) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "meeting_notes",
        Category = "meetings",
        ReadsUntrustedContent = true,
        Description = "Notes of a recorded meeting (the latest, or one matching a name): decisions, action items with owners and dates, open questions, and the transcript.",
        Parameters = [new("meeting", "string", "Which meeting (name); default the latest.")],
    };

    protected override string Describe(ToolArgs args) => $"Notes of {args.GetString("meeting") ?? "the last meeting"}";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var q = args.GetString("meeting");
        var list = store.List(50);
        var m = q is { Length: > 0 }
            ? list.FirstOrDefault(x => x.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
            : list.FirstOrDefault(x => x.Status != MeetingStatus.Recording) ?? list.FirstOrDefault();
        if (m is null) return Task.FromResult(ToolResult.Fail(ctx.T("I haven't recorded any meeting yet. Say “record this meeting” to start.", "لسه مسجلتش أي اجتماع. قول «سجل الاجتماع» عشان أبدأ."), status: ToolStatus.NotFound));
        return Task.FromResult(ToolResult.Ok(MeetingText.Render(m, ctx), new
        {
            meetingId = m.Id, m.Title, m.StartedAt, m.Status, m.Notes,
            untrustedContent = m.Transcript.Length > 8000 ? m.Transcript[..8000] + "…" : m.Transcript,
        }));
    }
}
