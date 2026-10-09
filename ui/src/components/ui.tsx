import { useCallback, useEffect, useState, type ReactNode } from "react";
import type { Risk } from "../api";
import { tr, trNode, uiLocale } from "../lib/i18n";

export function Card({ title, actions, children, className = "" }: { title?: ReactNode; actions?: ReactNode; children?: ReactNode; className?: string }) {
  return (
    <section className={`card ${className}`}>
      {(title || actions) && (
        <header className="card-head">
          {title && <h3>{trNode(title)}</h3>}
          {actions && <div className="card-actions">{actions}</div>}
        </header>
      )}
      {children}
    </section>
  );
}

export function Badge({ children, tone = "neutral", title }: { children: ReactNode; tone?: "neutral" | "good" | "warn" | "bad" | "info" | "accent"; title?: string }) {
  return (
    <span className={`badge badge-${tone}`} title={title}>
      {children}
    </span>
  );
}

export function RiskBadge({ risk }: { risk: Risk | string }) {
  const tone = risk === "Critical" ? "bad" : risk === "Sensitive" ? "warn" : "good";
  return <Badge tone={tone}>{tr(String(risk))}</Badge>;
}

export function StatusDot({ ok, label }: { ok: boolean | null | undefined; label?: string }) {
  const l = label === undefined ? undefined : tr(label);
  return <span className={`dot ${ok === true ? "dot-ok" : ok === false ? "dot-bad" : "dot-unknown"}`} aria-label={l} title={l} />;
}

export function Empty({ children }: { children: ReactNode }) {
  return <div className="empty">{trNode(children)}</div>;
}

export function ErrorNote({ error }: { error: unknown }) {
  if (!error) return null;
  return <div className="error-note">{error instanceof Error ? error.message : String(error)}</div>;
}

export function Toggle({ checked, onChange, label, hint }: { checked: boolean; onChange: (v: boolean) => void; label: string; hint?: string }) {
  return (
    <label className="toggle-row">
      <span className="toggle-text">
        <span>{tr(label)}</span>
        {hint && <small>{tr(hint)}</small>}
      </span>
      <input type="checkbox" role="switch" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      <span className="switch" aria-hidden />
    </label>
  );
}

export function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <label className="field">
      <span className="field-label">{tr(label)}</span>
      {children}
      {hint && <small className="field-hint">{tr(hint)}</small>}
    </label>
  );
}

