import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { useEffect, useState } from "react";
import { ShieldAlert, Check, X } from "lucide-react";
import { get, post } from "../api";
import { useEvents } from "../events";
import { RiskBadge } from "./ui";
import { tr } from "../lib/i18n";
/** Live list of actions waiting for the user's decision. Used on Overview and in the Assistant. */
export function useApprovals() {
    const [items, setItems] = useState([]);
    const refresh = () => get("/approvals").then(setItems).catch(() => { });
    useEffect(() => {
        void refresh();
    }, []);
    useEvents(["approval"], (e) => {
        if (e.type === "approval.requested")
            setItems((cur) => [...cur.filter((a) => a.id !== e.data.id), e.data]);
        if (e.type === "approval.resolved")
            setItems((cur) => cur.filter((a) => a.id !== e.data.id));
    });
    return items;
}
export function ApprovalCard({ approval }) {
    const [busy, setBusy] = useState(false);
    const [remaining, setRemaining] = useState(0);
    useEffect(() => {
        const tick = () => setRemaining(Math.max(0, Math.round((new Date(approval.expiresAt).getTime() - Date.now()) / 1000)));
        tick();
        const t = window.setInterval(tick, 1000);
        return () => window.clearInterval(t);
    }, [approval.expiresAt]);
    const decide = async (approve, remember = false) => {
        setBusy(true);
        try {
            await post(`/approvals/${approval.id}`, { approve, remember });
        }
        finally {
            setBusy(false);
        }
    };
    return (_jsxs("div", { className: `approval approval-${approval.risk.toLowerCase()}`, children: [_jsxs("div", { className: "approval-head", children: [_jsx(ShieldAlert, { size: 18 }), _jsx("strong", { children: tr("Approval needed") }), _jsx(RiskBadge, { risk: approval.risk }), _jsxs("span", { className: "meta", children: [remaining, "s"] })] }), _jsx("div", { className: "approval-summary", dir: "auto", children: approval.summary }), _jsx("div", { className: "small muted", children: approval.reason }), _jsxs("div", { className: "approval-actions", children: [_jsxs("button", { className: "btn btn-primary", disabled: busy, onClick: () => decide(true), children: [_jsx(Check, { size: 16 }), " ", tr("Approve")] }), _jsxs("button", { className: "btn", disabled: busy, onClick: () => decide(false), children: [_jsx(X, { size: 16 }), " ", tr("Deny")] }), approval.risk !== "Critical" && (_jsxs("button", { className: "btn btn-ghost small", disabled: busy, onClick: () => decide(true, true), title: `Always allow ${approval.tool} without asking`, children: ["Always allow \u201C", approval.tool, "\u201D"] }))] })] }));
}
