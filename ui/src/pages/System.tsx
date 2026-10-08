import { Bell, Play, Trash2 } from "lucide-react";
import { get, post, put, type Capability, type NotificationItem, type QueuedAction, type ToolInfo } from "../api";
import { useStatus } from "../App";
import { useEvents } from "../events";
import { Badge, Card, Empty, RiskBadge, timeAgo, useLoad } from "../components/ui";

export function SystemPage() {
  const { status } = useStatus();
  const caps = useLoad(() => get<Capability[]>("/capabilities"));
  const tools = useLoad(() => get<ToolInfo[]>("/tools"));
  const notes = useLoad(() => get<NotificationItem[]>("/notifications?limit=30"));
  const queue = useLoad(() => get<QueuedAction[]>("/queue"));
  useEvents(["notification"], () => void notes.reload());
  useEvents(["queue"], () => void queue.reload());
  const p = status?.presence?.snapshot;

  return (
    <div className="stack">
      <h2>System</h2>
      <div className="grid-2">
        <Card title="Runtime">
          <dl className="kv">
            <dt>Version</dt><dd>{status?.version}</dd>
            <dt>Platform</dt><dd>{status?.platformDescription}</dd>
            <dt>Up for</dt><dd>{status?.uptimeSeconds != null ? fmtDuration(status.uptimeSeconds) : ""}</dd>
            <dt>Internet</dt><dd>{status?.online ? "Online" : "Offline"}</dd>
            <dt>Data folder</dt><dd className="mono small wrap-anywhere">{status?.dataDir}</dd>
            <dt>Secrets</dt><dd>{status?.secretsProtection}</dd>
            <dt>Voice</dt><dd>{status?.voice ? `${status.voice.state} · STT ${status.voice.sttEngine}${status.voice.sttReady ? "" : " (not ready)"} · TTS ${status.voice.ttsAvailable ? status.voice.ttsEngine : "unavailable"}` : ""}</dd>
            <dt>Microphone</dt><dd>{status?.voice?.audioAvailable ? status.voice.audioDevice ?? "available" : "none detected"}{status?.voice?.microphoneActive ? " · OPEN" : ""}</dd>
          </dl>
        </Card>
        <Card title="Presence (what JARVIS sees)">
          {!status?.presence?.supported ? (
            <Empty>Presence detection is not available on this platform.</Empty>
          ) : p ? (
            <dl className="kv">
              <dt>State</dt><dd><Badge tone="info">{p.state}</Badge></dd>
              <dt>Active app</dt><dd>{p.activeProcess}</dd>
              <dt>Window</dt><dd dir="auto" className="wrap-anywhere">{p.activeWindowTitle}</dd>
              <dt>Idle</dt><dd>{p.idleSeconds}s</dd>
              <dt>Fullscreen</dt><dd>{p.isFullscreen ? "yes" : "no"}</dd>
              <dt>Microphone in use</dt><dd>{p.microphoneInUse ? "yes" : "no"}</dd>
              <dt>Meeting</dt><dd>{p.inMeeting ? `yes${p.meetingApp ? ` (${p.meetingApp})` : ""}` : "no"}</dd>
              <dt>Activity</dt><dd>{p.activity}</dd>
            </dl>
          ) : null}
          <p className="muted small">Signals come from Windows (foreground window, input idle time, fullscreen state, microphone consent store). No camera, no recording.</p>
        </Card>
      </div>

      <Card title="Offline queue" actions={null}>
        {queue.data?.length ? (
          <ul className="list list-rows">
            {queue.data.map((q) => (
              <li key={q.id}>
                <div className="grow" dir="auto">{q.summary}<div className="muted small">queued {timeAgo(q.createdAt)}</div></div>
                <button className="btn" disabled={!status?.online} onClick={() => post(`/queue/${q.id}/run`)}><Play size={14} /> Run now</button>
                <button className="btn btn-ghost" onClick={() => post(`/queue/${q.id}/discard`)} aria-label="Discard"><Trash2 size={16} /></button>
              </li>
            ))}
          </ul>
        ) : (
          <Empty>Nothing queued. Internet actions requested while offline wait here for your OK.</Empty>
        )}
      </Card>

      <Card title="Notifications" actions={<button className="btn btn-ghost" onClick={() => post("/notifications/test")}><Bell size={14} /> Send test</button>}>
        {notes.data?.length ? (
          <ul className="list list-rows">
            {notes.data.map((n) => (
              <li key={n.id}>
                <div className="grow" dir="auto">
                  {n.title}
                  {n.body && <div className="muted small">{n.body}</div>}
                </div>
                <Badge tone={n.priority === "Critical" ? "bad" : n.priority === "High" ? "warn" : "neutral"}>{n.priority}</Badge>
                <Badge tone={n.status === "held" ? "info" : n.status === "suppressed" ? "neutral" : "good"}>{n.status}</Badge>
                <span className="muted small nowrap">{timeAgo(n.timestamp)}</span>
              </li>
            ))}
          </ul>
        ) : (
          <Empty>No notifications yet.</Empty>
        )}
      </Card>

      <Card title="Tools and permissions">
        <p className="muted small">
          Safe tools run immediately. Sensitive tools ask first (unless you allow them). Critical actions — deleting, power, destructive commands — always ask, whatever you choose here.
        </p>
        <table className="table">
          <thead><tr><th>Tool</th><th>What it does</th><th>Risk</th><th>Permission</th></tr></thead>
          <tbody>
            {tools.data?.map((t) => (
              <tr key={t.name}>
                <td className="mono small">{t.name}{t.requiresInternet && <Badge tone="info">web</Badge>}</td>
                <td className="small">{t.description}</td>
                <td><RiskBadge risk={t.risk} /></td>
                <td>
                  <select className="input input-sm" value={t.policy} onChange={async (e) => { await put(`/tools/${t.name}/policy`, { policy: e.target.value }); void tools.reload(); }}>
                    <option value="Default">Default</option>
                    <option value="Allow">Always allow</option>
                    <option value="Ask">Always ask</option>
                    <option value="Block">Block</option>
                  </select>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </Card>

      <Card title="What this build can do (honest status)">
        <table className="table">
          <thead><tr><th>Area</th><th>Capability</th><th>Status</th><th>Notes</th><th>Phase</th></tr></thead>
          <tbody>
            {caps.data?.map((c) => (
              <tr key={c.area + c.name}>
                <td>{c.area}</td>
                <td>{c.name}</td>
                <td><Badge tone={c.status === "Working" ? "good" : c.status === "Partial" ? "warn" : c.status === "Foundation" ? "info" : "neutral"}>{c.status}</Badge></td>
                <td className="small muted">{c.notes}</td>
                <td className="small">{c.phase}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </Card>
    </div>
  );
}

function fmtDuration(s: number) {
  const d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60);
  return d ? `${d}d ${h}h` : h ? `${h}h ${m}m` : `${m}m`;
}
