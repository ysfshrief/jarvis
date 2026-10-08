import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { Fragment, useState } from "react";
import { get } from "../api";
import { useEvents } from "../events";
import { Badge, Card, Empty, RiskBadge, timeAgo, useLoad } from "../components/ui";
const KINDS = ["", "request", "tool", "approval", "ai", "notification", "memory", "system", "error"];
const STATUSES = ["", "ok", "failed", "pending", "approved", "denied", "timedout", "queued", "blocked"];
/** The audit trail: what JARVIS did, with which tool, what was approved, what failed. */
export function ActivityPage() {
    const [kind, setKind] = useState("");
    const [status, setStatus] = useState("");
    const [open, setOpen] = useState(null);
    const q = new URLSearchParams({ limit: "300", ...(kind ? { kind } : {}), ...(status ? { status } : {}) });
    const items = useLoad(() => get(`/activity?${q}`), [kind, status]);
    useEvents(["activity"], (e) => {
        const a = e.data;
        if ((!kind || a.kind === kind) && (!status || a.status === status))
            items.setData((cur) => [a, ...(cur ?? [])]);
    });
    return (_jsxs("div", { className: "stack", children: [_jsx("h2", { children: "Activity" }), _jsx("p", { className: "muted", children: "Every request, tool run, approval and failure, newest first. Stored locally." }), _jsxs(Card, { children: [_jsxs("div", { className: "row wrap", children: [_jsx("select", { className: "input", value: kind, onChange: (e) => setKind(e.target.value), "aria-label": "Kind", children: KINDS.map((k) => _jsx("option", { value: k, children: k || "All kinds" }, k)) }), _jsx("select", { className: "input", value: status, onChange: (e) => setStatus(e.target.value), "aria-label": "Status", children: STATUSES.map((s) => _jsx("option", { value: s, children: s || "Any status" }, s)) })] }), items.data?.length === 0 && _jsx(Empty, { children: "No activity matches." }), _jsxs("table", { className: "table", children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "When" }), _jsx("th", { children: "Kind" }), _jsx("th", { children: "What" }), _jsx("th", { children: "Tool" }), _jsx("th", { children: "Risk" }), _jsx("th", { children: "Status" }), _jsx("th", { children: "Time" })] }) }), _jsx("tbody", { children: items.data?.map((a) => (_jsxs(Fragment, { children: [_jsxs("tr", { onClick: () => setOpen(open === a.id ? null : a.id), className: a.details ? "clickable" : "", children: [_jsx("td", { className: "nowrap muted", title: new Date(a.timestamp).toLocaleString(), children: timeAgo(a.timestamp) }), _jsx("td", { children: _jsx(Badge, { children: a.kind }) }), _jsx("td", { dir: "auto", className: "wrap-anywhere", children: a.summary }), _jsx("td", { className: "mono small", children: a.tool ?? "" }), _jsx("td", { children: a.risk ? _jsx(RiskBadge, { risk: a.risk }) : null }), _jsx("td", { children: a.status ? _jsx(Badge, { tone: a.status === "ok" || a.status === "approved" ? "good" : a.status === "pending" || a.status === "queued" ? "info" : "bad", children: a.status }) : null }), _jsx("td", { className: "muted small nowrap", children: a.durationMs != null ? `${a.durationMs} ms` : "" })] }), open === a.id && a.details && (_jsx("tr", { children: _jsx("td", { colSpan: 7, children: _jsx("pre", { className: "details", dir: "auto", children: a.details }) }) }))] }, a.id))) })] })] })] }));
}
