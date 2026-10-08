import { useEffect, useState } from "react";
import { Bell, Play, Trash2 } from "lucide-react";
import { get, post, type Capability, type DiskInfo, type NotificationItem, type ProcessInfo, type QueuedAction } from "../api";
import { useStatus } from "../App";
import { useEvents } from "../events";
import { Badge, Card, Empty, fmtBytes, Gauge, Meter, PageHead, Segmented, Sparkline, timeAgo, useLoad } from "../components/ui";
import { useMetrics } from "../lib/metrics";

/** Machine health, JARVIS's own services, and the honest capability matrix. */
export function SystemPage() {
  const { status } = useStatus();
  const { data: metrics } = useMetrics(2000);
  const disks = useLoad(() => get<DiskInfo[]>("/system/disks"));
  const caps = useLoad(() => get<Capability[]>("/capabilities"));
  const notes = useLoad(() => get<NotificationItem[]>("/notifications?limit=30"));
  const queue = useLoad(() => get<QueuedAction[]>("/queue"));
  useEvents(["notification"], () => void notes.reload());
  useEvents(["queue"], () => void queue.reload());
  const c = metrics?.current;
  const h = metrics?.history ?? [];
  const p = status?.presence?.snapshot;
  const maxNet = Math.max(64 * 1024, ...h.map((x) => Math.max(x.netDownBytesPerSec, x.netUpBytesPerSec)));

  return (
    <div className="stack">
      <PageHead title="System" sub={`${status?.platformDescription ?? ""} · sampled only while this page is open`} />

      <div className="grid-4">
        <MetricCard label="CPU" value={c?.cpuPercent} unit="%" series={h.map((x) => x.cpuPercent)} max={100} />
        <MetricCard label="Memory" value={c?.memoryPercent} unit="%" series={h.map((x) => x.memoryPercent)} max={100}
          note={c?.memoryTotalGb ? `${c.memoryUsedGb?.toFixed(1)} / ${c.memoryTotalGb} GB` : undefined} />
        <MetricCard label="GPU" value={c?.gpuPercent} unit="%" series={h.map((x) => x.gpuPercent)} max={100}
          note={c && c.gpuPercent == null ? "Not reported on this system" : "3D engine load"} />
        <Card className="metric-card" title="Network">
          <div className="row between">
            <div><div className="hud-label">Down</div><div className="metric-value">{fmtBytes(c?.netDownBytesPerSec, true)}</div></div>
            <div><div className="hud-label">Up</div><div className="metric-value">{fmtBytes(c?.netUpBytesPerSec, true)}</div></div>
          </div>
          <Sparkline values={h.map((x) => x.netDownBytesPerSec)} max={maxNet} height={44} />
          <div className="meta">{status?.online ? "Internet reachable" : "Offline"}</div>
        </Card>
      </div>

      <div className="grid-3">
        <Card title="Power & thermals">
          <div className="row" style={{ justifyContent: "space-around" }}>
            <Gauge value={c?.batteryPercent} label={c?.charging ? "Charging" : "Battery"} size={104} />
            <Gauge value={c?.temperatureC == null ? null : Math.min(100, c.temperatureC)} display={c?.temperatureC == null ? "—" : `${Math.round(c.temperatureC)}°`} label="Temp" size={104} />
          </div>
          <p className="meta">
            {c?.batteryPercent == null ? "No battery (desktop or not reported). " : ""}
            {c?.temperatureC == null ? "Windows only exposes CPU temperature to administrator tools, so it isn't shown." : ""}
          </p>
        </Card>
        <Card title="Disks">
          {disks.data?.length ? disks.data.map((d) => (
            <div key={d.name} className="stack-sm">
              <div className="row between small">
                <span className="mono">{d.name}{d.label ? ` · ${d.label}` : ""}</span>
                <span className="meta">{d.freeGb} GB free of {d.totalGb} GB</span>
              </div>
              <Meter value={d.usedPercent} tone={d.usedPercent > 95 ? "bad" : d.usedPercent > 85 ? "warn" : undefined} />
            </div>
          )) : <Empty>No disks reported.</Empty>}
        </Card>
        <Card title="JARVIS services">
          <dl className="kv small">
            <dt>Version</dt><dd>{status?.version}</dd>
            <dt>Uptime</dt><dd>{status?.uptimeSeconds != null ? fmtDuration(status.uptimeSeconds) : ""}</dd>
            <dt>Footprint</dt><dd>{c ? `${Math.round(c.runtimeMemoryMb)} MB · ${c.runtimeCpuPercent.toFixed(1)}% CPU` : "—"}</dd>
            <dt>AI</dt><dd>{status?.ai?.providers?.map((x) => `${x.name.replace(/\s*\(.*\)/, "")}: ${x.available == null ? "unchecked" : x.available ? `${x.models} model(s)` : "unavailable"}`).join(" · ")}</dd>
            <dt>Voice</dt><dd>{status?.voice ? `${status.voice.state} · STT ${status.voice.sttEngine}${status.voice.sttReady ? "" : " (not ready)"} · TTS ${status.voice.ttsAvailable ? status.voice.ttsEngine : "unavailable"}` : ""}</dd>
            <dt>Mic</dt><dd>{status?.voice?.audioAvailable ? status.voice.audioDevice ?? "available" : "none"}{status?.voice?.microphoneActive ? " · OPEN" : ""}</dd>
            <dt>Secrets</dt><dd>{status?.secretsProtection}</dd>
            <dt>Data</dt><dd className="mono tiny">{status?.dataDir}</dd>
          </dl>
        </Card>
      </div>

      <div className="grid-2">
        <Processes />
        <Card title="Presence (what JARVIS sees)">
          {!status?.presence?.supported ? (
            <Empty>Presence detection is not available on this platform.</Empty>
          ) : p ? (
            <dl className="kv small">
              <dt>State</dt><dd><Badge tone="info">{p.state}</Badge></dd>
              <dt>Active app</dt><dd className="mono">{p.activeProcess}</dd>
              <dt>Window</dt><dd dir="auto">{p.activeWindowTitle}</dd>
              <dt>Idle</dt><dd>{p.idleSeconds}s</dd>
              <dt>Fullscreen</dt><dd>{p.isFullscreen ? "yes" : "no"}</dd>
              <dt>Mic in use</dt><dd>{p.microphoneInUse ? "yes" : "no"}</dd>
              <dt>Meeting</dt><dd>{p.inMeeting ? `yes${p.meetingApp ? ` (${p.meetingApp})` : ""}` : "no"}</dd>
            </dl>
          ) : null}
          <p className="meta">Signals come from Windows (foreground window, input idle time, fullscreen state, microphone consent store). No camera, no recording.</p>
        </Card>
      </div>

      <Card title="Offline queue">
        {queue.data?.length ? (
          <ul className="list list-rows">
            {queue.data.map((q) => (
              <li key={q.id}>
                <div className="grow" dir="auto">{q.summary}<div className="meta">queued {timeAgo(q.createdAt)}</div></div>
                <button className="btn btn-sm" disabled={!status?.online} onClick={() => post(`/queue/${q.id}/run`)}><Play size={13} /> Run now</button>
                <button className="btn btn-ghost btn-icon" onClick={() => post(`/queue/${q.id}/discard`)} aria-label="Discard"><Trash2 size={15} /></button>
              </li>
            ))}
          </ul>
        ) : <Empty>Nothing queued. Internet actions requested while offline wait here for your OK.</Empty>}
      </Card>

      <Card title="Notifications" actions={<button className="btn btn-ghost btn-sm" onClick={() => post("/notifications/test")}><Bell size={13} /> Send test</button>}>
        {notes.data?.length ? (
          <ul className="list list-rows">
            {notes.data.map((n) => (
              <li key={n.id}>
                <div className="grow" dir="auto">{n.title}{n.body && <div className="small muted">{n.body}</div>}</div>
                <Badge tone={n.priority === "Critical" ? "bad" : n.priority === "High" ? "warn" : "neutral"}>{n.priority}</Badge>
                <Badge tone={n.status === "held" ? "info" : n.status === "suppressed" ? "neutral" : "good"}>{n.status}</Badge>
                <span className="meta nowrap">{timeAgo(n.timestamp)}</span>
              </li>
            ))}
          </ul>
        ) : <Empty>No notifications yet.</Empty>}
      </Card>

      <Card title="What this build can do (honest status)">
        <div className="table-wrap">
          <table className="table">
            <thead><tr><th>Area</th><th>Capability</th><th>Status</th><th>Notes</th></tr></thead>
            <tbody>
              {caps.data?.map((cap) => (
                <tr key={cap.area + cap.name}>
                  <td className="nowrap">{cap.area}</td>
                  <td>{cap.name}</td>
                  <td><Badge tone={cap.status === "Working" ? "good" : cap.status === "Partial" ? "warn" : cap.status === "Foundation" ? "info" : "neutral"}>{cap.status}</Badge></td>
                  <td className="small muted">{cap.notes}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>
    </div>
  );
}

function MetricCard({ label, value, unit, series, max, note }: { label: string; value: number | null | undefined; unit: string; series: (number | null | undefined)[]; max: number; note?: string }) {
  return (
    <Card className="metric-card" title={label}>
      <div className="metric-value">{value == null ? "—" : Math.round(value)}{value != null && <small>{unit}</small>}</div>
      <Sparkline values={series} max={max} height={44} />
      {note && <div className="meta">{note}</div>}
    </Card>
  );
}

function Processes() {
  const [sort, setSort] = useState<"memory" | "cpu">("memory");
  const [list, setList] = useState<ProcessInfo[]>([]);
  useEffect(() => {
    let stop = false;
    let t = 0;
    const tick = async () => {
      if (document.visibilityState === "visible") {
        try { const l = await get<ProcessInfo[]>(`/system/processes?sort=${sort}&limit=12`); if (!stop) setList(l); } catch { /* shown as empty */ }
      }
      if (!stop) t = window.setTimeout(tick, 4000);
    };
    void tick();
    return () => { stop = true; window.clearTimeout(t); };
  }, [sort]);
  return (
    <Card title="Processes" actions={<Segmented label="Sort" value={sort} onChange={setSort} options={[["memory", "Memory"], ["cpu", "CPU"]]} />}>
      <div className="table-wrap">
        <table className="table">
          <thead><tr><th>Process</th><th>PID</th><th>Memory</th><th>CPU</th></tr></thead>
          <tbody>
            {list.map((p) => (
              <tr key={p.pid}>
                <td className="mono small truncate" style={{ maxWidth: 220 }}>{p.name}</td>
                <td className="meta">{p.pid}</td>
                <td className="small nowrap">{p.memoryMb >= 1024 ? `${(p.memoryMb / 1024).toFixed(1)} GB` : `${Math.round(p.memoryMb)} MB`}</td>
                <td className="small">{p.cpuPercent == null ? "…" : `${p.cpuPercent.toFixed(1)}%`}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="meta">To close an app, say “close chrome”; JARVIS asks before force-stopping anything.</p>
    </Card>
  );
}

function fmtDuration(s: number) {
  const d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60);
  return d ? `${d}d ${h}h` : h ? `${h}h ${m}m` : `${m}m`;
}
