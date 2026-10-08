import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { CheckCircle2, CircleDashed, Clock, ShieldX, Wrench, XCircle } from "lucide-react";
import { RiskBadge } from "./ui";
/** Tool executions for one request: what ran, its risk, outcome and time. */
export function StepList({ steps }) {
    return (_jsx("div", { className: "steps-list", children: steps.map((s, i) => {
            const status = String(s.status);
            return (_jsxs("div", { className: `step step-${status.toLowerCase()}`, title: s.message ?? undefined, children: [_jsx(StatusIcon, { status: status }), _jsx("span", { className: "step-tool", children: s.tool }), _jsx("span", { className: "truncate grow", dir: "auto", children: s.summary }), s.risk && s.risk !== "Safe" && _jsx(RiskBadge, { risk: s.risk }), _jsx("span", { className: "meta nowrap", children: status === "running" ? "running" : s.durationMs != null ? `${s.durationMs} ms` : status })] }, i));
        }) }));
}
function StatusIcon({ status }) {
    switch (status) {
        case "running": return _jsx(CircleDashed, { size: 13, className: "spin", color: "var(--accent)" });
        case "Ok": return _jsx(CheckCircle2, { size: 13, color: "var(--good)" });
        case "Queued": return _jsx(Clock, { size: 13, color: "var(--info)" });
        case "Denied": return _jsx(ShieldX, { size: 13, color: "var(--warn)" });
        case "Failed":
        case "TimedOut":
        case "NotFound": return _jsx(XCircle, { size: 13, color: "var(--bad)" });
        default: return _jsx(Wrench, { size: 13 });
    }
}
