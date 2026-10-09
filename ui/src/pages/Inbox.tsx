import { useEffect, useState } from "react";
import { ArrowLeft, Check, Link2, Mail, PenLine, RefreshCw, Search, Send, Settings2, Trash2, Wand2 } from "lucide-react";
import { del, get, post, put, type InboxMessage, type InboxStatus, type MailCategory, type MailDraft, type MessageDetail } from "../api";
import { navigate } from "../App";
import { useEvents } from "../events";
import { ApprovalCard, useApprovals } from "../components/Approvals";
import { Badge, Card, Empty, ErrorNote, Field, PageHead, Segmented, formatTime, timeAgo, useLoad } from "../components/ui";
import { tr } from "../lib/i18n";

type Tab = MailCategory | "drafts";
const LABELS: Record<MailCategory, string> = { urgent: "Urgent", needs_response: "Needs reply", important: "Important", fyi: "FYI", noise: "Noise" };
const TONES: Record<MailCategory, "bad" | "warn" | "accent" | "info" | "neutral"> = { urgent: "bad", needs_response: "warn", important: "accent", fyi: "info", noise: "neutral" };

/** The executive inbox: sorted mail, reasons, drafts. Nothing is ever sent without your approval. */
export function InboxPage() {
  const status = useLoad(() => get<InboxStatus>("/inbox/status"));
  const [tab, setTab] = useState<Tab>("urgent");
  const [openId, setOpenId] = useState<string | null>(() => location.hash.split("/")[2] ?? null);
  const [syncing, setSyncing] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const approvals = useApprovals().filter((a) => a.tool === "inbox_send");
  useEvents(["inbox."], () => void status.reload());

  const open = (id: string | null) => { setOpenId(id); history.replaceState(null, "", id ? `#/inbox/${id}` : "#/inbox"); };
  const sync = async () => { setSyncing(true); try { await post("/inbox/sync", {}); await status.reload(); } catch (e) { setError(e); } finally { setSyncing(false); } };

  const s = status.data;
  if (s && s.accounts.length === 0) return <NoAccounts s={s} />;
  const c = s?.counts;
  const options: [Tab, string][] = [
    ...(Object.keys(LABELS) as MailCategory[]).map((k) => [k, `${tr(LABELS[k])}${c && c[k] ? ` ${c[k]}` : ""}`] as [Tab, string]),
    ["drafts", `${tr("Drafts")}${s?.drafts ? ` ${s.drafts}` : ""}`],
  ];

  return (
    <div className="stack">
      <PageHead
        title={tr("Executive Inbox")}
        sub="Your mail, sorted with the reason why. JARVIS drafts; only you send."
        actions={
          <>
            <button className="btn btn-sm" disabled={syncing} onClick={sync}><RefreshCw size={14} className={syncing ? "spin" : ""} /> {syncing ? "Checking…" : "Check now"}</button>
            <button className="btn btn-ghost btn-sm" onClick={() => navigate("settings", "accounts")}><Settings2 size={14} /> Accounts</button>
          </>
        }
      />
      <ErrorNote error={error ?? status.error} />
      {s?.accounts.filter((a) => a.status === "error").map((a) => (
        <div key={a.id} className="note warn small">{a.address}: {a.statusMessage}</div>
      ))}
      {approvals.map((a) => <div key={a.id} className="reply-card"><ApprovalCard approval={a} /></div>)}
      {openId ? (
        <Reader id={openId} onBack={() => open(null)} onError={setError} />
      ) : (
        <>
          <Segmented label="Category" value={tab} onChange={setTab} options={options} />
          {tab === "drafts" ? <Drafts onError={setError} /> : <MessageList category={tab} onOpen={open} />}
        </>
      )}
    </div>
  );
}

function NoAccounts({ s }: { s: InboxStatus }) {
  return (
    <div className="stack">
      <PageHead title={tr("Executive Inbox")} sub="Connect your email and JARVIS sorts it into urgent, needs reply, important, FYI and noise — and drafts replies you approve." />
      <Card title="Connect an account">
        <p className="small muted">Gmail works with an app password; Yahoo, iCloud, Zoho and company mail servers work over IMAP/SMTP. JARVIS reads your inbox without changing it and never sends anything without your approval.</p>
        <div className="row"><button className="btn btn-primary btn-sm" onClick={() => navigate("settings", "accounts")}><Mail size={14} /> Connect email</button></div>
      </Card>
      <ConnectorTable s={s} />
    </div>
  );
}

