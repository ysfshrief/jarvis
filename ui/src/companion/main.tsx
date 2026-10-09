import { StrictMode, useCallback, useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { Bell, Check, Home, MessageSquare, ShieldAlert, X } from "lucide-react";
import "@fontsource/ibm-plex-sans/latin-400.css";
import "@fontsource/ibm-plex-sans/latin-600.css";
import "@fontsource/ibm-plex-sans-arabic/arabic-400.css";
import "@fontsource/rajdhani/latin-600.css";
import "../styles.css";
import "./companion.css";

const KEY = "jarvis.companion.token";

/** Reads a token handed over by the Android app ("#token=…"), then keeps the URL clean. */
function takeHashToken() {
  const m = location.hash.match(/token=([A-Za-z0-9_-]+)/);
  if (m) {
    try { localStorage.setItem(KEY, m[1]); } catch { /* storage blocked */ }
    history.replaceState(null, "", location.pathname);
  }
}
takeHashToken();

function token() { try { return localStorage.getItem(KEY); } catch { return null; } }

async function call<T>(path: string, init?: RequestInit): Promise<T> {
  const r = await fetch(`/companion/api${path}`, {
    ...init,
    headers: { "Content-Type": "application/json", ...(token() ? { Authorization: `Bearer ${token()}` } : {}), ...(init?.headers ?? {}) },
  });
  if (r.status === 401) { try { localStorage.removeItem(KEY); } catch { /* */ } throw new Error("unpaired"); }
  const body = r.status === 204 ? null : await r.json().catch(() => null);
  if (!r.ok) throw new Error((body as { error?: string } | null)?.error ?? `Error ${r.status}`);
  return body as T;
}

interface Status { name: string; online: boolean; pendingApprovals: number; recording?: string | null; allowApprovals: boolean; honorific: string }
interface Approval { id: string; tool: string; risk: string; summary: string; reason?: string; expiresAt: string }
interface Note { id: string; title: string; body?: string; priority: string; source: string; timestamp: string }

function Pair({ onPaired }: { onPaired: () => void }) {
  const [code, setCode] = useState(() => location.hash.match(/pair=([A-Z0-9-]+)/i)?.[1] ?? "");
  const [name, setName] = useState(() => (/android/i.test(navigator.userAgent) ? "Android phone" : "Phone"));
  const [error, setError] = useState<string | null>(null);
  const pair = async () => {
    setError(null);
    try {
      const r = await call<{ token: string }>("/pair", { method: "POST", body: JSON.stringify({ code, deviceName: name }) });
      localStorage.setItem(KEY, r.token);
      history.replaceState(null, "", location.pathname);
      onPaired();
    } catch (e) { setError((e as Error).message); }
  };
  return (
    <div className="cmp-pair">
      <div className="cmp-orb" />
      <h1>JARVIS</h1>
      <p className="muted small">On your PC: Settings → Devices → Pair a phone. Enter the code shown there.</p>
      <input className="input" placeholder="Code, e.g. K7QM-3XWP" value={code} onChange={(e) => setCode(e.target.value.toUpperCase())} autoCapitalize="characters" />
      <input className="input" placeholder="This phone's name" value={name} onChange={(e) => setName(e.target.value)} />
      <button className="btn btn-primary" disabled={code.replace("-", "").length < 8} onClick={pair}>Pair</button>
      {error && <p className="bad small">{error}</p>}
    </div>
  );
}

function CompanionApp() {
  const [paired, setPaired] = useState(() => !!token());
  const [tab, setTab] = useState<"home" | "approvals" | "ask" | "alerts">("home");
  const [status, setStatus] = useState<Status | null>(null);
  const [briefing, setBriefing] = useState<string>("");
  const [approvals, setApprovals] = useState<Approval[]>([]);
  const [notes, setNotes] = useState<Note[]>([]);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      const [s, a] = await Promise.all([call<Status>("/status"), call<Approval[]>("/approvals")]);
      setStatus(s);
      setApprovals(a);
      setError(null);
    } catch (e) {
      if ((e as Error).message === "unpaired") setPaired(false);
      else setError("Can't reach JARVIS — is the PC on and on the same network?");
    }
  }, []);

  useEffect(() => {
    if (!paired) return;
    void refresh();
    void call<{ text: string }>("/briefing").then((b) => setBriefing(b.text)).catch(() => {});
    const t = setInterval(() => { if (document.visibilityState === "visible") void refresh(); }, 5000);
    return () => clearInterval(t);
  }, [paired, refresh]);

  useEffect(() => {
    if (paired && tab === "alerts") void call<Note[]>("/notifications").then(setNotes).catch(() => {});
  }, [paired, tab]);

  if (!paired) return <Pair onPaired={() => setPaired(true)} />;

  return (
    <div className="cmp">
      <header className="cmp-top">
        <span className="cmp-dot" data-ok={status ? "1" : "0"} />
        <strong>JARVIS</strong>
        {status?.recording && <span className="rec-chip"><span className="rec-dot" /> REC</span>}
        <span className="grow" />
        <span className="meta">{status ? (status.online ? "online" : "offline") : "…"}</span>
      </header>
      {error && <div className="note warn small">{error}</div>}
      <main className="cmp-main">
        {tab === "home" && (
          <div className="stack">
            {approvals.length > 0 && (
              <button className="cmp-alert" onClick={() => setTab("approvals")}><ShieldAlert size={18} /> {approvals.length} action(s) waiting for your OK</button>
            )}
            <section className="card"><pre className="cmp-brief" dir="auto">{briefing || "…"}</pre></section>
          </div>
        )}
        {tab === "approvals" && <Approvals items={approvals} allowed={status?.allowApprovals ?? false} onDone={refresh} />}
        {tab === "ask" && <Ask />}
        {tab === "alerts" && (
          <ul className="list list-rows">
            {notes.length === 0 && <li className="muted small">No notifications.</li>}
            {notes.map((n) => <li key={n.id}><span className="grow"><strong dir="auto">{n.title}</strong>{n.body && <div className="small muted" dir="auto">{n.body}</div>}</span><span className="meta">{new Date(n.timestamp).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</span></li>)}
          </ul>
        )}
      </main>
      <nav className="cmp-tabs">
        <button className={tab === "home" ? "on" : ""} onClick={() => setTab("home")}><Home size={18} /><span>Today</span></button>
        <button className={tab === "approvals" ? "on" : ""} onClick={() => setTab("approvals")}><ShieldAlert size={18} /><span>Approvals{approvals.length ? ` ${approvals.length}` : ""}</span></button>
        <button className={tab === "ask" ? "on" : ""} onClick={() => setTab("ask")}><MessageSquare size={18} /><span>Ask</span></button>
        <button className={tab === "alerts" ? "on" : ""} onClick={() => setTab("alerts")}><Bell size={18} /><span>Alerts</span></button>
      </nav>
    </div>
  );
}

