import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { useCallback, useEffect, useState } from "react";
export function Card({ title, actions, children, className = "" }) {
    return (_jsxs("section", { className: `card ${className}`, children: [(title || actions) && (_jsxs("header", { className: "card-head", children: [title && _jsx("h3", { children: title }), actions && _jsx("div", { className: "card-actions", children: actions })] })), children] }));
}
export function Badge({ children, tone = "neutral", title }) {
    return (_jsx("span", { className: `badge badge-${tone}`, title: title, children: children }));
}
export function RiskBadge({ risk }) {
    const tone = risk === "Critical" ? "bad" : risk === "Sensitive" ? "warn" : "good";
    return _jsx(Badge, { tone: tone, children: risk });
}
export function StatusDot({ ok, label }) {
    return _jsx("span", { className: `dot ${ok === true ? "dot-ok" : ok === false ? "dot-bad" : "dot-unknown"}`, "aria-label": label, title: label });
}
export function Empty({ children }) {
    return _jsx("div", { className: "empty", children: children });
}
export function ErrorNote({ error }) {
    if (!error)
        return null;
    return _jsx("div", { className: "error-note", children: error instanceof Error ? error.message : String(error) });
}
export function Toggle({ checked, onChange, label, hint }) {
    return (_jsxs("label", { className: "toggle-row", children: [_jsxs("span", { className: "toggle-text", children: [_jsx("span", { children: label }), hint && _jsx("small", { children: hint })] }), _jsx("input", { type: "checkbox", role: "switch", checked: checked, onChange: (e) => onChange(e.target.checked) }), _jsx("span", { className: "switch", "aria-hidden": true })] }));
}
export function Field({ label, hint, children }) {
    return (_jsxs("label", { className: "field", children: [_jsx("span", { className: "field-label", children: label }), children, hint && _jsx("small", { className: "field-hint", children: hint })] }));
}
/** Loads data with a promise-returning function; reload() re-runs it. */
export function useLoad(load, deps = []) {
    const [data, setData] = useState(null);
    const [error, setError] = useState(null);
    const [loading, setLoading] = useState(true);
    const run = useCallback(async () => {
        setLoading(true);
        try {
            setData(await load());
            setError(null);
        }
        catch (e) {
            setError(e);
        }
        finally {
            setLoading(false);
        }
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, deps);
    useEffect(() => {
        void run();
    }, [run]);
    return { data, error, loading, reload: run, setData };
}
export function timeAgo(iso) {
    if (!iso)
        return "";
    const diff = (Date.now() - new Date(iso).getTime()) / 1000;
    const abs = Math.abs(diff);
    const fmt = (n, u) => `${Math.round(n)} ${u}${Math.round(n) === 1 ? "" : "s"}`;
    const s = abs < 60 ? "just now" : abs < 3600 ? fmt(abs / 60, "min") : abs < 86400 ? fmt(abs / 3600, "hour") : fmt(abs / 86400, "day");
    if (s === "just now")
        return s;
    return diff >= 0 ? `${s} ago` : `in ${s}`;
}
export function formatTime(iso) {
    if (!iso)
        return "";
    const d = new Date(iso);
    const sameDay = d.toDateString() === new Date().toDateString();
    return sameDay
        ? d.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })
        : d.toLocaleString([], { weekday: "short", day: "numeric", month: "short", hour: "numeric", minute: "2-digit" });
}
export function ConfirmButton({ onConfirm, children, prompt, className = "btn btn-danger" }) {
    return (_jsx("button", { className: className, onClick: () => {
            if (window.confirm(prompt))
                onConfirm();
        }, children: children }));
}
/** Circular HUD gauge with tick ring. value is 0..100 (null = not reported). */
export function Gauge({ value, label, size = 112, unit = "%", display }) {
    const r = 42;
    const c = 2 * Math.PI * r;
    const v = value == null ? 0 : Math.max(0, Math.min(100, value));
    const tone = value == null ? "" : v >= 90 ? "bad" : v >= 75 ? "warn" : "";
    return (_jsxs("div", { className: `gauge ${tone}`, style: { width: size, height: size }, title: value == null ? `${label}: not reported by this system` : `${label}: ${Math.round(v)}${unit}`, children: [_jsxs("svg", { viewBox: "0 0 100 100", width: size, height: size, children: [_jsx("circle", { className: "g-ticks", cx: "50", cy: "50", r: "48", strokeWidth: "2", strokeDasharray: "0.6 3.2" }), _jsx("circle", { className: "g-track", cx: "50", cy: "50", r: r, strokeWidth: "5" }), _jsx("circle", { className: "g-val", cx: "50", cy: "50", r: r, strokeWidth: "5", strokeDasharray: c, strokeDashoffset: c * (1 - v / 100) })] }), _jsx("div", { className: "gauge-center", children: _jsxs("div", { children: [_jsxs("div", { className: "gauge-value", children: [display ?? (value == null ? "—" : Math.round(v)), value != null && !display && _jsx("small", { children: unit })] }), _jsx("div", { className: "gauge-label", children: label })] }) })] }));
}
/** Minimal SVG sparkline; values are plotted against max (or the data's own max). */
export function Sparkline({ values, max, height = 56 }) {
    const pts = values.map((v) => v ?? 0);
    const top = max ?? Math.max(1, ...pts);
    const n = Math.max(2, pts.length);
    const coords = pts.map((v, i) => `${(i / (n - 1)) * 100},${40 - (Math.min(v, top) / top) * 38}`);
    const line = coords.join(" ");
    return (_jsxs("svg", { className: "spark", viewBox: "0 0 100 40", preserveAspectRatio: "none", style: { height }, "aria-hidden": true, children: [_jsx("defs", { children: _jsxs("linearGradient", { id: "sparkfill", x1: "0", y1: "0", x2: "0", y2: "1", children: [_jsx("stop", { offset: "0%", stopColor: "rgb(var(--accent-rgb))", stopOpacity: "0.35" }), _jsx("stop", { offset: "100%", stopColor: "rgb(var(--accent-rgb))", stopOpacity: "0" })] }) }), [10, 20, 30].map((y) => _jsx("line", { className: "s-grid", x1: "0", x2: "100", y1: y, y2: y }, y)), pts.length > 1 && _jsx("polygon", { className: "s-fill", points: `0,40 ${line} 100,40` }), pts.length > 1 && _jsx("polyline", { className: "s-line", points: line })] }));
}
export function Meter({ value, tone }) {
    return _jsx("div", { className: `bar ${tone ?? ""}`, children: _jsx("i", { style: { width: `${Math.max(0, Math.min(100, value))}%` } }) });
}
/** Segmented control for small enumerations. */
export function Segmented({ value, options, onChange, label }) {
    return (_jsx("div", { className: "seg", role: "radiogroup", "aria-label": label, children: options.map(([v, text]) => (_jsx("button", { type: "button", role: "radio", "aria-checked": value === v, className: value === v ? "on" : "", onClick: () => onChange(v), children: text }, v))) }));
}
export function PageHead({ title, sub, actions }) {
    return (_jsxs("div", { className: "page-head", children: [_jsxs("div", { children: [_jsx("h2", { children: title }), sub && _jsx("div", { className: "sub", children: sub })] }), actions && _jsx("div", { className: "row wrap", children: actions })] }));
}
export function fmtBytes(n, perSec = false) {
    if (n == null)
        return "—";
    const units = ["B", "KB", "MB", "GB", "TB"];
    let i = 0;
    let v = n;
    while (v >= 1024 && i < units.length - 1) {
        v /= 1024;
        i++;
    }
    return `${v >= 100 || i === 0 ? Math.round(v) : v.toFixed(1)} ${units[i]}${perSec ? "/s" : ""}`;
}
