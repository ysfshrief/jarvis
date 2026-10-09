import { useState } from "react";
import { ArrowLeft, Ban, Check, CheckCircle2, Circle, CircleDot, Clock, Hourglass, Link2, Play, Plus, Repeat, ShieldCheck, SkipForward, Trash2, Undo2, XCircle } from "lucide-react";
import { del, get, post, put, type Workflow, type WorkflowEvent, type WorkflowStep, type WorkflowTemplate } from "../api";
import { useEvents } from "../events";
import { Badge, Card, ConfirmButton, Empty, ErrorNote, Field, formatTime, Meter, PageHead, timeAgo, useLoad } from "../components/ui";
import { tr, uiLocale } from "../lib/i18n";

const REPEATS: [string, string][] = [["", "Doesn't repeat"], ["daily", "Every day"], ["weekdays", "Every working day"], ["weekly", "Every week"], ["monthly", "Every month"]];

/** Long-running goals JARVIS tracks: steps, who you're waiting on, deadlines and history. */
export function WorkflowsPage() {
  const [openId, setOpenId] = useState<string | null>(() => location.hash.split("/")[2] ?? null);
  const [showClosed, setShowClosed] = useState(false);
  const list = useLoad(() => get<Workflow[]>(`/workflows?all=${showClosed}`), [showClosed]);
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<unknown>(null);
  useEvents(["workflows"], () => void list.reload());

  const open = (id: string | null) => { setOpenId(id); history.replaceState(null, "", id ? `#/workflows/${id}` : "#/workflows"); };

  if (openId) return <Detail id={openId} onBack={() => open(null)} />;

  return (
    <div className="stack">
      <PageHead
        title={tr("Workflows")}
        sub="Deals, projects and follow-ups JARVIS keeps track of — say “track the CityCrep deal” or start one here."
        actions={<button className="btn btn-primary btn-sm" onClick={() => setCreating((c) => !c)}><Plus size={14} /> {tr("New workflow")}</button>}
      />
      <ErrorNote error={error ?? list.error} />
      {creating && <Create onDone={(id) => { setCreating(false); open(id); }} onError={setError} />}
      <label className="row small"><input type="checkbox" checked={showClosed} onChange={(e) => setShowClosed(e.target.checked)} /> {tr("Show completed and cancelled")}</label>
      {list.data?.length === 0 && !creating && <Empty>Nothing tracked yet. Try “track the CityCrep deal” or “تابع صفقة سيتي كريب”.</Empty>}
      <div className="grid-3">
        {list.data?.map((w) => <WorkflowCard key={w.id} w={w} onOpen={() => open(w.id)} />)}
      </div>
    </div>
  );
}

function statusTone(s: string): "good" | "warn" | "bad" | "info" | "neutral" {
  return s === "completed" ? "good" : s === "waiting" ? "info" : s === "blocked" ? "bad" : s === "cancelled" ? "neutral" : "warn";
}

function WorkflowCard({ w, onOpen }: { w: Workflow; onOpen: () => void }) {
  const waiting = w.steps.filter((s) => s.ready && s.status === "waiting");
  const overdue = w.steps.filter((s) => !["done", "skipped"].includes(s.status) && s.dueAt && new Date(s.dueAt) < new Date()).length;
  return (
    <Card title={<span dir="auto">{w.title}</span>} actions={<Badge tone={statusTone(w.status)}>{tr(w.status)}</Badge>} className="clickable-card">
      <button className="link" style={{ all: "unset", cursor: "pointer", display: "flex", flexDirection: "column", gap: 8 }} onClick={onOpen} aria-label={tr("Open {name}", { name: w.title })}>
        <div className="row between small">
          <span className="muted">{tr("{done}/{total} steps", { done: w.doneCount, total: w.steps.length })}</span>
          {w.entityName && <span className="row small"><Link2 size={12} /> {w.entityName}</span>}
        </div>
        <Meter value={w.progress * 100} />
        {waiting.length > 0 ? (
          <div className="small"><Hourglass size={12} /> {tr("Waiting on {who}", { who: waiting.map((s) => s.waitingFor ?? s.title).join(", ") })}</div>
        ) : w.next ? (
          <div className="small"><CircleDot size={12} /> {tr("Next:")} <span dir="auto">{w.next.title}</span></div>
        ) : null}
        <div className="row small wrap">
          {w.dueAt && <span className="meta" dir="ltr">{tr("due {time}", { time: formatTime(w.dueAt) })}</span>}
          {overdue > 0 && <Badge tone="bad">{tr("{n} overdue", { n: overdue })}</Badge>}
          {w.recurrence && <Badge tone="info"><Repeat size={10} /> {tr(w.recurrence)}</Badge>}
          <span className="meta">{tr("updated {ago}", { ago: timeAgo(w.updatedAt) })}</span>
        </div>
      </button>
    </Card>
  );
}