export function ConnectorTable({ s }: { s: InboxStatus }) {
  const tone = (st: string) => (st === "supported" ? "good" : st === "not possible" ? "bad" : "warn");
  return (
    <Card title="Messaging services">
      <ul className="list list-rows">
        {s.connectors.map((c) => (
          <li key={c.id}>
            <span className="grow" style={{ display: "flex", flexDirection: "column", gap: 2 }}>
              <span className="row wrap" style={{ gap: 8 }}><strong>{c.name}</strong> <Badge tone={tone(c.status)}>{c.status}</Badge></span>
              <span className="small muted">{c.note}</span>
            </span>
          </li>
        ))}
      </ul>
    </Card>
  );
}

function MessageList({ category, onOpen }: { category: MailCategory; onOpen: (id: string) => void }) {
  const [q, setQ] = useState("");
  const [query, setQuery] = useState("");
  const [showHandled, setShowHandled] = useState(false);
  const list = useLoad(() => get<InboxMessage[]>(query ? `/inbox/messages?q=${encodeURIComponent(query)}` : `/inbox/messages?category=${category}&all=${showHandled}`), [category, query, showHandled]);
  useEvents(["inbox."], () => void list.reload());
  useEffect(() => { const t = setTimeout(() => setQuery(q.trim()), 300); return () => clearTimeout(t); }, [q]);

  return (
    <Card>
      <div className="stack">
        <div className="row wrap">
          <div className="search grow">
            <Search size={15} />
            <input className="input" dir="auto" placeholder="Search all mail… sender, subject, words" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search mail" />
          </div>
          {!query && <label className="row small"><input type="checkbox" checked={showHandled} onChange={(e) => setShowHandled(e.target.checked)} /> Show handled</label>}
        </div>
        <ErrorNote error={list.error} />
        {list.data?.length === 0 && <Empty>{query ? `No mail mentions “${query}”.` : "Nothing here."}</Empty>}
        <ul className="list list-rows">
          {list.data?.map((m) => (
            <li key={m.id}>
              <button className={`file-row ${m.isRead ? "" : "unread"}`} onClick={() => onOpen(m.id)}>
                <Mail size={16} className="muted" />
                <span className="grow" style={{ minWidth: 0 }}>
                  <span className="row wrap" style={{ gap: 6 }}>
                    <strong dir="auto" className="ellipsis">{m.fromName || m.fromAddress}</strong>
                    {query && <Badge tone={TONES[m.category]}>{tr(LABELS[m.category])}</Badge>}
                    {m.handled && <Badge tone="good">handled</Badge>}
                  </span>
                  <span className="small ellipsis" dir="auto">{m.subject || "(no subject)"}</span>
                  <span className="small snippet" dir="auto">{m.snippet}</span>
                  {m.reason && <span className="meta">{m.reason}</span>}
                </span>
                <span className="meta nowrap">{timeAgo(m.receivedAt)}</span>
              </button>
            </li>
          ))}
        </ul>
      </div>
    </Card>
  );
}

