import { Fragment, useState } from "react";
import { get, type ActivityEntry } from "../api";
import { useEvents } from "../events";
import { Badge, Card, Empty, RiskBadge, timeAgo, useLoad } from "../components/ui";
import { tr, uiLocale } from "../lib/i18n";

const KINDS = ["", "request", "tool", "approval", "ai", "notification", "memory", "system", "error"];
const STATUSES = ["", "ok", "failed", "pending", "approved", "denied", "timedout", "queued", "blocked"];

/** The audit trail: what JARVIS did, with which tool, what was approved, what failed. */
export function ActivityPage() {
  const [kind, setKind] = useState("");
  const [status, setStatus] = useState("");
  const [open, setOpen] = useState<number | null>(null);
  const q = new URLSearchParams({ limit: "300", ...(kind ? { kind } : {}), ...(status ? { status } : {}) });
  const items = useLoad(() => get<ActivityEntry[]>(`/activity?${q}`), [kind, status]);
  useEvents(["activity"], (e) => {
    const a = e.data as ActivityEntry;
    if ((!kind || a.kind === kind) && (!status || a.status === status)) items.setData((cur) => [a, ...(cur ?? [])]);
  });

  return (
    <div className="stack">
      <h2>{tr("Activity")}</h2>
      <p className="muted">{tr("Every request, tool run, approval and failure, newest first. Stored locally.")}</p>
      <Card>
        <div className="row wrap">
          <select className="input" value={kind} onChange={(e) => setKind(e.target.value)} aria-label={tr("Kind")}>
            {KINDS.map((k) => <option key={k} value={k}>{k ? tr(k) : tr("All kinds")}</option>)}
          </select>
          <select className="input" value={status} onChange={(e) => setStatus(e.target.value)} aria-label={tr("Status")}>
            {STATUSES.map((s) => <option key={s} value={s}>{s ? tr(s) : tr("Any status")}</option>)}
          </select>
        </div>
        {items.data?.length === 0 && <Empty>No activity matches.</Empty>}
        <div className="table-wrap">
        <table className="table">
          <thead>
            <tr><th>{tr("When")}</th><th>{tr("Kind")}</th><th>{tr("What")}</th><th>{tr("Tool")}</th><th>{tr("Risk")}</th><th>{tr("Status")}</th><th>{tr("Time")}</th></tr>
          </thead>
          <tbody>
            {items.data?.map((a) => (
              <Fragment key={a.id}>
                <tr onClick={() => setOpen(open === a.id ? null : a.id)} className={a.details ? "clickable" : ""}>
                  <td className="nowrap muted" title={new Date(a.timestamp).toLocaleString(uiLocale())}>{timeAgo(a.timestamp)}</td>
                  <td><Badge>{tr(a.kind)}</Badge></td>
                  <td dir="auto" className="wrap-anywhere">{a.summary}</td>
                  <td className="mono small">{a.tool ?? ""}</td>
                  <td>{a.risk ? <RiskBadge risk={a.risk} /> : null}</td>
                  <td>{a.status ? <Badge tone={a.status === "ok" || a.status === "approved" ? "good" : a.status === "pending" || a.status === "queued" ? "info" : "bad"}>{tr(a.status)}</Badge> : null}</td>
                  <td className="muted small nowrap">{a.durationMs != null ? `${a.durationMs} ms` : ""}</td>
                </tr>
                {open === a.id && a.details && (
                  <tr>
                    <td colSpan={7}><pre className="details" dir="auto">{a.details}</pre></td>
                  </tr>
                )}
              </Fragment>
            ))}
          </tbody>
        </table>
        </div>
      </Card>
    </div>
  );
}