function Create({ onDone, onError }: { onDone: (id: string) => void; onError: (e: unknown) => void }) {
  const templates = useLoad(() => get<WorkflowTemplate[]>("/workflows/templates"));
  const [title, setTitle] = useState("");
  const [template, setTemplate] = useState("deal");
  const [about, setAbout] = useState("");
  const [due, setDue] = useState("");
  const [repeat, setRepeat] = useState("");
  const [steps, setSteps] = useState("");
  const tpl = templates.data?.find((t) => t.id === template);
  const submit = async () => {
    try {
      const custom = template === "custom" ? steps.split("\n").map((s) => s.trim()).filter(Boolean).map((t) => ({ title: t })) : undefined;
      const wf = await post<Workflow>("/workflows", { title, template, about: about || null, due: due ? new Date(due).toISOString() : null, repeat: repeat || null, steps: custom });
      onDone(wf.id);
    } catch (e) { onError(e); }
  };
  return (
    <Card title="Start tracking">
      <div className="form-grid">
        <Field label="Name"><input className="input" dir="auto" placeholder={tr("e.g. CityCrep deal")} value={title} onChange={(e) => setTitle(e.target.value)} /></Field>
        <Field label="Template" hint={tpl?.description}>
          <select className="input" value={template} onChange={(e) => setTemplate(e.target.value)}>
            {templates.data?.map((t) => <option key={t.id} value={t.id}>{tr(t.name)}</option>)}
          </select>
        </Field>
        <Field label="About (client, project, person)"><input className="input" dir="auto" placeholder={tr("e.g. CityCrep")} value={about} onChange={(e) => setAbout(e.target.value)} /></Field>
        <Field label="Deadline" hint="Step deadlines are spread up to it."><input className="input" type="datetime-local" value={due} onChange={(e) => setDue(e.target.value)} /></Field>
        <Field label="Repeat"><select className="input" value={repeat} onChange={(e) => setRepeat(e.target.value)}>{REPEATS.map(([v, l]) => <option key={v} value={v}>{tr(l)}</option>)}</select></Field>
      </div>
      {template === "custom" ? (
        <Field label="Steps (one per line, in order)"><textarea className="input" dir="auto" rows={5} value={steps} onChange={(e) => setSteps(e.target.value)} /></Field>
      ) : tpl && (
        <ol className="steps small muted">{tpl.steps.map((s) => <li key={s}>{s}</li>)}</ol>
      )}
      <div className="row"><button className="btn btn-primary" disabled={!title.trim() || (template === "custom" && !steps.trim())} onClick={submit}><Play size={14} /> {tr("Start")}</button></div>
    </Card>
  );
}

