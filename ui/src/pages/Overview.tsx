import { useState } from "react";
import { ArrowDown, ArrowUp, BatteryCharging, BatteryMedium, Bell, Code2, CornerDownLeft, MessageCircle, Users } from "lucide-react";
import { get, post, type ActivityEntry, type NotificationItem, type Reminder, type TaskItem, type TurnResult } from "../api";
import { navigate, useClock, useStatus } from "../App";
import { useEvents } from "../events";
import { ApprovalCard, useApprovals } from "../components/Approvals";
import { Orb } from "../components/Orb";
import { StepList } from "../components/Steps";
import { Timeline } from "../components/Timeline";
import { Badge, Card, Empty, fmtBytes, formatTime, Gauge, Sparkline, timeAgo, useLoad } from "../components/ui";
import { useMetrics } from "../lib/metrics";
import { useOrbState } from "../lib/orbState";
import { useActiveTurn } from "../lib/turns";
import { tr } from "../lib/i18n";

/** The cinematic home: JARVIS at the centre, the machine on the left, your day on the right. */
export function Overview() {
  const { status, connected } = useStatus();
  const approvals = useApprovals();
  const orb = useOrbState(status, connected, approvals.length);
  const now = useClock();
  const tasks = useLoad(() => get<TaskItem[]>("/tasks"));
  const reminders = useLoad(() => get<Reminder[]>("/reminders"));
  const activity = useLoad(() => get<ActivityEntry[]>("/activity?limit=6&kind=request"));
  const notes = useLoad(() => get<NotificationItem[]>("/notifications?limit=5"));
  useEvents(["tasks", "reminders"], () => {
    void tasks.reload();
    void reminders.reload();
  });
  useEvents(["activity"], (e) => {
    const a = e.data as ActivityEntry;
    if (a.kind === "request") activity.setData((cur) => [a, ...(cur ?? [])].slice(0, 6));
  });
  useEvents(["notification"], () => void notes.reload());

  const hour = now.getHours();
  const greeting = hour < 5 ? "Working late" : hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
  const ar = document.documentElement.lang === "ar";
  const loc = ar ? "ar-EG" : [];
  const who = (ar ? status?.honorificAr : status?.honorific) || status?.userName || "";

  return (
    <div className="home">
      <div className="home-side">
        <MachineCard />
        <NowCard />
      </div>

      <div className="home-center">
        <div className="home-orb-wrap">
          <Orb state={orb.state} size="100%" label={orb.label} hollow />
          <div className="home-time" dir="ltr">
            <div className="d">{now.toLocaleDateString(loc, { weekday: "long" })}</div>
            <div className="t">{now.toLocaleTimeString(loc, { hour: "2-digit", minute: "2-digit" })}</div>
            <div className="d">{now.toLocaleDateString(loc, { day: "numeric", month: "long", year: "numeric" })}</div>
          </div>
        </div>
        <div className="orb-label" data-state={orb.state}>{orb.label}</div>
        <div className="greeting">
          <h1>{tr(greeting)}{who ? `${document.documentElement.lang === "ar" ? " " : ", "}${who}` : ""}{document.documentElement.lang === "ar" ? "" : "."}</h1>
          <div className="sub">{summaryLine(tasks.data ?? [], reminders.data ?? [], approvals.length, status?.queuedActions ?? 0)}</div>
        </div>
        <HomeCommand />
        {approvals.map((a) => <div key={a.id} className="reply-card"><ApprovalCard approval={a} /></div>)}
        {!status?.ai?.anyAvailable && status && (
          <Card title={tr("Enable conversation (free, local)")} className="reply-card">
            <p className="small">Direct commands work now. For open conversation and multi-step planning, JARVIS needs a local model:</p>
            <ol className="steps small">
              <li>Install <strong>Ollama</strong> from <code>ollama.com</code>.</li>
              <li>Download a model in <a href="#/settings/ai">Settings → AI</a> (qwen2.5:7b recommended, ~4.7 GB).</li>
            </ol>
          </Card>
        )}
      </div>

      <div className="home-side right">
        <Card title={tr("Priorities")} actions={<a href="#/tasks" className="link small">{tr("All")}</a>}>
          {tasks.data && tasks.data.length > 0 ? (
            <ul className="list">
              {tasks.data.slice().sort((a, b) => prio(b.priority) - prio(a.priority)).slice(0, 5).map((t) => (
                <li key={t.id}>
                  <span className="truncate grow" dir="auto">{t.title}</span>
                  {t.dueAt && <span className="meta nowrap" dir="ltr">{formatTime(t.dueAt)}</span>}
                  {t.priority !== "normal" && <Badge tone={t.priority === "urgent" ? "bad" : t.priority === "high" ? "warn" : "neutral"}>{t.priority}</Badge>}
                </li>
              ))}
            </ul>
          ) : <Empty>{tr("No open tasks.")}</Empty>}
        </Card>
        <Card title={tr("Upcoming")} actions={<a href="#/tasks" className="link small">{tr("All")}</a>}>
          {reminders.data && reminders.data.length > 0 ? (
            <ul className="list">
              {reminders.data.slice(0, 5).map((r) => (
                <li key={r.id}>
                  <Bell size={13} className="dim" />
                  <span className="truncate grow" dir="auto">{r.text}</span>
                  <span className="meta nowrap" dir="ltr">{formatTime(r.dueAt)}</span>
                </li>
              ))}
            </ul>
          ) : <Empty>{tr("Nothing scheduled.")}</Empty>}
        </Card>
        <Card title={tr("Recent")} actions={<a href="#/activity" className="link small">{tr("Log")}</a>}>
          {activity.data && activity.data.length > 0 ? (
            <ul className="list">
              {activity.data.map((a) => (
                <li key={a.id}>
                  <span className={`dot ${a.status === "ok" ? "dot-ok" : a.status === "failed" ? "dot-bad" : "dot-unknown"}`} />
                  <span className="truncate grow" dir="auto" title={a.summary}>{a.summary}</span>
                  <span className="meta nowrap" dir="ltr">{timeAgo(a.timestamp)}</span>
                </li>
              ))}
            </ul>
          ) : <Empty>{tr("Nothing yet.")}</Empty>}
        </Card>
        {notes.data && notes.data.length > 0 && (
          <Card title={tr("Notifications")} actions={<a href="#/system" className="link small">{tr("All")}</a>}>
            <ul className="list">
              {notes.data.slice(0, 4).map((n) => (
                <li key={n.id}>
                  <span className="truncate grow" dir="auto">{n.title}</span>
                  <Badge tone={n.status === "held" ? "info" : "neutral"}>{n.status}</Badge>
                </li>
              ))}
            </ul>
          </Card>
        )}
      </div>
    </div>
  );
}

