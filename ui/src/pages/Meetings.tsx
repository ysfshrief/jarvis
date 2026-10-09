import { useState } from "react";
import { ArrowLeft, CheckSquare, Circle, ListChecks, Mic2, Square, Trash2 } from "lucide-react";
import { del, get, post, type Meeting, type MeetingSummary } from "../api";
import { useStatus } from "../App";
import { useEvents } from "../events";
import { ApprovalCard, useApprovals } from "../components/Approvals";
import { Badge, Card, ConfirmButton, Empty, ErrorNote, PageHead, formatTime, useLoad } from "../components/ui";
import { tr } from "../lib/i18n";

/** Meetings you chose to record: live transcript, decisions, action items → tasks. */
export function MeetingsPage() {
  const { status, refresh } = useStatus();
  const list = useLoad(() => get<MeetingSummary[]>("/meetings"));
  const [openId, setOpenId] = useState<string | null>(null);
  const [title, setTitle] = useState("");
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const approvals = useApprovals().filter((a) => a.tool === "meeting_record_start");
  useEvents(["meeting."], () => void list.reload());

  const rec = status?.recording;
  const start = async () => {
    setBusy(true);
    setNote(null);
    try {
      // Asks for confirmation like any critical action; the card appears just below.
      const r = await post<{ success: boolean; message: string }>("/meetings/start", { title: title || null });
      setNote(r.message);
      await refresh();
    } catch (e) { setError(e); } finally { setBusy(false); }
  };
  const stop = async () => { await post("/meetings/stop").catch(setError); await refresh(); };

  if (openId) return <Detail id={openId} onBack={() => setOpenId(null)} onError={setError} />;
  return (
    <div className="stack">
      <PageHead title={tr("Meetings")} sub="Record only when you choose, always visibly. Transcribed on this PC; the audio is never kept." />
      <ErrorNote error={error ?? list.error} />
      <Card title={rec ? tr("Recording") : tr("Record a meeting")} className={rec ? "rec-card" : ""}>
        {rec ? (
          <div className="row wrap">
            <span className="rec-dot" /> <strong dir="auto">{rec.title}</strong> <span className="muted small">{rec.source} · {tr("since {time}", { time: formatTime(rec.startedAt) })}</span>
            <button className="btn btn-primary btn-sm" onClick={stop}><Square size={13} /> {tr("Stop and write notes")}</button>
            <button className="btn btn-ghost btn-sm" onClick={() => setOpenId(rec.id)}>{tr("Live transcript")}</button>
          </div>
        ) : status?.recordingBlocker ? (
          <p className="small muted">{status.recordingBlocker}</p>
        ) : (
          <div className="stack">
            <p className="small muted">{tr("Everyone in the meeting should know it's being recorded. JARVIS captures your microphone and what you hear, transcribes it locally with Whisper, then pulls out decisions and action items.")}</p>
            <div className="row wrap">
              <input className="input grow" dir="auto" placeholder={tr("Name (default: the calendar event happening now)")} value={title} onChange={(e) => setTitle(e.target.value)} />
              <button className="btn btn-primary btn-sm" disabled={busy} onClick={start}><Mic2 size={14} /> {busy ? tr("Waiting for your OK…") : tr("Start recording")}</button>
            </div>
          </div>
        )}
        {approvals.map((a) => <div key={a.id} className="reply-card"><ApprovalCard approval={a} /></div>)}
        {note && <p className="small" dir="auto">{note}</p>}
      </Card>
      {list.data?.length === 0 && <Empty>{tr("No meetings recorded yet. Say “record this meeting” when one starts.")}</Empty>}
      <ul className="list list-rows">
        {list.data?.map((m) => (
          <li key={m.id}>
            <button className="file-row" onClick={() => setOpenId(m.id)}>
              {m.status === "recording" ? <span className="rec-dot" /> : <Circle size={14} className="muted" />}
              <span className="grow" style={{ minWidth: 0 }}>
                <strong className="ellipsis" dir="auto">{m.title}</strong>
                <span className="small muted">{formatTime(m.startedAt)} · {tr("{n} min", { n: Math.round(m.audioSeconds / 60) })} · {tr("{n} decision(s)", { n: m.decisions })} · {tr("{n} action item(s)", { n: m.actionItems })}</span>
              </span>
              <Badge tone={m.status === "done" ? "good" : m.status === "failed" ? "bad" : "warn"}>{tr(m.status)}</Badge>
            </button>
          </li>
        ))}
      </ul>
    </div>
  );
}