function Detail({ id, onBack }: { id: string; onBack: () => void }) {
  const data = useLoad(() => get<{ workflow: Workflow; history: WorkflowEvent[] }>(`/workflows/${id}`), [id]);
  const [error, setError] = useState<unknown>(null);
  const [note, setNote] = useState("");
  const [newStep, setNewStep] = useState("");
  useEvents(["workflows"], () => void data.reload());
  if (!data.data) return <div className="stack"><button className="btn btn-ghost btn-sm" onClick={onBack}><ArrowLeft size={14} /> {tr("All workflows")}</button><ErrorNote error={data.error} /></div>;
  const { workflow: w, history } = data.data;
  const act = (p: Promise<unknown>) => p.catch(setError);

  return (
    <div className="stack">
      <button className="btn btn-ghost btn-sm" style={{ alignSelf: "flex-start" }} onClick={onBack}><ArrowLeft size={14} /> {tr("All workflows")}</button>
      <PageHead
        title={w.title}
        sub={<span>{w.goal}{w.entityName && <> · <Link2 size={12} /> {w.entityName}</>}{w.dueAt && <> · {tr("due {time}", { time: formatTime(w.dueAt) })}</>}</span>}
        actions={
          <>
            <Badge tone={statusTone(w.status)}>{tr(w.status)}</Badge>
            {w.isOpen && <ConfirmButton className="btn btn-ghost btn-sm" prompt={tr("Stop tracking {name}?", { name: w.title })} onConfirm={() => act(post(`/workflows/${id}/cancel`))}><Ban size={13} /> {tr("Stop tracking")}</ConfirmButton>}
            <ConfirmButton className="btn btn-danger btn-sm" prompt={tr("Delete {name} and its history?", { name: w.title })} onConfirm={() => del(`/workflows/${id}`).then(onBack).catch(setError)}><Trash2 size={13} /></ConfirmButton>
          </>
        }
      />
      <ErrorNote error={error} />
      <div className="row"><div className="grow"><Meter value={w.progress * 100} /></div><span className="meta nowrap">{w.doneCount}/{w.steps.length}</span></div>

      <div className="grid-2" style={{ alignItems: "start" }}>
        <Card title="Steps">
          <ol className="wf-steps">
            {w.steps.map((s, i) => <StepRow key={s.id} s={s} index={i} wf={w} onError={setError} />)}
          </ol>
          {w.isOpen && (
            <form className="row" onSubmit={(e) => { e.preventDefault(); act(post(`/workflows/${id}/steps`, { title: newStep }).then(() => setNewStep(""))); }}>
              <input className="input grow" dir="auto" placeholder={tr("Add a step…")} value={newStep} onChange={(e) => setNewStep(e.target.value)} />
              <button className="btn btn-sm" disabled={!newStep.trim()}><Plus size={13} /> {tr("Add")}</button>
            </form>
          )}
        </Card>
        <Card title="History">
          <form className="row" onSubmit={(e) => { e.preventDefault(); act(post(`/workflows/${id}/notes`, { text: note }).then(() => setNote(""))); }}>
            <input className="input grow" dir="auto" placeholder={tr("Add a note (e.g. “Ahmed asked for a 10% discount”)")} value={note} onChange={(e) => setNote(e.target.value)} />
            <button className="btn btn-sm" disabled={!note.trim()}>{tr("Note")}</button>
          </form>
          <ul className="list">
            {history.map((h) => (
              <li key={h.id} style={{ alignItems: "flex-start" }}>
                <span className="small grow" dir="auto">{h.text}</span>
                <span className="meta nowrap" title={new Date(h.timestamp).toLocaleString(uiLocale())}>{timeAgo(h.timestamp)}</span>
              </li>
            ))}
          </ul>
        </Card>
      </div>
    </div>
  );
}