function prio(p: string) {
  return p === "urgent" ? 3 : p === "high" ? 2 : p === "normal" ? 1 : 0;
}

function summaryLine(tasks: TaskItem[], reminders: Reminder[], approvals: number, queued: number) {
  const ar = document.documentElement.lang === "ar";
  const parts: string[] = [];
  const pl = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;
  if (approvals) parts.push(ar ? `${approvals} حاجة مستنية موافقتك` : `${pl(approvals, "action", "actions")} waiting for your approval`);
  const soon = reminders.filter((r) => new Date(r.dueAt).getTime() - Date.now() < 3 * 3600_000).length;
  if (soon) parts.push(ar ? `${soon} تذكير في الكام ساعة الجايين` : `${pl(soon, "reminder", "reminders")} in the next few hours`);
  const urgent = tasks.filter((t) => t.priority === "urgent" || t.priority === "high").length;
  if (urgent) parts.push(ar ? `${urgent} مهمة مهمة` : `${pl(urgent, "high-priority task", "high-priority tasks")}`);
  else if (tasks.length) parts.push(ar ? `${tasks.length} مهمة مفتوحة` : `${pl(tasks.length, "open task", "open tasks")}`);
  if (queued) parts.push(ar ? `${queued} حاجة مستنية النت` : `${pl(queued, "action", "actions")} queued until we're back online`);
  return parts.length ? parts.join(" · ") + (ar ? "" : ".") : tr("All clear. Ask me anything, or say “Jarvis”.");
}

