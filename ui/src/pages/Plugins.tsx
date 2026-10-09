import { useState } from "react";
import { ArrowLeft, Code2, FlaskConical, PackagePlus, Power, ShieldCheck, Trash2, Wand2 } from "lucide-react";
import { del, get, post, type PluginView } from "../api";
import { useEvents } from "../events";
import { ApprovalCard, useApprovals } from "../components/Approvals";
import { Badge, Card, ConfirmButton, Empty, ErrorNote, Field, PageHead, RiskBadge, timeAgo, useLoad } from "../components/ui";
import { tr } from "../lib/i18n";

const tone = (s: string) => (s === "installed" ? "good" : s === "ready" ? "info" : s === "failed" || s === "changed" ? "bad" : "neutral");

/** Sandboxed extensions: written by JARVIS or imported, installed only with your approval. */
export function PluginsPage() {
  const list = useLoad(() => get<PluginView[]>("/plugins"));
  const [openId, setOpenId] = useState<string | null>(null);
  const [desc, setDesc] = useState("");
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const [importing, setImporting] = useState(false);
  const [error, setError] = useState<unknown>(null);
  useEvents(["plugins."], () => void list.reload());

  const generate = async () => {
    setBusy(true);
    setNote(null);
    try {
      const r = await post<{ success: boolean; message: string; data?: { plugin?: string } }>("/plugins/generate", { description: desc });
      setNote(r.message);
      if (r.data?.plugin) setOpenId(r.data.plugin);
      await list.reload();
    } catch (e) { setError(e); } finally { setBusy(false); }
  };

  if (openId) return <Detail id={openId} onBack={() => setOpenId(null)} onError={setError} />;
  return (
    <div className="stack">
      <PageHead title={tr("Plugins")} sub="New abilities, each in a sandbox: no files, apps or JARVIS data — only what you approve." />
      <ErrorNote error={error ?? list.error} />
      <Card title="Make a plugin">
        <p className="small muted">Describe a tool and JARVIS writes it, checks it and runs its tests with the network off. You then see exactly what it may do and decide whether to install it.</p>
        <Field label="What should it do?"><textarea className="input" rows={3} dir="auto" value={desc} onChange={(e) => setDesc(e.target.value)} placeholder="e.g. convert currencies using a free exchange-rate API" /></Field>
        <div className="row wrap">
          <button className="btn btn-primary btn-sm" disabled={busy || desc.trim().length < 8} onClick={generate}><Wand2 size={14} /> {busy ? "Writing and checking…" : "Write it"}</button>
          <button className="btn btn-ghost btn-sm" onClick={() => setImporting((x) => !x)}><PackagePlus size={14} /> Import</button>
        </div>
        {note && <p className="small" dir="auto">{note}</p>}
      </Card>
      {importing && <Import onDone={(id) => { setImporting(false); setOpenId(id); }} onError={setError} />}
      {list.data?.length === 0 && <Empty>No plugins yet.</Empty>}
      <ul className="list list-rows">
        {list.data?.map((p) => (
          <li key={p.id}>
            <button className="file-row" onClick={() => setOpenId(p.id)}>
              <Code2 size={16} className="muted" />
              <span className="grow" style={{ minWidth: 0 }}>
                <strong className="ellipsis">{p.manifest.name} <span className="meta">{p.manifest.version}</span></strong>
                <span className="small muted ellipsis" dir="auto">{p.manifest.description}</span>
              </span>
              <Badge tone={tone(p.status)}>{p.status}</Badge>
            </button>
          </li>
        ))}
      </ul>
    </div>
  );
}

function Import({ onDone, onError }: { onDone: (id: string) => void; onError: (e: unknown) => void }) {
  const [manifest, setManifest] = useState("");
  const [code, setCode] = useState("");
  const go = async () => {
    try { const p = await post<PluginView>("/plugins/import", { manifest, code }); onDone(p.id); } catch (e) { onError(e); }
  };
  return (
    <Card title="Import a plugin">
      <Field label="plugin.json"><textarea className="input mono" rows={8} dir="ltr" value={manifest} onChange={(e) => setManifest(e.target.value)} /></Field>
      <Field label="main.js"><textarea className="input mono" rows={8} dir="ltr" value={code} onChange={(e) => setCode(e.target.value)} /></Field>
      <div className="row"><button className="btn btn-primary btn-sm" disabled={!manifest.trim() || !code.trim()} onClick={go}>Check it</button></div>
    </Card>
  );
}

