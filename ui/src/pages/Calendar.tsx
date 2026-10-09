import { useState } from "react";
import { ChevronLeft, ChevronRight, MapPin, Plus, RefreshCw, Settings2, Sparkles, Trash2, Users } from "lucide-react";
import { del, get, post, type AgendaCalendar, type AgendaEvent, type MeetingPrep } from "../api";
import { navigate } from "../App";
import { useEvents } from "../events";
import { Badge, Card, ConfirmButton, Empty, ErrorNote, Field, PageHead, useLoad } from "../components/ui";
import { tr } from "../lib/i18n";

const DAY_MS = 86_400_000;
const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate());
const hm = (iso: string) => new Date(iso).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
const people = (e: AgendaEvent) => [...e.attendees, ...(e.organizer ? [e.organizer] : [])].map((a) => a.name || a.email || "").filter(Boolean);

/** A week of calendar, meeting prep from real data, and adding events. */
export function CalendarPage() {
  const [weekStart, setWeekStart] = useState(() => startOfDay(new Date()));
  const [openId, setOpenId] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const from = weekStart.toISOString();
  const to = new Date(weekStart.getTime() + 7 * DAY_MS).toISOString();
  const events = useLoad(() => get<AgendaEvent[]>(`/calendar/events?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`), [from]);
  const cals = useLoad(() => get<AgendaCalendar[]>("/calendar/calendars"));
  useEvents(["calendar."], () => { void events.reload(); void cals.reload(); });

  const days = Array.from({ length: 7 }, (_, i) => new Date(weekStart.getTime() + i * DAY_MS));
  const open = events.data?.find((e) => e.id === openId) ?? null;
  const sync = () => post("/calendar/sync", {}).then(() => events.reload()).catch(setError);

  return (
    <div className="stack">
      <PageHead
        title={tr("Calendar")}
        sub="Your week, and what you need to know before each meeting."
        actions={
          <>
            <button className="btn btn-primary btn-sm" onClick={() => setAdding((a) => !a)}><Plus size={14} /> Add event</button>
            {cals.data?.some((c) => c.kind === "ics") && <button className="btn btn-sm" onClick={sync}><RefreshCw size={14} /> Refresh</button>}
            <button className="btn btn-ghost btn-sm" onClick={() => navigate("settings", "accounts")}><Settings2 size={14} /> Calendars</button>
          </>
        }
      />
      <ErrorNote error={error ?? events.error} />
      {cals.data?.filter((c) => c.status === "error").map((c) => <div key={c.id} className="note warn small">{c.name}: {c.statusMessage}</div>)}
      {adding && <AddEvent onDone={() => { setAdding(false); void events.reload(); }} onError={setError} />}
      <div className="row between">
        <button className="btn btn-ghost btn-sm" aria-label="Previous week" onClick={() => setWeekStart(new Date(weekStart.getTime() - 7 * DAY_MS))}><ChevronLeft size={16} /></button>
        <strong className="small">{days[0].toLocaleDateString([], { day: "numeric", month: "short" })} – {days[6].toLocaleDateString([], { day: "numeric", month: "short", year: "numeric" })}</strong>
        <button className="btn btn-ghost btn-sm" aria-label="Next week" onClick={() => setWeekStart(new Date(weekStart.getTime() + 7 * DAY_MS))}><ChevronRight size={16} /></button>
      </div>
      {cals.data?.length === 0 && events.data?.length === 0 && (
        <Empty>No calendars yet. Add an event, say “schedule a meeting with Ahmed tomorrow at 3pm”, or subscribe to your Google/Outlook calendar in Settings → Accounts.</Empty>
      )}
      <div className="cal-grid">
        <div className="cal-days">
          {days.map((d) => {
            const list = (events.data ?? []).filter((e) => startOfDay(new Date(e.start)).getTime() === d.getTime() || (e.allDay && new Date(e.start) <= d && new Date(e.end) > d));
            const today = d.getTime() === startOfDay(new Date()).getTime();
            return (
              <Card key={d.toISOString()} className={`cal-day ${today ? "today" : ""}`} title={d.toLocaleDateString([], { weekday: "long", day: "numeric", month: "short" })}>
                {list.length === 0 ? <div className="small muted">—</div> : (
                  <ul className="list">
                    {list.map((e) => (
                      <li key={e.id}>
                        <button className={`file-row ${openId === e.id ? "on" : ""}`} onClick={() => setOpenId(e.id)}>
                          <span className="meta nowrap cal-time">{e.allDay ? "all day" : hm(e.start)}</span>
                          <span className="grow" style={{ minWidth: 0 }}>
                            <strong className="ellipsis" dir="auto">{e.title}</strong>
                            {(e.location || people(e).length > 0) && <span className="small muted ellipsis" dir="auto">{[e.location, people(e).slice(0, 3).join(", ")].filter(Boolean).join(" · ")}</span>}
                          </span>
                        </button>
                      </li>
                    ))}
                  </ul>
                )}
              </Card>
            );
          })}
        </div>
        {open && <EventDetail e={open} onClose={() => setOpenId(null)} onError={setError} />}
      </div>
    </div>
  );
}