function MachineCard() {
  const { data } = useMetrics(3000);
  const c = data?.current;
  const hist = data?.history ?? [];
  return (
    <Card title={tr("Systems")} actions={<a href="#/system" className="link small">{tr("Details")}</a>}>
      <div className="row" style={{ justifyContent: "space-around", flexWrap: "wrap", gap: 6 }}>
        <Gauge value={c?.cpuPercent} label="CPU" size={96} />
        <Gauge value={c?.memoryPercent} label="Memory" size={96} />
        {c?.gpuPercent != null ? <Gauge value={c.gpuPercent} label="GPU" size={96} /> : null}
      </div>
      <Sparkline values={hist.map((h) => h.cpuPercent)} max={100} height={44} />
      <div className="row between small">
        <span className="row" title="Download"><ArrowDown size={13} className="dim" /> {fmtBytes(c?.netDownBytesPerSec, true)}</span>
        <span className="row" title="Upload"><ArrowUp size={13} className="dim" /> {fmtBytes(c?.netUpBytesPerSec, true)}</span>
        {c?.batteryPercent != null && (
          <span className="row" title="Battery">{c.charging ? <BatteryCharging size={14} /> : <BatteryMedium size={14} />} {c.batteryPercent}%</span>
        )}
      </div>
      <div className="meta">JARVIS itself: {c ? `${Math.round(c.runtimeMemoryMb)} MB · ${c.runtimeCpuPercent.toFixed(1)}% CPU` : "—"}</div>
    </Card>
  );
}

function NowCard() {
  const { status } = useStatus();
  const p = status?.presence?.snapshot;
  if (!status?.presence?.supported || !p) return null;
  const icon = p.activity === "coding" ? <Code2 size={16} /> : p.activity === "meeting" ? <Users size={16} /> : <MessageCircle size={16} />;
  const hint =
    p.activity === "coding"
      ? "Try “build this project and tell me what failed” — I'll ask before running anything that changes files."
      : p.inMeeting
        ? "Holding non-urgent notifications until the meeting ends."
        : null;
  return (
    <Card title={tr("Now")}>
      <div className="row">
        {icon}
        <strong className="small">{p.state === "Idle" ? "Away" : p.activity === "other" ? "Working" : p.activity}</strong>
        {p.inMeeting && <Badge tone="warn">meeting</Badge>}
        {p.isFullscreen && <Badge tone="info">fullscreen</Badge>}
      </div>
      {p.activeProcess && (
        <div className="small truncate" title={p.activeWindowTitle ?? ""}>
          <span className="mono">{p.activeProcess}</span> <span className="muted" dir="auto">{p.activeWindowTitle}</span>
        </div>
      )}
      {hint && <p className="small muted">{hint}</p>}
    </Card>
  );
}

function HomeCommand() {
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [last, setLast] = useState<TurnResult | null>(null);
  const active = useActiveTurn();
  const send = async () => {
    if (!text.trim() || busy) return;
    setBusy(true);
    setLast(null);
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
    <>
      <form className="cmd" onSubmit={(e) => { e.preventDefault(); void send(); }}>
        <span className="prompt" aria-hidden>›</span>
        <input dir="auto" placeholder={tr("Ask JARVIS… “what's happening today?”, “افتح VS Code”")} value={text} onChange={(e) => setText(e.target.value)} aria-label="Ask JARVIS" />
        <span className="kbd" title="Command console">Ctrl K</span>
        <button className="btn btn-primary btn-icon" disabled={busy || !text.trim()} type="submit" aria-label="Send"><CornerDownLeft size={16} /></button>
      </form>
      {busy && active && (
        <div className="reply-card stack-sm">
          <Timeline turn={active} />
          {active.tools.length > 0 && <StepList steps={active.tools} />}
          {active.draft && <div className="bubble"><div className="bubble-text cursor" dir="auto">{active.draft}</div></div>}
        </div>
      )}
      {last && !busy && (
        <div className={`reply-card stack-sm ${last.success ? "" : "msg-error"}`}>
          {last.steps?.length > 0 && <StepList steps={last.steps} />}
          <div className="bubble"><div className="bubble-text" dir="auto">{last.reply}</div></div>
          <button className="link small" style={{ alignSelf: "flex-start" }} onClick={() => navigate("assistant")}>{tr("Continue in Assistant →")}</button>
        </div>
      )}
    </>
  );
}