function Approvals({ items, allowed, onDone }: { items: Approval[]; allowed: boolean; onDone: () => void }) {
  const [busy, setBusy] = useState<string | null>(null);
  const decide = async (id: string, approve: boolean) => {
    setBusy(id);
    try { await call(`/approvals/${id}`, { method: "POST", body: JSON.stringify({ approve }) }); } catch { /* shown on next refresh */ }
    setBusy(null);
    onDone();
  };
  if (items.length === 0) return <p className="muted small">Nothing is waiting for your approval.</p>;
  return (
    <div className="stack">
      {!allowed && <div className="note small">Approving from the phone is turned off on the PC.</div>}
      {items.map((a) => (
        <section key={a.id} className={`card cmp-approval risk-${a.risk.toLowerCase()}`}>
          <div className="stack">
            <div className="row"><span className={`badge ${a.risk === "Critical" ? "badge-bad" : "badge-warn"}`}>{a.risk}</span><span className="meta">{a.tool}</span></div>
            <strong dir="auto">{a.summary}</strong>
            {a.reason && <pre className="small muted cmp-reason" dir="auto">{a.reason}</pre>}
            {allowed && (
              <div className="row">
                <button className="btn btn-primary grow" disabled={busy === a.id} onClick={() => decide(a.id, true)}><Check size={16} /> Approve</button>
                <button className="btn grow" disabled={busy === a.id} onClick={() => decide(a.id, false)}><X size={16} /> Refuse</button>
              </div>
            )}
          </div>
        </section>
      ))}
    </div>
  );
}

function Ask() {
  const [text, setText] = useState("");
  const [log, setLog] = useState<{ who: "you" | "jarvis"; text: string }[]>([]);
  const [busy, setBusy] = useState(false);
  const send = async () => {
    const t = text.trim();
    if (!t) return;
    setText("");
    setLog((l) => [...l, { who: "you", text: t }]);
    setBusy(true);
    try {
      const r = await call<{ reply: string }>("/chat", { method: "POST", body: JSON.stringify({ text: t }) });
      setLog((l) => [...l, { who: "jarvis", text: r.reply }]);
    } catch (e) { setLog((l) => [...l, { who: "jarvis", text: (e as Error).message }]); }
    setBusy(false);
  };
  return (
    <div className="stack">
      <div className="cmp-log">
        {log.map((m, i) => <div key={i} className={`cmp-msg ${m.who}`} dir="auto">{m.text}</div>)}
        {busy && <div className="cmp-msg jarvis muted">…</div>}
      </div>
      <form className="row" onSubmit={(e) => { e.preventDefault(); void send(); }}>
        <input className="input grow" dir="auto" value={text} onChange={(e) => setText(e.target.value)} placeholder="Ask JARVIS… «إيه اللي ورايا النهارده؟»" />
        <button className="btn btn-primary" disabled={busy || !text.trim()}>Send</button>
      </form>
    </div>
  );
}

createRoot(document.getElementById("root")!).render(<StrictMode><CompanionApp /></StrictMode>);
