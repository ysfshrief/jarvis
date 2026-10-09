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
            <button className="btn btn-sm" disabled={s.progress.running} onClick={scan}><RefreshCw size={14} /> {s.progress.running ? tr("Scanning…") : tr("Scan now")}</button>
            <button className="btn btn-ghost btn-sm" onClick={() => navigate("settings", "files")}><Settings2 size={14} /> {tr("Folders")}</button>
          </>
        )}
      />
      <ErrorNote error={error ?? status.error} />
      {s && !s.enabled && (
        <Card title="File knowledge is off">
          <p className="small muted">
            {tr("When you turn it on, JARVIS reads the documents in your chosen folders (Documents, Desktop and Downloads by default) and keeps a private index on this PC so it can find, summarise and compare them. Protected places — credential stores, system folders — are never read.")}
          </p>
          <div className="row"><button className="btn btn-primary btn-sm" onClick={() => navigate("settings", "files")}><Play size={14} /> {tr("Set up in Settings")}</button></div>
        </Card>
      )}
      {s?.enabled && <IndexStatus s={s} />}
      {s && s.files > 0 && <Browse onOpen={open} />}
      {s?.enabled && s.files === 0 && !s.progress.running && <Empty>{tr("Nothing indexed yet. Press “Scan now”.")}</Empty>}
    </div>
  );
}

