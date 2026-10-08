import { useState } from "react";
import { Bell, Plus, Trash2, X } from "lucide-react";
import { del, get, post, put, type Reminder, type TaskItem } from "../api";
import { useEvents } from "../events";
import { Badge, Card, Empty, ErrorNote, formatTime, timeAgo, useLoad } from "../components/ui";

const STATES = ["pending", "in_progress", "waiting", "blocked", "completed", "cancelled"];
const PRIORITIES = ["low", "normal", "high", "urgent"];

export function Tasks() {
  const [showClosed, setShowClosed] = useState(false);
  const tasks = useLoad(() => get<TaskItem[]>(`/tasks?all=${showClosed}`), [showClosed]);
  const reminders = useLoad(() => get<Reminder[]>("/reminders"));
  const [title, setTitle] = useState("");
  const [priority, setPriority] = useState("normal");
  const [due, setDue] = useState("");
  const [error, setError] = useState<unknown>(null);
  useEvents(["tasks"], () => void tasks.reload());
  useEvents(["reminders"], () => void reminders.reload());

  const add = async () => {
    if (!title.trim()) return;
    try {
      await post("/tasks", { title, priority, dueAt: due ? new Date(due).toISOString() : null });
      setTitle("");
      setDue("");
      setError(null);
    } catch (e) {
      setError(e);
    }
  };

  const update = (t: TaskItem, patch: Partial<TaskItem>) => put(`/tasks/${t.id}`, patch).catch(setError);

  const grouped = STATES.map((s) => ({ state: s, items: (tasks.data ?? []).filter((t) => t.state === s) })).filter((g) => g.items.length > 0);

  return (
    <div className="stack">
      <h2>Tasks</h2>
      <Card>
        <form className="row wrap" onSubmit={(e) => { e.preventDefault(); void add(); }}>
          <input className="input grow" dir="auto" placeholder="New task…" value={title} onChange={(e) => setTitle(e.target.value)} />
          <select className="input" value={priority} onChange={(e) => setPriority(e.target.value)} aria-label="Priority">
            {PRIORITIES.map((p) => <option key={p}>{p}</option>)}
          </select>
          <input className="input" type="datetime-local" value={due} onChange={(e) => setDue(e.target.value)} aria-label="Due" />
          <button className="btn btn-primary" type="submit" disabled={!title.trim()}><Plus size={16} /> Add</button>
        </form>
        <ErrorNote error={error ?? tasks.error} />
      </Card>

      <div className="row">
        <label className="row small"><input type="checkbox" checked={showClosed} onChange={(e) => setShowClosed(e.target.checked)} /> Show completed and cancelled</label>
      </div>

      {grouped.length === 0 && <Empty>No tasks. Add one above, or say “add task …” / “ضيف مهمة …”.</Empty>}
      {grouped.map((g) => (
        <Card key={g.state} title={`${g.state.replace("_", " ")} (${g.items.length})`}>
          <ul className="list list-rows">
            {g.items.map((t) => (
              <li key={t.id}>
                <input type="checkbox" aria-label="Done" checked={t.state === "completed"} onChange={(e) => update(t, { state: e.target.checked ? "completed" : "pending" })} />
                <div className="grow">
                  <div dir="auto" className={t.state === "completed" ? "done" : ""}>{t.title}</div>
                  <div className="muted small">
                    {t.dueAt ? `Due ${formatTime(t.dueAt)} · ` : ""}created {timeAgo(t.createdAt)}
                    {t.project ? ` · ${t.project}` : ""}
                  </div>
                </div>
                <Badge tone={t.priority === "urgent" ? "bad" : t.priority === "high" ? "warn" : "neutral"}>{t.priority}</Badge>
                <select className="input input-sm" value={t.state} onChange={(e) => update(t, { state: e.target.value })} aria-label="State">
                  {STATES.map((s) => <option key={s} value={s}>{s.replace("_", " ")}</option>)}
                </select>
                <button className="btn btn-ghost" onClick={() => del(`/tasks/${t.id}`)} aria-label="Delete task"><Trash2 size={16} /></button>
              </li>
            ))}
          </ul>
        </Card>
      ))}

      <Reminders reminders={reminders.data ?? []} />
    </div>
  );
}

function Reminders({ reminders }: { reminders: Reminder[] }) {
  const [text, setText] = useState("");
  const [minutes, setMinutes] = useState(30);
  const add = async () => {
    if (!text.trim()) return;
    await post("/reminders", { text, inMinutes: minutes });
    setText("");
  };
  return (
    <Card title={<span className="row"><Bell size={16} /> Reminders</span>}>
      <form className="row wrap" onSubmit={(e) => { e.preventDefault(); void add(); }}>
        <input className="input grow" dir="auto" placeholder="Remind me to…" value={text} onChange={(e) => setText(e.target.value)} />
        <span className="muted">in</span>
        <input className="input input-sm" type="number" min={1} value={minutes} onChange={(e) => setMinutes(Number(e.target.value))} aria-label="Minutes" />
        <span className="muted">min</span>
        <button className="btn" type="submit" disabled={!text.trim()}><Plus size={16} /> Remind me</button>
      </form>
      {reminders.length === 0 ? (
        <Empty>No upcoming reminders.</Empty>
      ) : (
        <ul className="list list-rows">
          {reminders.map((r) => (
            <li key={r.id}>
              <div className="grow" dir="auto">{r.text}</div>
              <span className="muted small nowrap" dir="ltr">{formatTime(r.dueAt)} ({timeAgo(r.dueAt)})</span>
              <button className="btn btn-ghost" onClick={() => del(`/reminders/${r.id}`)} aria-label="Cancel reminder"><X size={16} /></button>
            </li>
          ))}
        </ul>
      )}
    </Card>
  );
}
