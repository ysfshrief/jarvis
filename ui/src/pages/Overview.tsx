import { useState } from "react";
import { Bell, Code2, MessageCircle, Users, Send } from "lucide-react";
import { get, post, type ActivityEntry, type Reminder, type TaskItem, type TurnResult } from "../api";
import { useStatus, navigate } from "../App";
import { useEvents } from "../events";
import { ApprovalCard, useApprovals } from "../components/Approvals";
import { Badge, Card, Empty, formatTime, timeAgo, useLoad } from "../components/ui";

export function Overview() {
  const { status } = useStatus();
  const approvals = useApprovals();
  const tasks = useLoad(() => get<TaskItem[]>("/tasks"));
  const reminders = useLoad(() => get<Reminder[]>("/reminders"));
  const activity = useLoad(() => get<ActivityEntry[]>("/activity?limit=8"));
  useEvents(["tasks", "reminders"], () => {
    void tasks.reload();
    void reminders.reload();
  });
  useEvents(["activity"], (e) => activity.setData((cur) => [e.data as ActivityEntry, ...(cur ?? [])].slice(0, 8)));

  const hour = new Date().getHours();
  const greeting = hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
  const who = status?.userName || status?.honorific || "";
  const presence = status?.presence?.snapshot;

  return (
    <div className="stack">
      <div className="hero">
        <div>
          <h1>
            {greeting}
            {who ? `, ${who}` : ""}.
          </h1>
          <p className="muted">
            {summaryLine(tasks.data?.length ?? 0, reminders.data ?? [], approvals.length, status?.queuedActions ?? 0)}
          </p>
        </div>
      </div>

      <QuickCommand />

      {approvals.length > 0 && (
        <Card title="Waiting for your decision">
          <div className="stack">
            {approvals.map((a) => (
              <ApprovalCard key={a.id} approval={a} />
            ))}
          </div>
        </Card>
      )}

      {presence && <ContextPanel activity={presence.activity} app={presence.activeProcess} title={presence.activeWindowTitle} meeting={presence.inMeeting} />}

      {!status?.ai?.anyAvailable && (
        <Card title="Enable conversation (free, local)">
          <p>
            Direct commands work now. For open conversation and multi-step planning, JARVIS needs a language model. The free, private option:
          </p>
          <ol className="steps">
            <li>Install <strong>Ollama</strong> from <code>ollama.com</code>.</li>
            <li>In a terminal run <code>ollama pull qwen2.5:7b</code> (good English and Arabic; ~4.7 GB).</li>
            <li>JARVIS detects it automatically — check <a href="#/settings">Settings → AI</a>.</li>
          </ol>
        </Card>
      )}

      <div className="grid-3">
        <Card title="Open tasks" actions={<a href="#/tasks" className="link">All</a>}>
          {tasks.data && tasks.data.length > 0 ? (
            <ul className="list">
              {tasks.data.slice(0, 6).map((t) => (
                <li key={t.id} dir="auto">
                  <span>{t.title}</span>
                  {t.priority !== "normal" && <Badge tone={t.priority === "urgent" ? "bad" : t.priority === "high" ? "warn" : "neutral"}>{t.priority}</Badge>}
                </li>
              ))}
            </ul>
          ) : (
            <Empty>No open tasks.</Empty>
          )}
        </Card>
        <Card title="Upcoming reminders" actions={<a href="#/tasks" className="link">All</a>}>
          {reminders.data && reminders.data.length > 0 ? (
            <ul className="list">
              {reminders.data.slice(0, 6).map((r) => (
                <li key={r.id} dir="auto">
                  <span>{r.text}</span>
                  <span className="muted small">{formatTime(r.dueAt)}</span>
                </li>
              ))}
            </ul>
          ) : (
            <Empty>Nothing scheduled.</Empty>
          )}
        </Card>
        <Card title="Recent activity" actions={<a href="#/activity" className="link">All</a>}>
          {activity.data && activity.data.length > 0 ? (
            <ul className="list">
              {activity.data.map((a) => (
                <li key={a.id}>
                  <span className="truncate" dir="auto" title={a.summary}>{a.summary}</span>
                  <span className="muted small">{timeAgo(a.timestamp)}</span>
                </li>
              ))}
            </ul>
          ) : (
            <Empty>Nothing yet.</Empty>
          )}
        </Card>
      </div>
    </div>
  );
}

function summaryLine(tasks: number, reminders: Reminder[], approvals: number, queued: number) {
  const parts: string[] = [];
  if (approvals) parts.push(`${approvals} action${approvals === 1 ? "" : "s"} waiting for your approval`);
  const soon = reminders.filter((r) => new Date(r.dueAt).getTime() - Date.now() < 3 * 3600_000);
  if (soon.length) parts.push(`${soon.length} reminder${soon.length === 1 ? "" : "s"} in the next few hours`);
  if (tasks) parts.push(`${tasks} open task${tasks === 1 ? "" : "s"}`);
  if (queued) parts.push(`${queued} action${queued === 1 ? "" : "s"} queued until we're back online`);
  return parts.length ? parts.join(" · ") + "." : "All clear. Ask me anything, or say “Jarvis”.";
}

/** The adaptive part of the dashboard: shows what's relevant to what you're doing right now. */
function ContextPanel({ activity, app, title, meeting }: { activity: string; app?: string | null; title?: string | null; meeting: boolean }) {
  if (activity === "other" || activity === "browsing") return null;
  const icon = activity === "coding" ? <Code2 size={18} /> : activity === "meeting" ? <Users size={18} /> : <MessageCircle size={18} />;
  const label = activity === "coding" ? "Coding" : activity === "meeting" ? "In a meeting" : "Communication";
  const hint =
    activity === "coding"
      ? "Ask “run the build in this project and tell me what failed” — I'll ask before running anything that changes files."
      : activity === "meeting"
        ? "I'm holding non-urgent notifications until the meeting ends."
        : "Drafting replies is coming with the inbox connectors; I'll never send without your OK.";
  return (
    <Card title={<span className="row">{icon} {label} mode</span>}>
      <div className="row wrap">
        {app && <Badge tone="info">{app}</Badge>}
        {title && <span className="muted truncate" dir="auto">{title}</span>}
        {meeting && <Badge tone="warn"><Bell size={12} /> notifications held</Badge>}
      </div>
      <p className="muted small">{hint}</p>
    </Card>
  );
}

function QuickCommand() {
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [last, setLast] = useState<TurnResult | null>(null);
  const send = async () => {
    if (!text.trim()) return;
    setBusy(true);
    try {
      setLast(await post<TurnResult>("/chat", { text }));
      setText("");
    } catch (e) {
      setLast({ reply: e instanceof Error ? e.message : String(e), success: false } as TurnResult);
    } finally {
      setBusy(false);
    }
  };
  return (
    <Card>
      <form className="row" onSubmit={(e) => { e.preventDefault(); void send(); }}>
        <input className="input grow" dir="auto" placeholder="Ask JARVIS… (e.g. “open VS Code”, “فكرني بعد ربع ساعة أكلم أحمد”)" value={text} onChange={(e) => setText(e.target.value)} />
        <button className="btn btn-primary" disabled={busy || !text.trim()} type="submit" aria-label="Send">
          <Send size={16} />
        </button>
      </form>
      {last && (
        <div className={`quick-reply ${last.success ? "" : "quick-reply-bad"}`} dir="auto">
          {last.reply}{" "}
          <button className="link small" onClick={() => navigate("assistant")}>Continue in Assistant →</button>
        </div>
      )}
    </Card>
  );
}
