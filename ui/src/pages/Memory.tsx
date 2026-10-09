import { useEffect, useState } from "react";
import { Brain, Check, ChevronDown, ChevronRight, GraduationCap, Link2, Pencil, Plus, RefreshCw, Save, Search, Sparkles, Trash2, Users, X } from "lucide-react";
import { del, get, post, put, type Entity, type EntityProfile, type MemoryItem, type MemoryStatus } from "../api";
import { useEvents } from "../events";
import { Badge, Card, ConfirmButton, Empty, ErrorNote, formatTime, PageHead, Segmented, timeAgo, useLoad } from "../components/ui";
import { tr } from "../lib/i18n";

const KINDS = ["fact", "preference", "person", "project", "context", "pattern", "assumption"];
const ENTITY_TYPES = ["person", "organization", "project", "place", "file", "event", "topic"];
const SOURCE_LABEL: Record<string, string> = {
  user: "You told me",
  confirmed: "You confirmed",
  learned: "Learned — unconfirmed",
  derived: "Inferred — unconfirmed",
};

type Tab = "memories" | "review" | "people";

/**
 * What JARVIS knows, where each piece came from, and what it only suspects. Inferred items never
 * become facts until you confirm them here.
 */
export function MemoryPage() {
  const [tab, setTab] = useState<Tab>("memories");
  const [openEntity, setOpenEntity] = useState<string | null>(null);
  const status = useLoad(() => get<MemoryStatus>("/memory/status"));
  const [error, setError] = useState<unknown>(null);
  const [learnMsg, setLearnMsg] = useState<string | null>(null);
  useEvents(["memory"], () => void status.reload());

  const showEntity = (id: string) => { setOpenEntity(id); setTab("people"); };
  const s = status.data;

  const analyze = async () => {
    try {
      const r = await post<{ proposed: number; updated: number; skippedRejected: number }>("/memory/learn");
      setLearnMsg(r.proposed + r.updated === 0 ? "No new patterns in the last two weeks." : `${r.proposed} new and ${r.updated} updated pattern(s) to review.`);
      if (r.proposed) setTab("review");
    } catch (e) { setError(e); }
  };

  return (
    <div className="stack">
      <PageHead
        title={tr("Memory")}
        sub="Everything lives only on this computer. Each item shows where it came from; guesses stay separate from facts until you confirm them."
        actions={
          <>
            <button className="btn btn-ghost btn-sm" onClick={analyze} title="Look for patterns in the last two weeks of activity"><Sparkles size={14} /> Analyze my activity</button>
            <ConfirmButton className="btn btn-danger btn-sm" prompt="Delete ALL memories? This cannot be undone." onConfirm={() => del("/memory?confirm=true").catch(setError)}>
              <Trash2 size={14} /> Clear all
            </ConfirmButton>
          </>
        }
      />
      <ErrorNote error={error} />
      {learnMsg && <div className="note">{learnMsg}</div>}

      <div className="grid-4">
        <Stat label="Confirmed facts" value={s?.confirmed} />
        <Stat label="To review" value={s?.inferred} tone={s && s.inferred > 0 ? "warn" : undefined} />
        <Stat label="People & things" value={s?.entities} />
        <Card className="metric-card" title="Semantic search">
          <div className="row">
            <span className={`dot ${s?.semantic.available ? "dot-ok" : "dot-unknown"}`} />
            <strong className="small">{s?.semantic.available ? s.semantic.model : "Keyword only"}</strong>
            {s?.semantic.available && (
              <button className="btn btn-ghost btn-icon" title="Re-index now" aria-label="Re-index" onClick={() => post("/memory/reindex").then(() => status.reload()).catch(setError)}><RefreshCw size={13} /></button>
            )}
          </div>
          <div className="meta">{s?.semantic.message}</div>
          {!s?.semantic.available && <a className="link small" href="#/settings/ai">Get the free model →</a>}
        </Card>
      </div>

      <LearnCard onDone={() => setTab("review")} onError={setError} />

      <Segmented label="View" value={tab} onChange={setTab} options={[["memories", "Memories"], ["review", `To review${s?.inferred ? ` (${s.inferred})` : ""}`], ["people", "People & things"]]} />

      {tab === "memories" && <Memories onEntity={showEntity} onError={setError} />}
      {tab === "review" && <Review learning={!!s?.learning} onEntity={showEntity} onError={setError} />}
      {tab === "people" && <People open={openEntity} setOpen={setOpenEntity} onError={setError} />}
    </div>
  );
}

