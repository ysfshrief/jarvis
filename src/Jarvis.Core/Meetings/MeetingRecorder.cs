using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Jarvis.Core.Activity;
using Jarvis.Core.Agenda;
using Jarvis.Core.Events;
using Jarvis.Core.Persistence;
using Jarvis.Core.Settings;
using Jarvis.Core.Voice;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Meetings;

/// <summary>Where meeting audio comes from: on Windows, your microphone mixed with what you hear (16 kHz mono).</summary>
public interface IMeetingAudioSource
{
    bool IsAvailable { get; }
    /// <summary>What's being captured, or why nothing can be ("Microphone + speakers", "No microphone").</summary>
    string Description { get; }
    event Action<float[]>? FrameCaptured;
    void Start(bool includeSystemAudio);
    void Stop();
}

public sealed class NullMeetingAudioSource : IMeetingAudioSource
{
    public bool IsAvailable => false;
    public string Description => "Meeting recording needs JARVIS on Windows with a microphone.";
    public event Action<float[]>? FrameCaptured { add { } remove { } }
    public void Start(bool includeSystemAudio) => throw new InvalidOperationException(Description);
    public void Stop() { }
}

public static class MeetingStatus
{
    public const string Recording = "recording";
    public const string Transcribing = "transcribing";
    public const string Done = "done";
    public const string Failed = "failed";
}

public sealed record Meeting
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? EventId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string Status { get; init; } = MeetingStatus.Recording;
    public string Transcript { get; init; } = "";
    public MeetingNotesResult? Notes { get; init; }
    public double AudioSeconds { get; init; }
    public string? Error { get; init; }
}

public sealed class MeetingStore(JarvisDatabase db, IEventBus events)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Meeting Create(string title, string? eventId)
    {
        var m = new Meeting { Id = Guid.NewGuid().ToString("n"), Title = title, EventId = eventId, StartedAt = DateTimeOffset.Now };
        Exec("INSERT INTO meetings(id, title, event_id, started_at, status) VALUES ($id, $t, $e, $s, 'recording');", c =>
        {
            c.Parameters.AddWithValue("$id", m.Id);
            c.Parameters.AddWithValue("$t", title);
            c.Parameters.AddWithValue("$e", (object?)eventId ?? DBNull.Value);
            c.Parameters.AddWithValue("$s", JarvisDatabase.Format(m.StartedAt));
        });
        Changed(m.Id);
        return m;
    }

    public void AppendTranscript(string id, string text, double audioSeconds)
    {
        Exec("UPDATE meetings SET transcript = transcript || $t, audio_seconds = $a WHERE id = $id;", c =>
        {
            c.Parameters.AddWithValue("$id", id);
            c.Parameters.AddWithValue("$t", text);
            c.Parameters.AddWithValue("$a", audioSeconds);
        });
        Changed(id);
    }

    public void Finish(string id, string status, MeetingNotesResult? notes, string? error = null)
    {
        Exec("UPDATE meetings SET status = $s, notes = $n, error = $err, ended_at = COALESCE(ended_at, $t) WHERE id = $id;", c =>
        {
            c.Parameters.AddWithValue("$id", id);
            c.Parameters.AddWithValue("$s", status);
            c.Parameters.AddWithValue("$n", notes is null ? DBNull.Value : JsonSerializer.Serialize(notes, Json));
            c.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
            c.Parameters.AddWithValue("$t", JarvisDatabase.Now());
        });
        Changed(id);
    }

    public void SetStatus(string id, string status) =>
        Exec("UPDATE meetings SET status = $s, ended_at = CASE WHEN $s = 'recording' THEN ended_at ELSE COALESCE(ended_at, $t) END WHERE id = $id;", c =>
        {
            c.Parameters.AddWithValue("$id", id);
            c.Parameters.AddWithValue("$s", status);
            c.Parameters.AddWithValue("$t", JarvisDatabase.Now());
        });

    public Meeting? Get(string id) => List(1, id).FirstOrDefault();

    public IReadOnlyList<Meeting> List(int limit = 50, string? id = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, title, event_id, started_at, ended_at, status, transcript, notes, audio_seconds, error FROM meetings WHERE ($id IS NULL OR id = $id) ORDER BY started_at DESC LIMIT {Math.Clamp(limit, 1, 500)};";
        cmd.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<Meeting>();
        while (r.Read())
            list.Add(new Meeting
            {
                Id = r.GetString(0), Title = r.GetString(1), EventId = r.IsDBNull(2) ? null : r.GetString(2), StartedAt = DateTimeOffset.Parse(r.GetString(3)),
                EndedAt = r.IsDBNull(4) ? null : DateTimeOffset.Parse(r.GetString(4)), Status = r.GetString(5), Transcript = r.GetString(6),
                Notes = r.IsDBNull(7) ? null : JsonSerializer.Deserialize<MeetingNotesResult>(r.GetString(7), Json), AudioSeconds = r.GetDouble(8),
                Error = r.IsDBNull(9) ? null : r.GetString(9),
            });
        return list;
    }

    public bool Delete(string id)
    {
        var n = Exec("DELETE FROM meetings WHERE id = $id;", c => c.Parameters.AddWithValue("$id", id));
        Changed(id);
        return n > 0;
    }

    private int Exec(string sql, Action<SqliteCommand> bind)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        return cmd.ExecuteNonQuery();
    }

    private void Changed(string id) => events.Publish(EventTypes.MeetingChanged, new { id });
}

