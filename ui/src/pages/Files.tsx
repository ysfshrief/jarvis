import { Fragment, useEffect, useState } from "react";
import { ArrowLeft, FileText, FolderSearch, GitCompare, Link2, Play, RefreshCw, ScanText, Search, Settings2, Trash2 } from "lucide-react";
import { del, get, post, type FileComparison, type FileDetail, type FileHit, type FilesStatus, type IndexedFile } from "../api";
import { navigate } from "../App";
import { useEvents } from "../events";
import { Badge, Card, ConfirmButton, Empty, ErrorNote, Meter, PageHead, Segmented, fmtBytes, formatTime, timeAgo, useLoad } from "../components/ui";
import { tr } from "../lib/i18n";

const KINDS: [string, string][] = [["", "All"], ["pdf", "PDF"], ["document", "Docs"], ["spreadsheet", "Sheets"], ["presentation", "Slides"], ["image", "Images"], ["code", "Code"], ["text", "Text"]];

/** Local file knowledge: what JARVIS has read from your documents, searchable by words and meaning. */
export function FilesPage() {
  const [openId, setOpenId] = useState<string | null>(() => location.hash.split("/")[2] ?? null);
  const status = useLoad(() => get<FilesStatus>("/files/status"));
  const [error, setError] = useState<unknown>(null);
  useEvents(["files."], () => void status.reload());

  const open = (id: string | null) => { setOpenId(id); history.replaceState(null, "", id ? `#/files/${id}` : "#/files"); };
  if (openId) return <Detail id={openId} onBack={() => open(null)} onOpen={open} />;

  const s = status.data;
  const scan = () => post("/files/scan", {}).then(() => status.reload()).catch(setError);
  return (
    <div className="stack">
      <PageHead
        title={tr("Files")}
        sub="Documents JARVIS has read on this PC — ask “find the CityCrep proposal” or “what's my latest PDF?”. Nothing leaves your machine."
        actions={s?.enabled && (
          <>
            <button className="btn btn-sm" disabled={s.progress.running} onClick={scan}><RefreshCw size={14} /> {s.progress.running ? "Scanning…" : "Scan now"}</button>
            <button className="btn btn-ghost btn-sm" onClick={() => navigate("settings", "files")}><Settings2 size={14} /> Folders</button>
          </>
        )}
      />
      <ErrorNote error={error ?? status.error} />
      {s && !s.enabled && (
        <Card title="File knowledge is off">
          <p className="small muted">
            When you turn it on, JARVIS reads the documents in your chosen folders (Documents, Desktop and Downloads by default) and keeps a private
            index on this PC so it can find, summarise and compare them. Protected places — credential stores, system folders — are never read.
          </p>
          <div className="row"><button className="btn btn-primary btn-sm" onClick={() => navigate("settings", "files")}><Play size={14} /> Set up in Settings</button></div>
        </Card>
      )}
      {s?.enabled && <IndexStatus s={s} />}
      {s && s.files > 0 && <Browse onOpen={open} />}
      {s?.enabled && s.files === 0 && !s.progress.running && <Empty>Nothing indexed yet. Press “Scan now”.</Empty>}
    </div>
  );
}

