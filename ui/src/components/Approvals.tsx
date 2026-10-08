import { useEffect, useState } from "react";
import { ShieldAlert, Check, X } from "lucide-react";
import { get, post, type Approval } from "../api";
import { useEvents } from "../events";
import { RiskBadge } from "./ui";
import { tr } from "../lib/i18n";

/** Live list of actions waiting for the user's decision. Used on Overview and in the Assistant. */
export function useApprovals() {
  const [items, setItems] = useState<Approval[]>([]);
  const refresh = () => get<Approval[]>("/approvals").then(setItems).catch(() => {});
  useEffect(() => {
    void refresh();
  }, []);
  useEvents(["approval"], (e) => {
    if (e.type === "approval.requested") setItems((cur) => [...cur.filter((a) => a.id !== e.data.id), e.data as Approval]);
    if (e.type === "approval.resolved") setItems((cur) => cur.filter((a) => a.id !== e.data.id));
  });
  return items;
}

export function ApprovalCard({ approval }: { approval: Approval }) {
  const [busy, setBusy] = useState(false);
  const [remaining, setRemaining] = useState(0);
  useEffect(() => {
    const tick = () => setRemaining(Math.max(0, Math.round((new Date(approval.expiresAt).getTime() - Date.now()) / 1000)));
    tick();
    const t = window.setInterval(tick, 1000);
    return () => window.clearInterval(t);
  }, [approval.expiresAt]);

  const decide = async (approve: boolean, remember = false) => {
    setBusy(true);
    try {
      await post(`/approvals/${approval.id}`, { approve, remember });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className={`approval approval-${approval.risk.toLowerCase()}`}>
      <div className="approval-head">
        <ShieldAlert size={18} />
        <strong>{tr("Approval needed")}</strong>
        <RiskBadge risk={approval.risk} />
        <span className="meta">{remaining}s</span>
      </div>
      <div className="approval-summary" dir="auto">
        {approval.summary}
      </div>
      <div className="small muted">{approval.reason}</div>
      <div className="approval-actions">
        <button className="btn btn-primary" disabled={busy} onClick={() => decide(true)}>
          <Check size={16} /> {tr("Approve")}
        </button>
        <button className="btn" disabled={busy} onClick={() => decide(false)}>
          <X size={16} /> {tr("Deny")}
        </button>
        {approval.risk !== "Critical" && (
          <button className="btn btn-ghost small" disabled={busy} onClick={() => decide(true, true)} title={`Always allow ${approval.tool} without asking`}>
            Always allow “{approval.tool}”
          </button>
        )}
      </div>
    </div>
  );
}
