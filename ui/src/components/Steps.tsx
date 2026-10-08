import { CheckCircle2, CircleDashed, Clock, ShieldX, Wrench, XCircle } from "lucide-react";
import type { ToolStep } from "../api";
import type { LiveTool } from "../lib/turns";
import { RiskBadge } from "./ui";

type AnyStep = Pick<LiveTool, "tool" | "summary" | "status" | "message" | "durationMs"> & { risk?: ToolStep["risk"] };

/** Tool executions for one request: what ran, its risk, outcome and time. */
export function StepList({ steps }: { steps: AnyStep[] }) {
  return (
    <div className="steps-list">
      {steps.map((s, i) => {
        const status = String(s.status);
        return (
          <div key={i} className={`step step-${status.toLowerCase()}`} title={s.message ?? undefined}>
            <StatusIcon status={status} />
            <span className="step-tool">{s.tool}</span>
            <span className="truncate grow" dir="auto">{s.summary}</span>
            {s.risk && s.risk !== "Safe" && <RiskBadge risk={s.risk} />}
            <span className="meta nowrap">{status === "running" ? "running" : s.durationMs != null ? `${s.durationMs} ms` : status}</span>
          </div>
        );
      })}
    </div>
  );
}

function StatusIcon({ status }: { status: string }) {
  switch (status) {
    case "running": return <CircleDashed size={13} className="spin" color="var(--accent)" />;
    case "Ok": return <CheckCircle2 size={13} color="var(--good)" />;
    case "Queued": return <Clock size={13} color="var(--info)" />;
    case "Denied": return <ShieldX size={13} color="var(--warn)" />;
    case "Failed": case "TimedOut": case "NotFound": return <XCircle size={13} color="var(--bad)" />;
    default: return <Wrench size={13} />;
  }
}