/** Loads data with a promise-returning function; reload() re-runs it. */
export function useLoad<T>(load: () => Promise<T>, deps: unknown[] = []) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(true);
  const run = useCallback(async () => {
    setLoading(true);
    try {
      setData(await load());
      setError(null);
    } catch (e) {
      setError(e);
    } finally {
      setLoading(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);
  useEffect(() => {
    void run();
  }, [run]);
  return { data, error, loading, reload: run, setData };
}

export function timeAgo(iso: string | null | undefined): string {
  if (!iso) return "";
  const diff = (Date.now() - new Date(iso).getTime()) / 1000;
  const abs = Math.abs(diff);
  if (abs < 60) return tr("just now");
  const [n, unit] = abs < 3600 ? [Math.round(abs / 60), "min"] : abs < 86400 ? [Math.round(abs / 3600), "hour"] : [Math.round(abs / 86400), "day"];
  const plural = n === 1 ? "" : "s";
  // Keys like "{n} min ago" / "in {n} hours" — Arabic word order differs, so the whole phrase is translated.
  return diff >= 0 ? tr(`{n} ${unit}${plural} ago`, { n }) : tr(`in {n} ${unit}${plural}`, { n });
}

export function formatTime(iso: string | null | undefined): string {
  if (!iso) return "";
  const d = new Date(iso);
  const sameDay = d.toDateString() === new Date().toDateString();
  return sameDay
    ? d.toLocaleTimeString(uiLocale() ?? [], { hour: "numeric", minute: "2-digit" })
    : d.toLocaleString(uiLocale() ?? [], { weekday: "short", day: "numeric", month: "short", hour: "numeric", minute: "2-digit" });
}

export function ConfirmButton({ onConfirm, children, prompt, className = "btn btn-danger" }: { onConfirm: () => void; children: ReactNode; prompt: string; className?: string }) {
  return (
    <button
      className={className}
      onClick={() => {
        if (window.confirm(tr(prompt))) onConfirm();
      }}
    >
      {children}
    </button>
  );
}

/** Circular HUD gauge with tick ring. value is 0..100 (null = not reported). */
export function Gauge({ value, label, size = 112, unit = "%", display }: { value: number | null | undefined; label: string; size?: number; unit?: string; display?: string }) {
  const r = 42;
  const c = 2 * Math.PI * r;
  const v = value == null ? 0 : Math.max(0, Math.min(100, value));
  const tone = value == null ? "" : v >= 90 ? "bad" : v >= 75 ? "warn" : "";
  return (
    <div className={`gauge ${tone}`} style={{ width: size, height: size }} title={value == null ? `${label}: not reported by this system` : `${label}: ${Math.round(v)}${unit}`}>
      <svg viewBox="0 0 100 100" width={size} height={size}>
        <circle className="g-ticks" cx="50" cy="50" r="48" strokeWidth="2" strokeDasharray="0.6 3.2" />
        <circle className="g-track" cx="50" cy="50" r={r} strokeWidth="5" />
        <circle className="g-val" cx="50" cy="50" r={r} strokeWidth="5" strokeDasharray={c} strokeDashoffset={c * (1 - v / 100)} />
      </svg>
      <div className="gauge-center">
        <div>
          <div className="gauge-value">{display ?? (value == null ? "—" : Math.round(v))}{value != null && !display && <small>{unit}</small>}</div>
          <div className="gauge-label">{label}</div>
        </div>
      </div>
    </div>
  );
}

/** Minimal SVG sparkline; values are plotted against max (or the data's own max). */
export function Sparkline({ values, max, height = 56 }: { values: (number | null | undefined)[]; max?: number; height?: number }) {
  const pts = values.map((v) => v ?? 0);
  const top = max ?? Math.max(1, ...pts);
  const n = Math.max(2, pts.length);
  const coords = pts.map((v, i) => `${(i / (n - 1)) * 100},${40 - (Math.min(v, top) / top) * 38}`);
  const line = coords.join(" ");
  return (
    <svg className="spark" viewBox="0 0 100 40" preserveAspectRatio="none" style={{ height }} aria-hidden>
      <defs>
        <linearGradient id="sparkfill" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor="rgb(var(--accent-rgb))" stopOpacity="0.35" />
          <stop offset="100%" stopColor="rgb(var(--accent-rgb))" stopOpacity="0" />
        </linearGradient>
      </defs>
      {[10, 20, 30].map((y) => <line key={y} className="s-grid" x1="0" x2="100" y1={y} y2={y} />)}
      {pts.length > 1 && <polygon className="s-fill" points={`0,40 ${line} 100,40`} />}
      {pts.length > 1 && <polyline className="s-line" points={line} />}
    </svg>
  );
}

export function Meter({ value, tone }: { value: number; tone?: "warn" | "bad" }) {
  return <div className={`bar ${tone ?? ""}`}><i style={{ width: `${Math.max(0, Math.min(100, value))}%` }} /></div>;
}

/** Segmented control for small enumerations. */
export function Segmented<T extends string>({ value, options, onChange, label }: { value: T; options: [T, string][]; onChange: (v: T) => void; label: string }) {
  return (
    <div className="seg" role="radiogroup" aria-label={tr(label)}>
      {options.map(([v, text]) => (
        <button key={v} type="button" role="radio" aria-checked={value === v} className={value === v ? "on" : ""} onClick={() => onChange(v)}>{tr(text)}</button>
      ))}
    </div>
  );
}

export function PageHead({ title, sub, actions }: { title: string; sub?: ReactNode; actions?: ReactNode }) {
  return (
    <div className="page-head">
      <div>
        <h2>{tr(title)}</h2>
        {sub && <div className="sub" dir="auto">{trNode(sub)}</div>}
      </div>
      {actions && <div className="row wrap">{actions}</div>}
    </div>
  );
}

export function fmtBytes(n: number | null | undefined, perSec = false): string {
  if (n == null) return "—";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let i = 0;
  let v = n;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return `${v >= 100 || i === 0 ? Math.round(v) : v.toFixed(1)} ${units[i]}${perSec ? "/s" : ""}`;
}