function Stat({ label, value, tone }: { label: string; value: number | undefined; tone?: "warn" }) {
  return (
    <Card className="metric-card" title={label}>
      <div className="metric-value" style={tone === "warn" ? { color: "var(--warn)" } : undefined}>{value ?? "—"}</div>
    </Card>
  );
}

function Memories({ onEntity, onError }: { onEntity: (id: string) => void; onError: (e: unknown) => void }) {
  const [q, setQ] = useState("");
  const [kind, setKind] = useState("");
  const [query, setQuery] = useState("");
  useEffect(() => {
    const t = window.setTimeout(() => setQuery(q), 250);
    return () => window.clearTimeout(t);
  }, [q]);
  const path = `/memory?${new URLSearchParams({ ...(query ? { q: query } : {}), ...(kind ? { kind } : {}) })}`;
  const items = useLoad(() => get<MemoryItem[]>(path), [path]);
  useEvents(["memory"], () => void items.reload());

  return (
    <>
      <AddMemory onError={onError} />
      <Card>
        <div className="row wrap">
          <div className="input-icon grow">
            <Search size={16} />
            <input className="input" dir="auto" placeholder="Search by words or meaning…" value={q} onChange={(e) => setQ(e.target.value)} />
          </div>
          <select className="input" value={kind} onChange={(e) => setKind(e.target.value)} aria-label="Kind">
            <option value="">All kinds</option>
            {KINDS.map((k) => <option key={k}>{k}</option>)}
          </select>
        </div>
        <ErrorNote error={items.error} />
        {items.data && items.data.length === 0 && <Empty>{q ? "Nothing matches." : "No memories yet. Say “remember that …” / “افتكر إن …”."}</Empty>}
        <ul className="list list-rows">
          {items.data?.map((m) => <MemoryRow key={m.id} m={m} onEntity={onEntity} onError={onError} />)}
        </ul>
      </Card>
    </>
  );
}