function Detail({ id, onBack, onError }: { id: string; onBack: () => void; onError: (e: unknown) => void }) {
  const p = useLoad(() => get<PluginView>(`/plugins/${id}`), [id]);
  const approvals = useApprovals().filter((a) => a.tool === "plugin_install");
  const [msg, setMsg] = useState<string | null>(null);
  const [showCode, setShowCode] = useState(false);
  useEvents(["plugins."], () => void p.reload());
  const back = <button className="btn btn-ghost btn-sm back" onClick={onBack}><ArrowLeft size={14} /> {tr("Plugins")}</button>;
  if (!p.data) return <div className="stack">{back}<ErrorNote error={p.error} /></div>;
  const x = p.data;
  const hosts = [...x.manifest.permissions.http, ...x.manifest.permissions.httpSend];
  const check = (network: boolean) => post(`/plugins/${id}/check`, { network }).then(() => p.reload()).catch(onError);
  const install = async () => { try { const r = await post<{ message: string }>(`/plugins/${id}/install`, {}); setMsg(r.message); await p.reload(); } catch (e) { onError(e); } };
  return (
    <div className="stack">
      {back}
      <PageHead title={x.manifest.name} sub={`${x.manifest.version} · ${x.source === "generated" ? "written by JARVIS" : "imported"} · ${timeAgo(x.createdAt)}`}
        actions={<Badge tone={tone(x.status)}>{x.status}</Badge>} />
      {x.error && <div className="note warn small">{x.error}</div>}
      <Card title="What it may do" actions={<ShieldCheck size={15} className="muted" />}>
        <p className="small" dir="auto">{x.manifest.description}</p>
        <p className="small">{x.permissions}</p>
        <ul className="list">
          {x.tools.map((t) => <li key={t.name}><span className="small grow"><span className="mono">{t.name}</span> — {t.description}</span><RiskBadge risk={t.risk} /></li>)}
        </ul>
      </Card>
      {x.report && (
        <Card title="Checks" actions={x.report.networkTested ? <Badge>with network</Badge> : <Badge>network off</Badge>}>
          {x.report.problems.length > 0 && <ul className="bullets small bad">{x.report.problems.map((pr, i) => <li key={i}>{pr}</li>)}</ul>}
          <ul className="list">
            {x.report.tests.map((t, i) => (
              <li key={i}><span className="small grow"><span className="mono">{t.tool}</span> — {t.detail}{t.log.length > 0 && <span className="meta"> · {t.log.join(" · ")}</span>}</span>
                <Badge tone={t.passed ? "good" : t.skipped ? "warn" : "bad"}>{t.passed ? "pass" : t.skipped ? "needs network" : "fail"}</Badge></li>
            ))}
          </ul>
          {x.report.tests.length === 0 && x.report.problems.length === 0 && <p className="small muted">It has no tests.</p>}
        </Card>
      )}
      {approvals.map((a) => <div key={a.id} className="reply-card"><ApprovalCard approval={a} /></div>)}
      <div className="row wrap">
        {(x.status === "draft" || x.status === "failed" || x.status === "ready") && <button className="btn btn-sm" onClick={() => check(false)}><FlaskConical size={14} /> Re-check</button>}
        {(x.status === "ready" || x.status === "failed") && hosts.length > 0 && x.report?.tests.some((t) => t.skipped) && (
          <button className="btn btn-sm" onClick={() => check(true)}><FlaskConical size={14} /> Run tests (reads from {hosts.join(", ")})</button>
        )}
        {x.status === "ready" && <button className="btn btn-primary btn-sm" onClick={install}><ShieldCheck size={14} /> Install…</button>}
        {x.status === "installed" && <button className="btn btn-sm" onClick={() => post(`/plugins/${id}/enabled`, { enabled: false }).then(() => p.reload()).catch(onError)}><Power size={14} /> Disable</button>}
        {x.status === "disabled" && <button className="btn btn-sm" onClick={() => post(`/plugins/${id}/enabled`, { enabled: true }).then(() => p.reload()).catch(onError)}><Power size={14} /> Enable</button>}
        <button className="btn btn-ghost btn-sm" onClick={() => setShowCode((s) => !s)}><Code2 size={14} /> {showCode ? "Hide code" : "Show code"}</button>
        <ConfirmButton className="btn btn-ghost btn-sm" prompt={`Remove ${x.manifest.name} and its storage?`} onConfirm={() => del(`/plugins/${id}`).then(onBack).catch(onError)}><Trash2 size={13} /> Remove</ConfirmButton>
      </div>
      {msg && <p className="small" dir="auto">{msg}</p>}
      {showCode && <Card title="main.js"><pre className="file-preview" dir="ltr">{x.code}</pre></Card>}
    </div>
  );
}