function EventDetail({ e, onClose, onError }: { e: AgendaEvent; onClose: () => void; onError: (x: unknown) => void }) {
  const prep = useLoad(() => get<MeetingPrep>(`/calendar/events/${e.id}/prep`), [e.id]);
  return (
    <Card className="cal-detail" title={<span dir="auto">{e.title}</span>} actions={<button className="btn btn-ghost btn-sm" onClick={onClose}>Close</button>}>
      <dl className="kv small">
        <dt>When</dt><dd>{e.allDay ? new Date(e.start).toLocaleDateString() : `${new Date(e.start).toLocaleString([], { weekday: "short", day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" })} – ${hm(e.end)}`}</dd>
        {e.location && <><dt><MapPin size={11} /> Where</dt><dd dir="auto">{e.location}</dd></>}
        {people(e).length > 0 && <><dt><Users size={11} /> Who</dt><dd dir="auto">{people(e).join(", ")}</dd></>}
        <dt>From</dt><dd>{e.source === "ics" ? "Subscribed calendar (read-only)" : e.source === "jarvis" ? "Added by JARVIS" : "Added by you"}</dd>
      </dl>
      <div className="hud-label"><Sparkles size={12} /> Prepare</div>
      {prep.data ? <pre className="file-preview mail-body" dir="auto">{prep.data.text}</pre> : <ErrorNote error={prep.error} />}
      {e.description && <><div className="hud-label">Invite notes</div><p className="small muted" dir="auto">{e.description}</p></>}
      {e.source !== "ics" && (
        <div className="row"><ConfirmButton className="btn btn-ghost btn-sm" prompt={`Remove “${e.title}”?`} onConfirm={() => del(`/calendar/events/${e.id}`).then(onClose).catch(onError)}><Trash2 size={13} /> Remove</ConfirmButton></div>
      )}
    </Card>
  );
}

function AddEvent({ onDone, onError }: { onDone: () => void; onError: (e: unknown) => void }) {
  const now = new Date();
  now.setMinutes(0, 0, 0);
  now.setHours(now.getHours() + 1);
  const local = (d: Date) => new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
  const [f, setF] = useState({ title: "", start: local(now), minutes: 60, location: "", attendees: "" });
  const save = async () => {
    try {
      const start = new Date(f.start);
      await post("/calendar/events", { title: f.title, start: start.toISOString(), end: new Date(start.getTime() + f.minutes * 60000).toISOString(), location: f.location || null, attendees: f.attendees || null });
      onDone();
    } catch (e) { onError(e); }
  };
  return (
    <Card title="Add to JARVIS's calendar">
      <div className="form-grid">
        <Field label="Title"><input className="input" dir="auto" value={f.title} onChange={(e) => setF({ ...f, title: e.target.value })} placeholder="e.g. Call with Ahmed" /></Field>
        <Field label="Starts"><input className="input" type="datetime-local" value={f.start} onChange={(e) => setF({ ...f, start: e.target.value })} /></Field>
        <Field label="Length (minutes)"><input className="input" type="number" min={5} step={5} value={f.minutes} onChange={(e) => setF({ ...f, minutes: Number(e.target.value) })} /></Field>
        <Field label="Where"><input className="input" dir="auto" value={f.location} onChange={(e) => setF({ ...f, location: e.target.value })} /></Field>
        <Field label="With" hint="Names or emails, for your reference — nobody is invited."><input className="input" dir="auto" value={f.attendees} onChange={(e) => setF({ ...f, attendees: e.target.value })} /></Field>
      </div>
      <div className="row"><button className="btn btn-primary btn-sm" disabled={!f.title.trim()} onClick={save}>Add</button></div>
    </Card>
  );
}

export function CalendarSubscriptions() {
  const cals = useLoad(() => get<AgendaCalendar[]>("/calendar/calendars"));
  useEvents(["calendar."], () => void cals.reload());
  const [name, setName] = useState("");
  const [url, setUrl] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const add = async () => {
    setBusy(true);
    setError(null);
    try { await post("/calendar/calendars", { name, url }); setName(""); setUrl(""); await cals.reload(); } catch (e) { setError(e); } finally { setBusy(false); }
  };
  return (
    <Card title="Calendars">
      <p className="small muted">
        Subscribe with your calendar's private iCal address — Google Calendar: Settings → your calendar → “Secret address in iCal format”; Outlook: Settings → Calendar →
        Shared calendars → Publish → ICS link; iCloud: share as public calendar. JARVIS reads it (no changes to your calendar); the address is stored encrypted.
      </p>
      {cals.data?.length ? (
        <ul className="list">
          {cals.data.map((c) => (
            <li key={c.id}>
              <span className="grow" style={{ display: "flex", flexDirection: "column" }}>
                <span className="row" style={{ gap: 8 }}><strong>{c.name}</strong> <Badge tone={c.status === "ok" ? "good" : c.status === "error" ? "bad" : "neutral"}>{c.kind === "local" ? "this PC" : c.status}</Badge></span>
                <span className="small muted">{c.statusMessage ?? (c.lastSync ? `Refreshed ${new Date(c.lastSync).toLocaleString()}` : c.kind === "local" ? "Events you or JARVIS add" : "")}</span>
              </span>
              {c.kind !== "local" && <ConfirmButton className="btn btn-ghost btn-sm" prompt={`Remove ${c.name}? Its events disappear from JARVIS (your calendar isn't changed).`} onConfirm={async () => { await del(`/calendar/calendars/${c.id}`); void cals.reload(); }}><Trash2 size={13} /> Remove</ConfirmButton>}
            </li>
          ))}
        </ul>
      ) : null}
      <div className="form-grid">
        <Field label="Name"><input className="input" dir="auto" value={name} onChange={(e) => setName(e.target.value)} placeholder="Work" /></Field>
        <Field label="iCal address (https:// or webcal://)"><input className="input mono" dir="ltr" type="password" autoComplete="off" value={url} onChange={(e) => setUrl(e.target.value)} /></Field>
      </div>
      <div className="row"><button className="btn btn-primary btn-sm" disabled={busy || !url.trim()} onClick={add}>{busy ? "Reading the calendar…" : "Subscribe"}</button></div>
      <ErrorNote error={error} />
    </Card>
  );
}