function IndexStatus({ s }: { s: FilesStatus }) {
  const p = s.progress;
  return (
    <div className="grid-3">
      <Card title="Index">
        <dl className="kv small">
          <dt>{tr("Files")}</dt><dd>{s.files.toLocaleString()} <span className="muted">({tr("{n} with text", { n: s.withText.toLocaleString() })})</span></dd>
          <dt>{tr("Text")}</dt><dd>{fmtBytes(s.chars)}</dd>
          <dt>{tr("Updated")}</dt><dd>{s.lastIndexed ? timeAgo(s.lastIndexed) : "—"}</dd>
          <dt>{tr("Folders")}</dt><dd className="mono" dir="ltr">{s.roots.length ? s.roots.map((r) => <div key={r}>{r}</div>) : tr("none found")}</dd>
        </dl>
      </Card>
      <Card title="Scan" actions={p.running ? <Badge tone="info">{tr("running")}</Badge> : p.finishedAt ? <Badge tone="good">{tr("idle")}</Badge> : null}>
        {p.running ? (
          <div className="stack small">
            <Meter value={p.scanned ? (100 * (p.indexed + p.skipped)) / Math.max(p.scanned, 1) : 5} />
            <div className="muted">{tr("{scanned} checked · {indexed} read · {errors} unreadable", { scanned: p.scanned, indexed: p.indexed, errors: p.errors })}</div>
            {p.current && <div className="mono ellipsis" dir="ltr" title={p.current}>{p.current}</div>}
          </div>
        ) : (
          <div className="small muted">
            {p.finishedAt
              ? p.errors
                ? tr("Last scan {time}: {scanned} checked, {indexed} new or changed, {errors} unreadable. Changes are picked up automatically.", { time: timeAgo(p.finishedAt), scanned: p.scanned, indexed: p.indexed, errors: p.errors })
                : tr("Last scan {time}: {scanned} checked, {indexed} new or changed. Changes are picked up automatically.", { time: timeAgo(p.finishedAt), scanned: p.scanned, indexed: p.indexed })
              : tr("Waiting for the first scan.")}
          </div>
        )}
      </Card>
      <Card title="Understanding">
        <dl className="kv small">
          <dt>{tr("Meaning")}</dt>
          <dd>{s.semantic.available ? <><Badge tone="good">{tr("on")}</Badge> {tr("{n} passages", { n: s.semantic.indexed.toLocaleString() })} · {s.semantic.model}</> : <><Badge>{tr("words only")}</Badge> <span className="muted">{s.semantic.message}</span></>}</dd>
          <dt>{tr("Images")}</dt>
          <dd>{s.ocr.isAvailable ? <><Badge tone="good">OCR</Badge> {s.ocr.name}</> : <Badge>{tr("names only")}</Badge>}</dd>
          <dt>{tr("Kinds")}</dt>
          <dd className="row wrap">{Object.entries(s.kinds).map(([k, n]) => <span key={k} className="chip">{tr(k)} {n}</span>)}</dd>
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
            <input className="input" dir="auto" placeholder={tr("Search by words or meaning… e.g. “renewal fee”, “عرض سيتي كريب”")} value={q} onChange={(e) => setQ(e.target.value)} aria-label={tr("Search files")} />
          </div>
          <Segmented label="Kind" value={kind} onChange={setKind} options={KINDS} />
        </div>
        <ErrorNote error={hits.error ?? latest.error} />
        {query ? (
          hits.data?.length === 0 ? <Empty>{tr("No documents mention “{query}”.", { query })}</Empty> : (
            <ul className="list list-rows">
              {hits.data?.map((h) => <FileRow key={h.file.id} f={h.file} snippet={h.snippet} semantic={h.semantic} onOpen={onOpen} />)}
            </ul>
          )
        ) : (
          <>
            <div className="meta">{tr("LATEST")}</div>
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
            <Badge>{tr(f.kind)}</Badge>
            {semantic && <Badge tone="accent" title={tr("Matched by meaning, not exact words")}>{tr("meaning")}</Badge>}
            {f.status !== "ok" && <Badge tone="warn" title={f.note ?? undefined}>{tr(f.status)}</Badge>}
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
  const back = <button className="btn btn-ghost btn-sm back" onClick={onBack}><ArrowLeft size={14} /> {tr("All files")}</button>;
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
            <dt>{tr("Kind")}</dt><dd>{tr(f.kind)} · {f.ext}</dd>
            <dt>{tr("Size")}</dt><dd>{fmtBytes(f.size)}{f.pages ? ` · ${tr("{n} pages", { n: f.pages })}` : ""}</dd>
            <dt>{tr("Modified")}</dt><dd>{formatTime(f.modifiedAt)}</dd>
            {f.author && <><dt>{tr("Author")}</dt><dd dir="auto">{f.author}</dd></>}
            {f.project && <><dt>{tr("Project")}</dt><dd dir="auto">{f.project}</dd></>}
            <dt>{tr("Indexed")}</dt><dd>{timeAgo(f.indexedAt)} · {tr("{n} characters", { n: f.textChars.toLocaleString() })}{f.note ? ` · ${f.note}` : ""}</dd>
            {Object.entries(f.metadata).slice(0, 8).map(([k, v]) => <Fragment key={k}><dt>{k}</dt><dd dir="auto">{v}</dd></Fragment>)}
          </dl>
          {entities.length > 0 && (
            <div className="row wrap" style={{ marginTop: 10 }}>
              {entities.map((e) => <button key={e.id} className="chip-btn" onClick={() => navigate("memory")}><Link2 size={12} /> {e.name}</button>)}
            </div>
          )}
        </Card>
        <Card title="Key points" actions={<Badge title={tr("Sentences picked from the document itself, not written by an AI")}>{tr("extracted")}</Badge>}>
          {keyPoints.length ? <ul className="small bullets">{keyPoints.map((k, i) => <li key={i} dir="auto">{k}</li>)}</ul> : <p className="small muted">{tr("No readable sentences.")}</p>}
          <p className="small muted"><ScanText size={12} /> {tr("Ask JARVIS “summarise {name}” for an AI summary when a model is available.", { name: f.name })}</p>
        </Card>
      </div>
      {previous && (
        <Card title="Earlier version" actions={!cmp && <button className="btn btn-sm" onClick={compare}><GitCompare size={14} /> {tr("Compare")}</button>}>
          <div className="row small"><button className="link" onClick={() => onOpen(previous.id)} dir="auto">{previous.name}</button> <span className="muted">{tr("modified {time}", { time: formatTime(previous.modifiedAt) })}</span></div>
          {cmp && (cmp.identical ? <p className="small">{tr("Same text in both versions.")}</p> : (
            <div className="stack small" style={{ marginTop: 10 }}>
              <div>{tr("{added} line(s) added · {removed} removed · {unchanged} unchanged", { added: cmp.added, removed: cmp.removed, unchanged: cmp.unchanged })}</div>
              <div className="diff" dir="auto">
                {cmp.removedLines.map((l, i) => <div key={`r${i}`} className="diff-del">− {l}</div>)}
                {cmp.addedLines.map((l, i) => <div key={`a${i}`} className="diff-add">+ {l}</div>)}
              </div>
            </div>
          ))}
        </Card>
      )}
      <Card title="Text">
        {preview ? <pre className="file-preview" dir="auto">{preview}</pre> : <p className="small muted">{f.note ?? tr("No text could be read from this file.")}</p>}
      </Card>
    </div>
  );
}

/** Settings → Files: what gets indexed. */
export function FilesIndexHint() {
  return <p className="small muted"><FolderSearch size={12} /> {tr("Indexing reads files only; it never changes, moves or uploads them.")}</p>;
}

export function ClearIndexButton({ onDone }: { onDone?: () => void }) {
  return (
    <ConfirmButton className="btn btn-ghost btn-sm" prompt="Forget everything JARVIS read from your files? (Your files are not touched.)"
      onConfirm={async () => { await del("/files/index"); onDone?.(); }}>
      <Trash2 size={13} /> {tr("Clear index")}
    </ConfirmButton>
  );
}