/// <summary>
/// Records a meeting only when you start it, shows that it's recording everywhere (status, dashboard,
/// orb), transcribes locally with Whisper in short chunks as it goes, never stores the audio, stops by
/// itself after the configured maximum, and extracts decisions and action items at the end.
/// </summary>
public sealed class MeetingRecorder(
    IMeetingAudioSource audio,
    ISpeechToText stt,
    MeetingStore store,
    AgendaStore agenda,
    ISettingsStore settings,
    IEventBus events,
    ActivityLog activity,
    ILogger<MeetingRecorder> logger) : IDisposable
{
    /// <summary>Audio is transcribed in pieces of about this length, cut at a pause when possible.</summary>
    public static readonly TimeSpan ChunkLength = TimeSpan.FromSeconds(25);
    private const int Rate = IAudioInput.SampleRate;

    private readonly object _gate = new();
    private readonly List<float> _buffer = [];
    private Channel<(float[] Samples, double Offset)>? _chunks;
    private Task? _worker;
    private Timer? _limit;
    private double _samplesSeen;
    private int _quietFrames;

    public Meeting? Current { get; private set; }
    public bool IsRecording => Current is not null;
    public string SourceDescription => audio.Description;
    public bool CanRecord => audio.IsAvailable && stt.IsReady;

    /// <summary>Why recording isn't possible right now, or null.</summary>
    public string? Blocker => !audio.IsAvailable ? audio.Description : !stt.IsReady ? (stt.StatusMessage ?? "Download a speech model first (Settings → Voice).") : null;

    public Meeting Start(string? title)
    {
        lock (_gate)
        {
            if (Current is not null) return Current;
            if (Blocker is { } why) throw new InvalidOperationException(why);
            var now = DateTimeOffset.Now;
            var ev = agenda.Between(now.AddMinutes(-15), now.AddMinutes(15)).FirstOrDefault(e => !e.AllDay);
            var meeting = store.Create(string.IsNullOrWhiteSpace(title) ? ev?.Title ?? $"Meeting {now:ddd d MMM HH:mm}" : title.Trim(), ev?.Id);
            _buffer.Clear();
            _samplesSeen = 0;
            _quietFrames = 0;
            _chunks = Channel.CreateUnbounded<(float[], double)>(new UnboundedChannelOptions { SingleReader = true });
            _worker = Task.Run(() => TranscribeLoopAsync(meeting.Id, _chunks.Reader));
            audio.FrameCaptured += OnFrame;
            try { audio.Start(settings.Current.Meetings.CaptureSystemAudio); }
            catch
            {
                audio.FrameCaptured -= OnFrame;
                _chunks.Writer.TryComplete();
                store.Finish(meeting.Id, MeetingStatus.Failed, null, "The microphone couldn't be opened.");
                throw;
            }
            var max = TimeSpan.FromMinutes(settings.Current.Meetings.MaxMinutes);
            _limit = new Timer(_ => _ = StopAsync("time limit"), null, max, Timeout.InfiniteTimeSpan);
            Current = meeting;
            activity.Record(ActivityKinds.System, $"Meeting recording started: {meeting.Title}", status: "recording", details: audio.Description);
            events.Publish(EventTypes.MeetingChanged, new { id = meeting.Id, recording = true, title = meeting.Title, startedAt = meeting.StartedAt });
            return meeting;
        }
    }

    public async Task<Meeting?> StopAsync(string reason = "stopped")
    {
        Meeting meeting;
        Task? worker;
        lock (_gate)
        {
            if (Current is null) return null;
            meeting = Current;
            audio.FrameCaptured -= OnFrame;
            try { audio.Stop(); } catch (Exception ex) { logger.LogWarning(ex, "Stopping meeting audio failed"); }
            _limit?.Dispose();
            _limit = null;
            Flush(force: true);
            _chunks?.Writer.TryComplete();
            worker = _worker;
            Current = null;
            store.SetStatus(meeting.Id, MeetingStatus.Transcribing);
        }
        events.Publish(EventTypes.MeetingChanged, new { id = meeting.Id, recording = false });
        activity.Record(ActivityKinds.System, $"Meeting recording stopped ({reason}): {meeting.Title}", status: "ok");
        if (worker is not null) await worker.ConfigureAwait(false);
        return store.Get(meeting.Id);
    }

    private void OnFrame(float[] frame)
    {
        lock (_gate)
        {
            if (Current is null) return;
            _buffer.AddRange(frame);
            _samplesSeen += frame.Length;
            var rms = Math.Sqrt(frame.Sum(x => x * x) / Math.Max(1, frame.Length));
            _quietFrames = rms < 0.01 ? _quietFrames + 1 : 0;
            Flush(force: false);
        }
    }

    /// <summary>Sends a chunk for transcription: at a pause once it's long enough, or when it gets too long.</summary>
    private void Flush(bool force)
    {
        var seconds = _buffer.Count / (double)Rate;
        var atPause = _quietFrames >= 10 && seconds >= ChunkLength.TotalSeconds * 0.6;
        if (!force && !atPause && seconds < ChunkLength.TotalSeconds * 1.6) return;
        if (_buffer.Count < Rate / 2) { _buffer.Clear(); return; }
        var offset = (_samplesSeen - _buffer.Count) / Rate;
        _chunks?.Writer.TryWrite(([.. _buffer], offset));
        _buffer.Clear();
    }

    private async Task TranscribeLoopAsync(string meetingId, ChannelReader<(float[] Samples, double Offset)> reader)
    {
        string? error = null;
        try
        {
            await foreach (var (samples, offset) in reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (IsSilent(samples)) continue;
                var t = await stt.TranscribeAsync(samples, null, CancellationToken.None).ConfigureAwait(false);
                var text = t.Text.Trim();
                if (text.Length == 0 || IsNoise(text)) continue;
                var stamp = TimeSpan.FromSeconds(offset);
                store.AppendTranscript(meetingId, $"[{(int)stamp.TotalMinutes:00}:{stamp.Seconds:00}] {text}\n", offset + samples.Length / (double)Rate);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Meeting transcription failed");
            error = ex.Message;
        }
        var m = store.Get(meetingId);
        if (m is null) return;
        var people = m.EventId is { } eid && agenda.Get(eid) is { } ev ? ev.People.ToList() : [];
        var notes = m.Transcript.Length > 0 ? MeetingNotes.Extract(m.Transcript, DateTimeOffset.Now, people) : null;
        store.Finish(meetingId, error is null ? MeetingStatus.Done : MeetingStatus.Failed, notes, error);
        events.Publish(EventTypes.MeetingChanged, new { id = meetingId, recording = false, done = true });
    }

    private static bool IsSilent(float[] s) => s.Length == 0 || Math.Sqrt(s.Sum(x => x * x) / s.Length) < 0.003;

    /// <summary>Whisper's usual hallucinations on silence or music.</summary>
    private static bool IsNoise(string text) =>
        text is "[BLANK_AUDIO]" or "(silence)" or "[Music]" or "[MUSIC]" || text.StartsWith('[') && text.EndsWith(']') && text.Length < 24;

    public void Dispose()
    {
        try { StopAsync("shutting down").GetAwaiter().GetResult(); } catch { }
    }
}

/// <summary>Renders notes for chat and approvals.</summary>
public static class MeetingText
{
    public static string Render(Meeting m, Tools.ToolContext ctx)
    {
        var sb = new StringBuilder();
        var len = m.EndedAt is { } end ? $" ({(int)(end - m.StartedAt).TotalMinutes} min)" : "";
        sb.AppendLine($"“{m.Title}” — {m.StartedAt:ddd d MMM HH:mm}{len}");
        if (m.Notes is not { } n)
        {
            sb.Append(m.Status switch
            {
                MeetingStatus.Recording => ctx.T("Still recording.", "لسه بسجل."),
                MeetingStatus.Transcribing => ctx.T("Still transcribing.", "لسه بكتب الكلام."),
                _ => ctx.T("Nothing was transcribed.", "متكتبش أي كلام."),
            });
            return sb.ToString().TrimEnd();
        }
        if (n.Decisions.Count > 0) sb.AppendLine(ctx.T("Decisions:", "القرارات:")).AppendLine(string.Join("\n", n.Decisions.Select(d => "• " + d)));
        if (n.ActionItems.Count > 0)
            sb.AppendLine(ctx.T("Action items:", "المطلوب:")).AppendLine(string.Join("\n", n.ActionItems.Select(a =>
                $"• {a.Text}{(a.Owner is { } o ? $" — {o}" : "")}{(a.Due is { } d ? $" ({d:ddd d MMM HH:mm})" : "")}")));
        if (n.OpenQuestions.Count > 0) sb.AppendLine(ctx.T("Open questions:", "أسئلة مفتوحة:")).AppendLine(string.Join("\n", n.OpenQuestions.Select(q => "• " + q)));
        if (n.Decisions.Count == 0 && n.ActionItems.Count == 0 && n.KeyPoints.Count > 0)
            sb.AppendLine(ctx.T("Key points (from the transcript):", "أهم النقط (من الكلام):")).AppendLine(string.Join("\n", n.KeyPoints.Select(k => "• " + k)));
        return sb.ToString().TrimEnd();
    }
}