function Detail({ id, onBack, onError }: { id: string; onBack: () => void; onError: (e: unknown) => void }) {
  const m = useLoad(() => get<Meeting>(`/meetings/${id}`), [id]);
  const [picked, setPicked] = useState<number[]>([]);
  const [created, setCreated] = useState<number | null>(null);
  useEvents(["meeting."], (e) => { if (e.data?.id === id) void m.reload(); });
  const back = <button className="btn btn-ghost btn-sm back" onClick={onBack}><ArrowLeft size={14} /> {tr("Meetings")}</button>;
  if (!m.data) return <div className="stack">{back}<ErrorNote error={m.error} /></div>;
  const x = m.data;
  const n = x.notes;
  const toggle = (i: number) => setPicked((p) => (p.includes(i) ? p.filter((y) => y !== i) : [...p, i]));
  const makeTasks = async () => {
    try { const r = await post<{ created: number }>(`/meetings/${id}/tasks`, { items: picked }); setCreated(r.created); setPicked([]); } catch (e) { onError(e); }
  };
  return (
    <div className="stack">
      {back}
      <PageHead title={x.title} sub={`${formatTime(x.startedAt)} · ${tr("{n} min", { n: Math.round(x.audioSeconds / 60) })} · ${tr(x.status)}`} />
      {x.error && <div className="note warn small">{x.error}</div>}
      {n ? (
        <div className="grid-2">
          <Card title="Decisions">{n.decisions.length ? <ul className="bullets small">{n.decisions.map((d, i) => <li key={i} dir="auto">{d}</li>)}</ul> : <p className="small muted">{tr("None said explicitly.")}</p>}</Card>
          <Card title="Action items" actions={picked.length > 0 && <button className="btn btn-primary btn-sm" onClick={makeTasks}><CheckSquare size={13} /> {tr("Add {n} as tasks", { n: picked.length })}</button>}>
            {n.actionItems.length ? (
              <ul className="list">
                {n.actionItems.map((a, i) => (
                  <li key={i}>
                    <label className="row small grow" style={{ alignItems: "flex-start" }}>
                      <input type="checkbox" checked={picked.includes(i)} onChange={() => toggle(i)} />
                      <span dir="auto">{a.text}{a.owner && <Badge tone="info">{a.owner}</Badge>}{a.due && <span className="meta"> · {formatTime(a.due)}</span>}</span>
                    </label>
                  </li>
                ))}
              </ul>
            ) : <p className="small muted">{tr("No action items found.")}</p>}
            {created !== null && <p className="small"><ListChecks size={13} /> {tr("Added {n} task(s).", { n: created })}</p>}
          </Card>
          {n.openQuestions.length > 0 && <Card title="Open questions"><ul className="bullets small">{n.openQuestions.map((q, i) => <li key={i} dir="auto">{q}</li>)}</ul></Card>}
          {n.keyPoints.length > 0 && <Card title="Key points" actions={<Badge title={tr("Sentences from the transcript itself")}>{tr("extracted")}</Badge>}><ul className="bullets small">{n.keyPoints.map((k, i) => <li key={i} dir="auto">{k}</li>)}</ul></Card>}
        </div>
      ) : (
        <p className="small muted">{x.status === "recording" ? tr("Notes are written when the recording stops.") : x.status === "transcribing" ? tr("Finishing the transcript…") : tr("No notes.")}</p>
      )}
      <Card title="Transcript" actions={<Badge>{tr("local · Whisper")}</Badge>}>
        {x.transcript ? <pre className="file-preview mail-body" dir="auto">{x.transcript}</pre> : <p className="small muted">{tr("Nothing transcribed yet.")}</p>}
      </Card>
      {x.status !== "recording" && (
        <div className="row"><ConfirmButton className="btn btn-ghost btn-sm" prompt="Delete this meeting's transcript and notes?" onConfirm={() => del(`/meetings/${id}`).then(onBack).catch(onError)}><Trash2 size={13} /> {tr("Delete")}</ConfirmButton></div>
      )}
    </div>
  );
}