function StepRow({ s, index, wf, onError }: { s: WorkflowStep; index: number; wf: Workflow; onError: (e: unknown) => void }) {
  const [waiting, setWaiting] = useState<{ who: string; days: number } | null>(null);
  const [msg, setMsg] = useState<string | null>(null);
  const update = (body: object) => put(`/workflows/${wf.id}/steps/${s.id}`, body).catch(onError);
  const closed = s.status === "done" || s.status === "skipped";
  const blockedBy = s.dependsOn.map((d) => wf.steps.find((x) => x.id === d)).filter((x) => x && !["done", "skipped"].includes(x.status));
  return (
    <li className={`wf-step wf-${s.status} ${s.ready ? "wf-ready" : ""}`}>
      <StepIcon s={s} />
      <div className="grow stack-sm">
        <div className={closed ? "done" : ""} dir="auto"><span className="meta">{index + 1}.</span> {s.title}</div>
        <div className="row small wrap">
          {s.requiresApproval && !closed && <Badge tone="warn"><ShieldCheck size={10} /> {tr("needs your OK")}</Badge>}
          {s.status === "waiting" && <Badge tone="info"><Hourglass size={10} /> {tr("waiting for {who}", { who: s.waitingFor ?? tr("a reply") })}</Badge>}
          {s.followUpAt && !closed && <span className="meta">{tr("follow up {time}", { time: formatTime(s.followUpAt) })}</span>}
          {s.dueAt && !closed && <span className={`meta ${new Date(s.dueAt) < new Date() ? "accent" : ""}`} dir="ltr">{tr("due {time}", { time: formatTime(s.dueAt) })}</span>}
          {!s.ready && !closed && blockedBy.length > 0 && <span className="meta">{tr("after “{step}”", { step: blockedBy[0]!.title })}</span>}
          {s.completedAt && <span className="meta">{tr("done {ago}", { ago: timeAgo(s.completedAt) })}</span>}
          {s.action && <Badge tone="accent">{tr("runs {tool}", { tool: s.action.tool })}</Badge>}
        </div>
        {msg && <div className="small muted">{msg}</div>}
        {waiting && (
          <form className="row wrap" onSubmit={(e) => { e.preventDefault(); void update({ status: "waiting", waitingFor: waiting.who || null, followUpDays: waiting.days }); setWaiting(null); }}>
            <input className="input input-sm grow" dir="auto" placeholder={tr("Waiting for whom?")} value={waiting.who} onChange={(e) => setWaiting({ ...waiting, who: e.target.value })} />
            <span className="small muted">{tr("follow up in")}</span>
            <input className="input input-sm" type="number" min={1} max={60} style={{ width: 64 }} value={waiting.days} onChange={(e) => setWaiting({ ...waiting, days: Number(e.target.value) })} />
            <span className="small muted">{tr("days")}</span>
            <button className="btn btn-sm">{tr("Set")}</button>
            <button type="button" className="btn btn-ghost btn-sm" onClick={() => setWaiting(null)}>{tr("Cancel")}</button>
          </form>
        )}
      </div>
      {wf.isOpen && (
        <div className="row">
          {!closed && s.action && s.ready && (
            <button className="btn btn-sm" title={tr("Run this step's action (asks first if it's a business action)")} onClick={async () => { const r = await post<{ ok: boolean; message: string }>(`/workflows/${wf.id}/steps/${s.id}/run`).catch(onError); if (r) setMsg(r.message); }}><Play size={13} /> {tr("Run")}</button>
          )}
          {!closed && <button className="btn btn-sm" title={tr("Done")} onClick={() => update({ status: "done" })}><Check size={13} /></button>}
          {!closed && s.status !== "waiting" && <button className="btn btn-ghost btn-icon" title={tr("Waiting on someone")} onClick={() => setWaiting({ who: s.waitingFor ?? "", days: 3 })}><Hourglass size={14} /></button>}
          {!closed && <button className="btn btn-ghost btn-icon" title={tr("Skip")} onClick={() => update({ status: "skipped" })}><SkipForward size={14} /></button>}
          {closed && <button className="btn btn-ghost btn-icon" title={tr("Reopen")} onClick={() => update({ status: "pending", clearFollowUp: true })}><Undo2 size={14} /></button>}
        </div>
      )}
    </li>
  );
}

function StepIcon({ s }: { s: WorkflowStep }) {
  const size = 16;
  switch (s.status) {
    case "done": return <CheckCircle2 size={size} color="var(--good)" />;
    case "skipped": return <SkipForward size={size} className="dim" />;
    case "waiting": return <Clock size={size} color="var(--info)" />;
    case "blocked": return <XCircle size={size} color="var(--bad)" />;
    case "in_progress": return <CircleDot size={size} color="var(--accent)" />;
    default: return s.ready ? <CircleDot size={size} color="var(--accent)" /> : <Circle size={size} className="dim" />;
  }
}