function Review({ learning, onEntity, onError }: { learning: boolean; onEntity: (id: string) => void; onError: (e: unknown) => void }) {
  const items = useLoad(() => get<MemoryItem[]>("/memory?confirmed=false&limit=200"));
  useEvents(["memory"], () => void items.reload());
  return (
    <Card title="Things JARVIS suspects but hasn't confirmed">
      <p className="small muted">
        Confirm what's right (it becomes a fact JARVIS can rely on) and reject what's wrong (it's deleted and never suggested again). Until then these are only
        hints, labelled as such when the AI sees them.
      </p>
      {!learning && (
        <div className="note">
          Learning from your activity is off. Turn it on in <a href="#/settings/memory">Settings → Memory</a> to let JARVIS notice routines (apps you open at certain
          times, your preferred language, how long you like answers). It only reads JARVIS's own activity log.
        </div>
      )}
      {items.data?.length === 0 && <Empty>Nothing to review.</Empty>}
      <ul className="list list-rows">
        {items.data?.map((m) => <MemoryRow key={m.id} m={m} onEntity={onEntity} onError={onError} review />)}
      </ul>
    </Card>
  );
}

function AddMemory({ onError }: { onError: (e: unknown) => void }) {
  const [content, setContent] = useState("");
  const [kind, setKind] = useState("fact");
  const [subject, setSubject] = useState("");
  const add = async () => {
    try {
      await post("/memory", { content, kind, subject: subject || null });
      setContent("");
      setSubject("");
    } catch (e) {
      onError(e);
    }
  };
  return (
    <Card title="Teach JARVIS something">
      <form className="row wrap" onSubmit={(e) => { e.preventDefault(); void add(); }}>
        <input className="input grow" dir="auto" placeholder="e.g. Ahmed is the CityCrep account manager" value={content} onChange={(e) => setContent(e.target.value)} />
        <input className="input" dir="auto" placeholder="About (optional)" value={subject} onChange={(e) => setSubject(e.target.value)} />
        <select className="input" value={kind} onChange={(e) => setKind(e.target.value)} aria-label="Kind">
          {KINDS.filter((k) => k !== "assumption").map((k) => <option key={k}>{k}</option>)}
        </select>
        <button className="btn btn-primary" disabled={!content.trim()} type="submit"><Plus size={15} /> Remember</button>
      </form>
    </Card>
  );
}

function MemoryRow({ m, onEntity, onError, review }: { m: MemoryItem; onEntity: (id: string) => void; onError: (e: unknown) => void; review?: boolean }) {
  const [editing, setEditing] = useState(false);
  const [why, setWhy] = useState(!!review);
  const [content, setContent] = useState(m.content);
  const [kind, setKind] = useState(m.kind);
  const save = async () => {
    try {
      await put(`/memory/${m.id}`, { content, kind });
      setEditing(false);
    } catch (e) {
      onError(e);
    }
  };
  const p = m.provenance;
  return (
    <li style={{ alignItems: "flex-start" }}>
      <div className="grow stack-sm">
        {editing ? (
          <div className="row wrap">
            <input className="input grow" dir="auto" value={content} onChange={(e) => setContent(e.target.value)} />
            <select className="input" value={kind} onChange={(e) => setKind(e.target.value)}>
              {KINDS.map((k) => <option key={k}>{k}</option>)}
            </select>
          </div>
        ) : (
          <div dir="auto">
            {m.subject && <strong>{m.subject}: </strong>}
            {m.content}
          </div>
        )}
        <div className="row small wrap">
          <Badge tone="accent">{m.kind}</Badge>
          <Badge tone={m.isConfirmed ? "good" : "warn"} title={`Confidence ${Math.round(m.confidence * 100)}%`}>
            {SOURCE_LABEL[m.source] ?? m.source} · {Math.round(m.confidence * 100)}%
          </Badge>
          {m.semantic && <Badge tone="info" title="Found by meaning, not just matching words">semantic</Badge>}
          {hasTag(m, "research") && <Badge tone="info" title="Learned from a web page or document — check the source before confirming">from a source</Badge>}
          {hasTag(m, "conflict") && <Badge tone="bad" title="Contradicts something JARVIS already had — see “Why is this here?”">conflict</Badge>}
          {m.entities.map((e) => (
            <button key={e.id} className="link small" onClick={() => onEntity(e.id)} title={e.type}><Link2 size={11} /> {e.name}</button>
          ))}
          <span className="meta">updated {timeAgo(m.updatedAt)}{m.useCount > 0 ? ` · used ${m.useCount}×` : ""}</span>
          <button className="link small" onClick={() => setWhy((w) => !w)}>{why ? <ChevronDown size={12} /> : <ChevronRight size={12} />} Why is this here?</button>
        </div>
        {why && (
          <div className="note small">
            <div>
              <strong>{p ? viaLabel(p.via) : "Stored before provenance was tracked."}</strong>
              {" · "}{formatTime(m.createdAt)}
              {m.confirmedAt && <> · confirmed {timeAgo(m.confirmedAt)}</>}
            </div>
            {p?.quote && (p.via === "research"
              ? <div className="mono small" dir="ltr">Source: {/^https?:\/\//.test(p.quote) ? <a className="link" href={p.quote} target="_blank" rel="noreferrer noopener">{p.quote}</a> : p.quote}</div>
              : <div dir="auto">You said: “{p.quote}”</div>)}
            {p?.reason && <div dir="auto">Evidence: {p.reason}</div>}
            {p?.tool && <div className="meta">via tool {p.tool}{p.conversationId ? ` · conversation ${p.conversationId}` : ""}</div>}
          </div>
        )}
      </div>
      {!m.isConfirmed && !editing && (
        <>
          <button className="btn btn-sm" onClick={() => post(`/memory/${m.id}/confirm`).catch(onError)} title="This is right — keep it as a fact"><Check size={14} /> Confirm</button>
          <button className="btn btn-ghost btn-sm" onClick={() => post(`/memory/${m.id}/reject`).catch(onError)} title="This is wrong — delete it and don't suggest it again"><X size={14} /> Reject</button>
        </>
      )}
      {editing ? (
        <>
          <button className="btn btn-primary btn-icon" onClick={save} aria-label="Save"><Save size={15} /></button>
          <button className="btn btn-ghost btn-icon" onClick={() => setEditing(false)} aria-label="Cancel"><X size={15} /></button>
        </>
      ) : (
        <>
          <button className="btn btn-ghost btn-icon" onClick={() => setEditing(true)} aria-label="Edit"><Pencil size={15} /></button>
          <button className="btn btn-ghost btn-icon" onClick={() => del(`/memory/${m.id}`).catch(onError)} aria-label="Delete"><Trash2 size={15} /></button>
        </>
      )}
    </li>
  );
}

function hasTag(m: MemoryItem, tag: string) {
  return (m.tags ?? "").split(/[ ,]+/).includes(tag);
}

/** Research a topic or learn from one page/document; results land in "To review" with their sources. */
function LearnCard({ onDone, onError }: { onDone: () => void; onError: (e: unknown) => void }) {
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<{ success: boolean; message: string; status: string } | null>(null);
  const isSource = /^https?:\/\//i.test(text.trim()) || /^([a-z]:\\|\/|~)/i.test(text.trim());
  const run = async () => {
    setBusy(true);
    setResult(null);
    try {
      const r = await post<{ success: boolean; message: string; status: string }>("/memory/research", isSource ? { source: text.trim() } : { topic: text.trim() });
      setResult(r);
      if (r.success) onDone();
    } catch (e) { onError(e); }
    finally { setBusy(false); }
  };
  return (
    <Card title={<><GraduationCap size={14} /> {tr("Learn something")}</>}>
      <p className="small muted">
        {tr("A topic to research on the web, or a web address / file path to learn from. JARVIS reads the sources, keeps the key facts with where they came from, and flags anything that contradicts what it already knows. Nothing becomes a fact until you confirm it. Needs an AI model.")}
      </p>
      <form className="row" onSubmit={(e) => { e.preventDefault(); if (text.trim().length > 1 && !busy) void run(); }}>
        <input className="input grow" dir="auto" value={text} onChange={(e) => setText(e.target.value)} placeholder={tr("e.g. CityCrep's competitors · https://… · C:\\Docs\\brief.pdf")} />
        <button className="btn btn-primary btn-sm" disabled={busy || text.trim().length < 2}>{busy ? tr("Reading…") : isSource ? tr("Learn from it") : tr("Research")}</button>
      </form>
      {result && <pre className={`note small${result.success ? "" : " warn"}`} dir="auto" style={{ whiteSpace: "pre-wrap", fontFamily: "inherit" }}>{result.message}</pre>}
    </Card>
  );
}

function viaLabel(via: string) {
  switch (via) {
    case "text": case "chat": return "You told me in a conversation";
    case "voice": return "You told me by voice";
    case "dashboard": return "Added in the dashboard";
    case "learner": return "Noticed in your activity or the email you sent (opt-in learning)";
    case "research": return "Learned from a source — not verified";
    default: return `Added via ${via}`;
  }
}

function People({ open, setOpen, onError }: { open: string | null; setOpen: (id: string | null) => void; onError: (e: unknown) => void }) {
  const [type, setType] = useState("");
  const [q, setQ] = useState("");
  const list = useLoad(() => get<Entity[]>(`/entities?${new URLSearchParams({ ...(type ? { type } : {}), ...(q ? { q } : {}) })}`), [type, q]);
  useEvents(["memory"], () => void list.reload());
  const [name, setName] = useState("");
  const [newType, setNewType] = useState("person");

  return (
    <div className="grid-2" style={{ alignItems: "start" }}>
      <Card title="People & things">
        <div className="row wrap">
          <div className="input-icon grow"><Search size={15} /><input className="input" dir="auto" placeholder="Find…" value={q} onChange={(e) => setQ(e.target.value)} /></div>
          <select className="input" value={type} onChange={(e) => setType(e.target.value)} aria-label="Type">
            <option value="">All types</option>
            {ENTITY_TYPES.map((t) => <option key={t}>{t}</option>)}
          </select>
        </div>
        {list.data?.length === 0 && <Empty>Nobody yet. Say “Ahmed works at CityCrep” or add one below.</Empty>}
        <ul className="list">
          {list.data?.map((e) => (
            <li key={e.id}>
              <button className="link grow" style={{ textAlign: "start", color: open === e.id ? "var(--accent)" : "var(--text)" }} onClick={() => setOpen(e.id)} dir="auto">
                {e.type === "person" ? <Users size={13} /> : <Brain size={13} />} {e.name}
              </button>
              <Badge>{e.type}</Badge>
              <span className="meta">{e.memories ?? 0}</span>
            </li>
          ))}
        </ul>
        <form className="row" onSubmit={async (ev) => { ev.preventDefault(); try { const e = await post<Entity>("/entities", { name, type: newType }); setName(""); setOpen(e.id); } catch (err) { onError(err); } }}>
          <input className="input grow" dir="auto" placeholder="Add a person, company, project…" value={name} onChange={(e) => setName(e.target.value)} />
          <select className="input" value={newType} onChange={(e) => setNewType(e.target.value)} aria-label="Type">{ENTITY_TYPES.map((t) => <option key={t}>{t}</option>)}</select>
          <button className="btn btn-sm" disabled={!name.trim()}><Plus size={13} /> Add</button>
        </form>
      </Card>
      {open ? <Profile id={open} onClose={() => setOpen(null)} onError={onError} /> : <Card><Empty>Select someone or something to see everything connected to it.</Empty></Card>}
    </div>
  );
}

function Profile({ id, onClose, onError }: { id: string; onClose: () => void; onError: (e: unknown) => void }) {
  const p = useLoad(() => get<EntityProfile>(`/entities/${id}`), [id]);
  useEvents(["memory", "tasks"], () => void p.reload());
  const [rel, setRel] = useState({ relation: "works_at", to: "", toType: "organization" });
  const [aliases, setAliases] = useState<string | null>(null);
  if (!p.data) return <Card><ErrorNote error={p.error} /></Card>;
  const { entity, memories, relations, tasks, reminders, files } = p.data;
  return (
    <Card title={<span dir="auto">{entity.name}</span>} actions={
      <>
        <Badge tone="accent">{entity.type}</Badge>
        <ConfirmButton className="btn btn-ghost btn-icon" prompt={`Delete ${entity.name}? Its relationships are removed; memories stay.`} onConfirm={() => del(`/entities/${id}`).then(onClose).catch(onError)}><Trash2 size={14} /></ConfirmButton>
      </>
    }>
      <div className="row wrap small">
        <span className="muted">Also known as:</span>
        {aliases === null ? (
          <>
            <span dir="auto">{entity.aliases.join(", ") || "—"}</span>
            <button className="link small" onClick={() => setAliases(entity.aliases.join(", "))}>edit</button>
          </>
        ) : (
          <form className="row grow" onSubmit={async (e) => { e.preventDefault(); await put(`/entities/${id}`, { aliases: aliases.split(",").map((a) => a.trim()).filter(Boolean) }).catch(onError); setAliases(null); void p.reload(); }}>
            <input className="input grow" dir="auto" value={aliases} onChange={(e) => setAliases(e.target.value)} placeholder="e.g. أحمد, Ahmed M." />
            <button className="btn btn-sm">Save</button>
          </form>
        )}
      </div>

      <div className="hud-label">Relationships</div>
      {relations.length === 0 ? <div className="small muted">None recorded.</div> : (
        <ul className="list">
          {relations.map((r) => (
            <li key={r.id}>
              <span className="small" dir="auto">{r.fromName} <span className="mono accent">{r.type.replace(/_/g, " ")}</span> {r.toName}</span>
              <button className="btn btn-ghost btn-icon" aria-label="Remove relationship" onClick={() => del(`/relations/${r.id}`).then(() => p.reload()).catch(onError)}><X size={13} /></button>
            </li>
          ))}
        </ul>
      )}
      <form className="row wrap" onSubmit={async (e) => { e.preventDefault(); try { await post("/relations", { from: entity.name, fromType: entity.type, relation: rel.relation, to: rel.to, toType: rel.toType }); setRel({ ...rel, to: "" }); } catch (err) { onError(err); } }}>
        <span className="small" dir="auto">{entity.name}</span>
        <select className="input input-sm" value={rel.relation} onChange={(e) => setRel({ ...rel, relation: e.target.value })} aria-label="Relationship">
          {["works_at", "manages", "reports_to", "client_of", "member_of", "owns", "partner_of", "related_to"].map((x) => <option key={x} value={x}>{x.replace(/_/g, " ")}</option>)}
        </select>
        <input className="input input-sm grow" style={{ minWidth: 140 }} dir="auto" placeholder="who / what" value={rel.to} onChange={(e) => setRel({ ...rel, to: e.target.value })} />
        <select className="input input-sm" value={rel.toType} onChange={(e) => setRel({ ...rel, toType: e.target.value })} aria-label="Type">{ENTITY_TYPES.map((t) => <option key={t}>{t}</option>)}</select>
        <button className="btn btn-sm" disabled={!rel.to.trim()}><Plus size={13} /> Link</button>
      </form>

      <div className="hud-label">What JARVIS knows</div>
      {memories.length === 0 ? <div className="small muted">No memories linked yet.</div> : (
        <ul className="list">
          {memories.map((m) => (
            <li key={m.id}>
              <span className="small grow" dir="auto">{m.content}</span>
              <Badge tone={m.isConfirmed ? "good" : "warn"}>{m.isConfirmed ? "fact" : "unconfirmed"}</Badge>
            </li>
          ))}
        </ul>
      )}

      {files.length > 0 && (
        <>
          <div className="hud-label">Documents</div>
          <ul className="list">
            {files.map((f) => (
              <li key={f.id}>
                <a className="small grow ellipsis" href={`#/files/${f.id}`} dir="auto">{f.name}</a>
                <span className="meta nowrap">{timeAgo(f.modifiedAt)}</span>
              </li>
            ))}
          </ul>
        </>
      )}

      {(tasks.length > 0 || reminders.length > 0) && (
        <>
          <div className="hud-label">Related work</div>
          <ul className="list">
            {tasks.map((t) => <li key={t.id}><span className="small grow" dir="auto">{t.title}</span><Badge>{t.state.replace("_", " ")}</Badge></li>)}
            {reminders.map((r) => <li key={r.id}><span className="small grow" dir="auto">⏰ {r.text}</span><span className="meta" dir="ltr">{formatTime(r.dueAt)}</span></li>)}
          </ul>
        </>
      )}
    </Card>
  );
}
