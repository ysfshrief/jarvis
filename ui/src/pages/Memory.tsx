import { useState } from "react";
import { Pencil, Plus, Save, Search, Trash2, X } from "lucide-react";
import { del, get, post, put, type MemoryItem } from "../api";
import { useEvents } from "../events";
import { Badge, Card, ConfirmButton, Empty, ErrorNote, timeAgo, useLoad } from "../components/ui";

const KINDS = ["fact", "preference", "person", "project", "context", "pattern", "assumption"];
const SOURCE_LABEL: Record<string, string> = {
  user: "You told me",
  confirmed: "You confirmed",
  learned: "Learned (unconfirmed)",
  derived: "My inference",
};

export function MemoryPage() {
  const [q, setQ] = useState("");
  const [kind, setKind] = useState("");
  const query = `/memory?${new URLSearchParams({ ...(q ? { q } : {}), ...(kind ? { kind } : {}) })}`;
  const items = useLoad(() => get<MemoryItem[]>(query), [query]);
  const [error, setError] = useState<unknown>(null);
  useEvents(["memory"], () => void items.reload());

  return (
    <div className="stack">
      <div className="row between">
        <h2>Memory</h2>
        <ConfirmButton prompt="Delete ALL memories? This cannot be undone." onConfirm={() => del("/memory?confirm=true").catch(setError)}>
          <Trash2 size={16} /> Clear all
        </ConfirmButton>
      </div>
      <p className="muted">
        Everything JARVIS remembers lives only on this computer. Each item shows where it came from, so guesses are never confused with facts.
        Control what may be remembered in <a href="#/settings">Settings → Memory</a>.
      </p>

      <AddMemory onError={setError} />

      <Card>
        <div className="row wrap">
          <div className="input-icon grow">
            <Search size={16} />
            <input className="input" dir="auto" placeholder="Search memory…" value={q} onChange={(e) => setQ(e.target.value)} />
          </div>
          <select className="input" value={kind} onChange={(e) => setKind(e.target.value)} aria-label="Kind">
            <option value="">All kinds</option>
            {KINDS.map((k) => <option key={k}>{k}</option>)}
          </select>
        </div>
        <ErrorNote error={error ?? items.error} />
        {items.data && items.data.length === 0 && <Empty>{q ? "Nothing matches." : "No memories yet. Say “remember that …” / “افتكر إن …”."}</Empty>}
        <ul className="list list-rows">
          {items.data?.map((m) => <MemoryRow key={m.id} m={m} onError={setError} />)}
        </ul>
      </Card>
    </div>
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
          {KINDS.map((k) => <option key={k}>{k}</option>)}
        </select>
        <button className="btn btn-primary" disabled={!content.trim()} type="submit"><Plus size={16} /> Remember</button>
      </form>
    </Card>
  );
}

function MemoryRow({ m, onError }: { m: MemoryItem; onError: (e: unknown) => void }) {
  const [editing, setEditing] = useState(false);
  const [content, setContent] = useState(m.content);
  const [kind, setKind] = useState(m.kind);
  const save = async () => {
    try {
      await put(`/memory/${m.id}`, { content, kind, source: m.source === "learned" || m.source === "derived" ? "confirmed" : m.source });
      setEditing(false);
    } catch (e) {
      onError(e);
    }
  };
  return (
    <li>
      <div className="grow">
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
        <div className="row small muted wrap">
          <Badge tone="accent">{m.kind}</Badge>
          <Badge tone={m.source === "user" || m.source === "confirmed" ? "good" : "warn"} title={`Confidence ${Math.round(m.confidence * 100)}%`}>
            {SOURCE_LABEL[m.source] ?? m.source} · {Math.round(m.confidence * 100)}%
          </Badge>
          <span>updated {timeAgo(m.updatedAt)}</span>
          {m.useCount > 0 && <span>used {m.useCount}×</span>}
        </div>
      </div>
      {editing ? (
        <>
          <button className="btn btn-primary" onClick={save} aria-label="Save"><Save size={16} /></button>
          <button className="btn btn-ghost" onClick={() => setEditing(false)} aria-label="Cancel"><X size={16} /></button>
        </>
      ) : (
        <>
          <button className="btn btn-ghost" onClick={() => setEditing(true)} aria-label="Edit"><Pencil size={16} /></button>
          <button className="btn btn-ghost" onClick={() => del(`/memory/${m.id}`).catch(onError)} aria-label="Delete"><Trash2 size={16} /></button>
        </>
      )}
    </li>
  );
}