function Reader({ id, onBack, onError }: { id: string; onBack: () => void; onError: (e: unknown) => void }) {
  const d = useLoad(() => get<MessageDetail>(`/inbox/messages/${id}`), [id]);
  const [reply, setReply] = useState<string | null>(null);
  const [asking, setAsking] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  useEvents(["inbox."], () => void d.reload());
  const back = <button className="btn btn-ghost btn-sm back" onClick={onBack}><ArrowLeft size={14} /> {tr("Inbox")}</button>;
  if (!d.data) return <div className="stack">{back}<ErrorNote error={d.error} /></div>;
  const { message: m, entities, drafts, fromSender } = d.data;

  const saveReply = async () => {
    try { await post<MailDraft>("/inbox/drafts", { replyTo: m.id, body: reply }); setReply(null); await d.reload(); } catch (e) { onError(e); }
  };
  const askJarvis = async () => {
    setAsking(true);
    setNote(null);
    try {
      const r = await post<{ reply: string }>("/chat", { text: `Draft a reply to email message ${m.id}. Read it first with inbox_read, keep it short, and save it as a draft — don't send it.` });
      setNote(r.reply);
      await d.reload();
    } catch (e) { onError(e); } finally { setAsking(false); }
  };

  return (
    <div className="stack">
      {back}
      <Card title={<span dir="auto">{m.subject || "(no subject)"}</span>} actions={<Badge tone={TONES[m.category]}>{tr(LABELS[m.category])}</Badge>}>
        <dl className="kv small">
          <dt>From</dt><dd dir="auto">{m.fromName ? `${m.fromName} <${m.fromAddress}>` : m.fromAddress}</dd>
          <dt>To</dt><dd dir="ltr">{m.to.join(", ")}{m.cc.length ? ` · cc ${m.cc.join(", ")}` : ""}</dd>
          <dt>Received</dt><dd>{formatTime(m.receivedAt)}</dd>
          <dt>Why here</dt><dd>{m.reason}{m.categorySource === "you" ? " (your choice)" : ""}</dd>
        </dl>
        <div className="row wrap" style={{ marginTop: 10 }}>
          <select className="input input-sm" value={m.category} aria-label="Move to"
            onChange={(e) => post(`/inbox/messages/${m.id}/category`, { category: e.target.value }).then(() => d.reload()).catch(onError)}>
            {(Object.keys(LABELS) as MailCategory[]).map((k) => <option key={k} value={k}>{tr(LABELS[k])}</option>)}
          </select>
          <button className="btn btn-sm" onClick={() => post(`/inbox/messages/${m.id}/handled`, { handled: !m.handled }).then(() => d.reload()).catch(onError)}>
            <Check size={14} /> {m.handled ? "Mark not handled" : "Mark handled"}
          </button>
          {entities.map((e) => <span key={e.id} className="chip"><Link2 size={11} /> {e.name}</span>)}
        </div>
        <p className="small muted" style={{ marginTop: 8 }}>Moving a message also moves future mail from this sender.</p>
      </Card>
      <Card title="Message" actions={<Badge title="Written by someone else. JARVIS treats it as information, never as instructions.">external</Badge>}>
        <pre className="file-preview mail-body" dir="auto">{m.body || m.snippet}</pre>
      </Card>
      <Card title="Reply" actions={
        <div className="row">
          <button className="btn btn-sm" disabled={asking} onClick={askJarvis}><Wand2 size={14} /> {asking ? "Drafting…" : "Draft with JARVIS"}</button>
          {reply === null && <button className="btn btn-sm" onClick={() => setReply("")}><PenLine size={14} /> Write</button>}
        </div>
      }>
        {note && <p className="small muted" dir="auto">{note}</p>}
        {reply !== null && (
          <div className="stack">
            <textarea className="input" rows={6} dir="auto" value={reply} onChange={(e) => setReply(e.target.value)} placeholder="Your reply…" />
            <div className="row"><button className="btn btn-primary btn-sm" disabled={!reply.trim()} onClick={saveReply}>Save draft</button><button className="btn btn-ghost btn-sm" onClick={() => setReply(null)}>Cancel</button></div>
          </div>
        )}
        {drafts.filter((x) => x.status !== "discarded").map((x) => <DraftEditor key={x.id} draft={x} onError={onError} onChange={() => d.reload()} />)}
        {reply === null && drafts.length === 0 && !note && <p className="small muted">No reply drafted yet.</p>}
      </Card>
      {fromSender.length > 0 && (
        <Card title={`Earlier from ${m.fromName || m.fromAddress}`}>
          <ul className="list">{fromSender.map((x) => <li key={x.id}><span className="small ellipsis grow" dir="auto">{x.subject}</span><span className="meta nowrap">{timeAgo(x.receivedAt)}</span></li>)}</ul>
        </Card>
      )}
    </div>
  );
}