function IndexStatus({ s }: { s: FilesStatus }) {
  const p = s.progress;
  return (
    <div className="grid-3">
      <Card title="Index">
        <dl className="kv small">
          <dt>Files</dt><dd>{s.files.toLocaleString()} <span className="muted">({s.withText.toLocaleString()} with text)</span></dd>
          <dt>Text</dt><dd>{fmtBytes(s.chars)}</dd>
          <dt>Updated</dt><dd>{s.lastIndexed ? timeAgo(s.lastIndexed) : "—"}</dd>
          <dt>Folders</dt><dd className="mono" dir="ltr">{s.roots.length ? s.roots.map((r) => <div key={r}>{r}</div>) : "none found"}</dd>
        </dl>
      </Card>
      <Card title="Scan" actions={p.running ? <Badge tone="info">running</Badge> : p.finishedAt ? <Badge tone="good">idle</Badge> : null}>
        {p.running ? (
          <div className="stack small">
            <Meter value={p.scanned ? (100 * (p.indexed + p.skipped)) / Math.max(p.scanned, 1) : 5} />
            <div className="muted">{p.scanned} checked · {p.indexed} read · {p.errors} unreadable</div>
            {p.current && <div className="mono ellipsis" dir="ltr" title={p.current}>{p.current}</div>}
          </div>
        ) : (
          <div className="small muted">
            {p.finishedAt ? <>Last scan {timeAgo(p.finishedAt)}: {p.scanned} checked, {p.indexed} new or changed{p.errors ? `, ${p.errors} unreadable` : ""}. Changes are picked up automatically.</> : "Waiting for the first scan."}
          </div>
        )}
      </Card>
      <Card title="Understanding">
        <dl className="kv small">
          <dt>Meaning</dt>
          <dd>{s.semantic.available ? <><Badge tone="good">on</Badge> {s.semantic.indexed.toLocaleString()} passages · {s.semantic.model}</> : <><Badge>words only</Badge> <span className="muted">{s.semantic.message}</span></>}</dd>
          <dt>Images</dt>
          <dd>{s.ocr.isAvailable ? <><Badge tone="good">OCR</Badge> {s.ocr.name}</> : <Badge>names only</Badge>}</dd>
          <dt>Kinds</dt>
          <dd className="row wrap">{Object.entries(s.kinds).map(([k, n]) => <span key={k} className="chip">{k} {n}</span>)}</dd>
        </dl>
      </Card>
    </div>
  );
}

function Browse({ onOpen }: { onOpen: (id: string) => void }) {
  const [q, setQ] = useState("");
  const [query, setQuery] = useState("");
  const [kind, setKind] = useState("");
  const latest = useLoad(() => get<IndexedFile[]>(`/files/latest?limit=30${kind ? `&kind=${kind}` : ""}`), [kind]);
  const hits = useLoad(() => (query ? get<FileHit[]>(`/files/search?q=${encodeURIComponent(query)}&limit=30${kind ? `&kind=${kind}` : ""}`) : Promise.resolve(null)), [query, kind]);
  useEffect(() => { const t = setTimeout(() => setQuery(q.trim()), 350); return () => clearTimeout(t); }, [q]);

  return (
    <Card>
      <div className="stack">
        <div className="row wrap">
          <div className="search grow">
            <Search size={15} />
            <input className="input" dir="auto" placeholder="Search by words or meaning… e.g. “renewal fee”, “عرض سيتي كريب”" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search files" />
          </div>
          <Segmented label="Kind" value={kind} onChange={setKind} options={KINDS} />
        </div>
        <ErrorNote error={hits.error ?? latest.error} />
        {query ? (
          hits.data?.length === 0 ? <Empty>No documents mention “{query}”.</Empty> : (
            <ul className="list list-rows">
              {hits.data?.map((h) => <FileRow key={h.file.id} f={h.file} snippet={h.snippet} semantic={h.semantic} onOpen={onOpen} />)}
            </ul>
          )
        ) : (
          <>
            <div className="meta">LATEST</div>
            <ul className="list list-rows">{latest.data?.map((f) => <FileRow key={f.id} f={f} onOpen={onOpen} />)}</ul>
          </>
        )}
      </div>
    </Card>
  );
}

function FileRow({ f, snippet, semantic, onOpen }: { f: IndexedFile; snippet?: string | null; semantic?: boolean; onOpen: (id: string) => void }) {
  return (
    <li>
      <button className="file-row" onClick={() => onOpen(f.id)}>
        <FileText size={16} className="muted" />
        <span className="grow" style={{ minWidth: 0 }}>
          <span className="row wrap" style={{ gap: 6 }}>
            <strong dir="auto" className="ellipsis">{f.name}</strong>
            <Badge>{f.kind}</Badge>
            {semantic && <Badge tone="accent" title="Matched by meaning, not exact words">meaning</Badge>}
            {f.status !== "ok" && <Badge tone="warn" title={f.note ?? undefined}>{f.status}</Badge>}
          </span>
          {f.title && f.title !== f.name && <span className="small muted ellipsis" dir="auto">{f.title}{f.author ? ` · ${f.author}` : ""}</span>}
          {snippet && <span className="small snippet" dir="auto">{snippet}</span>}
          <span className="meta ellipsis" dir="ltr">{f.path}</span>
        </span>
        <span className="meta nowrap">{timeAgo(f.modifiedAt)}</span>
      </button>
    </li>
  );
}

