import { Bell, Code2, MessageCircle, ShieldAlert, Users, X } from "lucide-react";
import { get, type Reminder, type TaskItem } from "../api";
import { useStatus } from "../App";
import { useEvents } from "../events";
import { useOrbState } from "../lib/orbState";
import { useLatestTurn } from "../lib/turns";
import { useApprovals } from "./Approvals";
import { Orb } from "./Orb";
import { Timeline } from "./Timeline";
import { Badge, formatTime, timeAgo, useLoad } from "./ui";
import { tr } from "../lib/i18n";

/** The right-hand panel: what's happening now and what's next, always one glance away. */
export function ContextPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { status, connected } = useStatus();
  const approvals = useApprovals();
  const orb = useOrbState(status, connected, approvals.length);
  const turn = useLatestTurn(15000);
  const tasks = useLoad(() => get<TaskItem[]>("/tasks"));
  const reminders = useLoad(() => get<Reminder[]>("/reminders"));
  useEvents(["tasks"], () => void tasks.reload());
  useEvents(["reminders"], () => void reminders.reload());
  const p = status?.presence?.snapshot;
  const nextReminder = reminders.data?.[0];
  const top = (tasks.data ?? []).slice().sort((a, b) => rank(b.priority) - rank(a.priority)).slice(0, 4);
  const providers = status?.ai?.providers?.filter((x) => x.available) ?? [];

  return (
    <aside className={`ctx ${open ? "open" : ""}`} aria-label="Context">
      <div className="row between">
        <span className="hud-label">{tr("Context")}</span>
        <button className="btn btn-ghost btn-icon ctx-toggle" onClick={onClose} aria-label="Close context panel"><X size={16} /></button>
      </div>

      <div className="card card-glow">
        <div className="row">
          <Orb state={orb.state} size={54} label={orb.label} />
          <div className="grow">
            <div className="orb-label" style={{ textAlign: "start" }}>{orb.state}</div>
            <div className="small truncate" title={orb.label}>{orb.label}</div>
          </div>
        </div>
        {turn && (
          <>
            <div className="divider" />
            <div className="meta truncate" dir="auto" title={turn.text}>› {turn.text}</div>
            <Timeline turn={turn} />
            {turn.tools.length > 0 && (
              <div className="steps-list">
                {turn.tools.slice(-3).map((t, i) => (
                  <div key={i} className={`step step-${String(t.status).toLowerCase()}`}>
                    <span className="step-tool">{t.tool}</span>
                    <span className="truncate" dir="auto">{t.summary}</span>
                  </div>
                ))}
              </div>
            )}
          </>
        )}
      </div>

      {approvals.length > 0 && (
        <a className="card card-warn" href="#/assistant" style={{ textDecoration: "none", color: "inherit" }}>
          <div className="row"><ShieldAlert size={16} color="var(--warn)" /> <strong>{approvals.length} · {tr("Needs your approval")}</strong></div>
          <div className="small muted truncate" dir="auto">{approvals[0].summary}</div>
        </a>
      )}

      {p && p.state !== "Unknown" && (
        <div className="card">
          <div className="card-head"><h3>{tr("Now")}</h3>{p.inMeeting && <Badge tone="warn"><Bell size={11} /> held</Badge>}</div>
          <div className="row">
            {p.activity === "coding" ? <Code2 size={16} /> : p.activity === "meeting" ? <Users size={16} /> : <MessageCircle size={16} />}
            <span className="small">{activityLabel(p.activity, p.state)}</span>
          </div>
          {p.activeProcess && <div className="small truncate" dir="auto" title={p.activeWindowTitle ?? ""}><span className="mono">{p.activeProcess}</span> · <span className="muted">{p.activeWindowTitle}</span></div>}
          {p.inMeeting && <div className="small muted">Non-urgent notifications are held until the meeting ends.</div>}
        </div>
      )}

      <div className="card">
        <div className="card-head"><h3>{tr("Next up")}</h3><a className="link small" href="#/tasks">{tr("All")}</a></div>
        {nextReminder ? (
          <div className="row small">
            <Bell size={14} />
            <span className="grow truncate" dir="auto">{nextReminder.text}</span>
            <span className="meta nowrap" dir="ltr">{formatTime(nextReminder.dueAt)}</span>
          </div>
        ) : <div className="small muted">{tr("No reminders scheduled.")}</div>}
        {top.length > 0 ? (
          <ul className="list">
            {top.map((t) => (
              <li key={t.id}>
                <span className="small truncate grow" dir="auto">{t.title}</span>
                {t.priority !== "normal" && <Badge tone={t.priority === "urgent" ? "bad" : t.priority === "high" ? "warn" : "neutral"}>{t.priority}</Badge>}
              </li>
            ))}
          </ul>
        ) : <div className="small muted">{tr("No open tasks.")}</div>}
      </div>

      <div className="card">
        <div className="card-head"><h3>{tr("Systems")}</h3></div>
        <dl className="kv small">
          <dt>{tr("Core")}</dt><dd>{connected ? "linked" : "reconnecting"}</dd>
          <dt>{tr("Network")}</dt><dd>{status?.online ? "online" : "offline"}{(status?.queuedActions ?? 0) > 0 ? ` · ${status?.queuedActions} queued` : ""}</dd>
          <dt>{tr("AI")}</dt><dd>{providers.length ? providers.map((x) => x.name.replace(/\s*\(.*\)/, "")).join(", ") : "no model"}</dd>
          <dt>{tr("Voice")}</dt><dd>{status?.voice?.sttReady ? (status.voice.microphoneActive ? "mic open" : "ready") : "not set up"}</dd>
          <dt>{tr("Uptime")}</dt><dd>{status?.uptimeSeconds != null ? timeAgo(new Date(Date.now() - status.uptimeSeconds * 1000).toISOString()).replace(" ago", "") : "—"}</dd>
        </dl>
      </div>
    </aside>
  );
}

function rank(p: string) {
  return p === "urgent" ? 3 : p === "high" ? 2 : p === "normal" ? 1 : 0;
}

function activityLabel(activity: string, state: string) {
  if (state === "Idle") return "Away from the keyboard";
  if (state === "Locked") return "Screen locked";
  return activity === "coding" ? "Coding" : activity === "meeting" ? "In a meeting" : activity === "communication" ? "Communicating" : activity === "browsing" ? "Browsing" : "Working";
}