function Drafts({ onError }: { onError: (e: unknown) => void }) {
  const list = useLoad(() => get<MailDraft[]>("/inbox/drafts"));
  const [creating, setCreating] = useState(false);
  const [n, setN] = useState({ to: "", cc: "", subject: "", body: "" });
  useEvents(["inbox."], () => void list.reload());
  const create = async () => {
    try { await post("/inbox/drafts", n); setN({ to: "", cc: "", subject: "", body: "" }); setCreating(false); await list.reload(); } catch (e) { onError(e); }
  };
  return (
    <div className="stack">
      <div className="row"><button className="btn btn-sm" onClick={() => setCreating((c) => !c)}><PenLine size={14} /> New email</button></div>
      {creating && (
        <Card title="New email">
          <div className="form-grid">
            <Field label="To"><input className="input" dir="ltr" value={n.to} onChange={(e) => setN({ ...n, to: e.target.value })} placeholder="name@example.com, …" /></Field>
            <Field label="Cc"><input className="input" dir="ltr" value={n.cc} onChange={(e) => setN({ ...n, cc: e.target.value })} /></Field>
          </div>
          <Field label="Subject"><input className="input" dir="auto" value={n.subject} onChange={(e) => setN({ ...n, subject: e.target.value })} /></Field>
          <Field label="Message"><textarea className="input" rows={6} dir="auto" value={n.body} onChange={(e) => setN({ ...n, body: e.target.value })} /></Field>
          <div className="row"><button className="btn btn-primary btn-sm" disabled={!n.to.trim()} onClick={create}>Save draft</button></div>
        </Card>
      )}
      {list.data?.length === 0 && !creating && <Empty>No drafts. Ask JARVIS to draft a reply, or write one.</Empty>}
      {list.data?.map((x) => <Card key={x.id}><DraftEditor draft={x} onError={onError} onChange={() => list.reload()} /></Card>)}
    </div>
  );
}

function DraftEditor({ draft, onError, onChange }: { draft: MailDraft; onError: (e: unknown) => void; onChange: () => void }) {
  const [d, setD] = useState({ to: draft.to.join(", "), cc: draft.cc.join(", "), subject: draft.subject, body: draft.body });
  const [sending, setSending] = useState(false);
  const [result, setResult] = useState<string | null>(null);
  const dirty = d.to !== draft.to.join(", ") || d.cc !== draft.cc.join(", ") || d.subject !== draft.subject || d.body !== draft.body;
  const editable = draft.status === "draft" || draft.status === "failed";
  const save = () => put(`/inbox/drafts/${draft.id}`, d).then(onChange).catch(onError);
  const send = async () => {
    setSending(true);
    setResult(null);
    try {
      if (dirty) await put(`/inbox/drafts/${draft.id}`, d);
      // Goes through the approval prompt like any critical action; it appears at the top of this page.
      const r = await post<{ success: boolean; message: string }>(`/inbox/drafts/${draft.id}/send`, {});
      setResult(r.message);
      onChange();
    } catch (e) { onError(e); } finally { setSending(false); }
  };
  return (
    <div className="stack draft">
      <div className="row wrap small">
        <Badge tone={draft.status === "sent" ? "good" : draft.status === "failed" ? "bad" : "neutral"}>{draft.status}</Badge>
        <span className="muted">{draft.createdBy === "jarvis" ? "Drafted by JARVIS" : "Your draft"} · {timeAgo(draft.updatedAt)}</span>
        {draft.error && <span className="bad">{draft.error}</span>}
      </div>
      <div className="form-grid">
        <Field label="To"><input className="input" dir="ltr" disabled={!editable} value={d.to} onChange={(e) => setD({ ...d, to: e.target.value })} /></Field>
        <Field label="Subject"><input className="input" dir="auto" disabled={!editable} value={d.subject} onChange={(e) => setD({ ...d, subject: e.target.value })} /></Field>
      </div>
      <textarea className="input" rows={6} dir="auto" disabled={!editable} value={d.body} onChange={(e) => setD({ ...d, body: e.target.value })} />
      {editable && (
        <div className="row wrap">
          <button className="btn btn-primary btn-sm" disabled={sending || !d.to.trim()} onClick={send}><Send size={14} /> {sending ? "Waiting for your approval…" : "Send…"}</button>
          {dirty && <button className="btn btn-sm" onClick={save}>Save</button>}
          <button className="btn btn-ghost btn-sm" onClick={() => del(`/inbox/drafts/${draft.id}`).then(onChange).catch(onError)}><Trash2 size={14} /> Discard</button>
        </div>
      )}
      {result && <p className="small" dir="auto">{result}</p>}
    </div>
  );
}