function Detail({ id, onBack, onOpen }: { id: string; onBack: () => void; onOpen: (id: string) => void }) {
  const data = useLoad(() => get<FileDetail>(`/files/${id}`), [id]);
  const [cmp, setCmp] = useState<FileComparison | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => setCmp(null), [id]);
  const back = <button className="btn btn-ghost btn-sm back" onClick={onBack}><ArrowLeft size={14} /> All files</button>;
  if (!data.data) return <div className="stack">{back}<ErrorNote error={data.error} /></div>;
  const { file: f, entities, keyPoints, preview, previous } = data.data;
  const compare = () => previous && get<FileComparison>(`/files/${f.id}/compare/${previous.id}`).then(setCmp).catch(setError);

  return (
    <div className="stack">
      {back}
      <PageHead title={f.title || f.name} sub={<span className="mono" dir="ltr">{f.path}</span>} />
      <ErrorNote error={error} />
      <div className="grid-2">
        <Card title="Details">
          <dl className="kv small">
            <dt>Kind</dt><dd>{f.kind} · {f.ext}</dd>
            <dt>Size</dt><dd>{fmtBytes(f.size)}{f.pages ? ` · ${f.pages} pages` : ""}</dd>
            <dt>Modified</dt><dd>{formatTime(f.modifiedAt)}</dd>
            {f.author && <><dt>Author</dt><dd dir="auto">{f.author}</dd></>}
            {f.project && <><dt>Project</dt><dd dir="auto">{f.project}</dd></>}
            <dt>Indexed</dt><dd>{timeAgo(f.indexedAt)} · {f.textChars.toLocaleString()} characters{f.note ? ` · ${f.note}` : ""}</dd>
            {Object.entries(f.metadata).slice(0, 8).map(([k, v]) => <Fragment key={k}><dt>{k}</dt><dd dir="auto">{v}</dd></Fragment>)}
          </dl>
          {entities.length > 0 && (
            <div className="row wrap" style={{ marginTop: 10 }}>
              {entities.map((e) => <button key={e.id} className="chip-btn" onClick={() => navigate("memory")}><Link2 size={12} /> {e.name}</button>)}
            </div>
          )}
        </Card>
        <Card title="Key points" actions={<Badge title="Sentences picked from the document itself, not written by an AI">extracted</Badge>}>
          {keyPoints.length ? <ul className="small bullets">{keyPoints.map((k, i) => <li key={i} dir="auto">{k}</li>)}</ul> : <p className="small muted">No readable sentences.</p>}
          <p className="small muted"><ScanText size={12} /> Ask JARVIS “summarise {f.name}” for an AI summary when a model is available.</p>
        </Card>
      </div>
      {previous && (
        <Card title="Earlier version" actions={!cmp && <button className="btn btn-sm" onClick={compare}><GitCompare size={14} /> Compare</button>}>
          <div className="row small"><button className="link" onClick={() => onOpen(previous.id)} dir="auto">{previous.name}</button> <span className="muted">modified {formatTime(previous.modifiedAt)}</span></div>
          {cmp && (cmp.identical ? <p className="small">Same text in both versions.</p> : (
            <div className="stack small" style={{ marginTop: 10 }}>
              <div>{cmp.added} line(s) added · {cmp.removed} removed · {cmp.unchanged} unchanged</div>
              <div className="diff" dir="auto">
                {cmp.removedLines.map((l, i) => <div key={`r${i}`} className="diff-del">− {l}</div>)}
                {cmp.addedLines.map((l, i) => <div key={`a${i}`} className="diff-add">+ {l}</div>)}
              </div>
            </div>
          ))}
        </Card>
      )}
      <Card title="Text">
        {preview ? <pre className="file-preview" dir="auto">{preview}</pre> : <p className="small muted">{f.note ?? "No text could be read from this file."}</p>}
      </Card>
    </div>
  );
}

/** Settings → Files: what gets indexed. */
export function FilesIndexHint() {
  return <p className="small muted"><FolderSearch size={12} /> Indexing reads files only; it never changes, moves or uploads them.</p>;
}

export function ClearIndexButton({ onDone }: { onDone?: () => void }) {
  return (
    <ConfirmButton className="btn btn-ghost btn-sm" prompt="Forget everything JARVIS read from your files? (Your files are not touched.)"
      onConfirm={async () => { await del("/files/index"); onDone?.(); }}>
      <Trash2 size={13} /> Clear index
    </ConfirmButton>
  );
}
