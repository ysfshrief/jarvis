import { useEffect, useState } from "react";
import { Check, Download, KeyRound, RefreshCw, Save, Trash2 } from "lucide-react";
import { del, get, post, put, type ProviderStatus, type Settings } from "../api";
import { useStatus } from "../App";
import { useEvents } from "../events";
import { Badge, Card, ConfirmButton, ErrorNote, Field, Toggle, useLoad } from "../components/ui";

const TABS = ["General", "AI", "Voice", "Permissions", "Memory", "Notifications", "Security"] as const;
type Tab = (typeof TABS)[number];

export function SettingsPage() {
  const [tab, setTab] = useState<Tab>("General");
  const loaded = useLoad(() => get<Settings>("/settings"));
  const [draft, setDraft] = useState<Settings | null>(null);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const { refresh } = useStatus();

  useEffect(() => {
    if (loaded.data) setDraft(structuredClone(loaded.data));
  }, [loaded.data]);

  if (!draft) return <ErrorNote error={loaded.error} />;
  const dirty = JSON.stringify(draft) !== JSON.stringify(loaded.data);

  const set = (fn: (s: Settings) => void) => {
    setDraft((cur) => {
      const next = structuredClone(cur!);
      fn(next);
      return next;
    });
    setSaved(false);
  };

  const save = async () => {
    try {
      const result = await put<Settings>("/settings", draft);
      loaded.setData(result);
      setDraft(structuredClone(result));
      setSaved(true);
      setError(null);
      await refresh();
    } catch (e) {
      setError(e);
    }
  };

  return (
    <div className="stack">
      <div className="row between">
        <h2>Settings</h2>
        <div className="row">
          {saved && !dirty && <span className="muted row"><Check size={14} /> Saved</span>}
          <button className="btn btn-primary" disabled={!dirty} onClick={save}><Save size={16} /> Save changes</button>
        </div>
      </div>
      <ErrorNote error={error} />
      <div className="tabs" role="tablist">
        {TABS.map((t) => (
          <button key={t} role="tab" aria-selected={tab === t} className={`tab ${tab === t ? "active" : ""}`} onClick={() => setTab(t)}>{t}</button>
        ))}
      </div>
      {tab === "General" && <General s={draft} set={set} />}
      {tab === "AI" && <Ai s={draft} set={set} />}
      {tab === "Voice" && <Voice s={draft} set={set} />}
      {tab === "Permissions" && <Permissions s={draft} set={set} />}
      {tab === "Memory" && <MemorySettings s={draft} set={set} />}
      {tab === "Notifications" && <Notifications s={draft} set={set} />}
      {tab === "Security" && <Security />}
    </div>
  );
}

type P = { s: Settings; set: (fn: (s: Settings) => void) => void };

function General({ s, set }: P) {
  return (
    <Card>
      <div className="form-grid">
        <Field label="Your name" hint="Optional. Used in greetings.">
          <input className="input" dir="auto" value={s.general.userName} onChange={(e) => set((x) => { x.general.userName = e.target.value; })} />
        </Field>
        <Field label="How JARVIS addresses you (English)" hint="e.g. Sir, Boss, your name — or leave empty.">
          <input className="input" value={s.general.honorific} onChange={(e) => set((x) => { x.general.honorific = e.target.value; })} />
        </Field>
        <Field label="How JARVIS addresses you (Arabic)" hint="مثلاً: يا فندم، يا باشا، يا هندسة">
          <input className="input" dir="rtl" value={s.general.honorificAr} onChange={(e) => set((x) => { x.general.honorificAr = e.target.value; })} />
        </Field>
        <Field label="Reply language" hint="Auto replies in the language you use.">
          <select className="input" value={s.general.language} onChange={(e) => set((x) => { x.general.language = e.target.value as Settings["general"]["language"]; })}>
            <option value="auto">Automatic (match me)</option>
            <option value="en">Always English</option>
            <option value="ar">Always Egyptian Arabic</option>
          </select>
        </Field>
        <Field label="Conversation memory window (minutes)" hint="After this much silence, a new conversation context starts.">
          <input className="input" type="number" min={1} value={s.general.conversationTimeoutMinutes} onChange={(e) => set((x) => { x.general.conversationTimeoutMinutes = Number(e.target.value); })} />
        </Field>
      </div>
      <Toggle label="Start JARVIS when I sign in to Windows" checked={s.general.startWithWindows} onChange={(v) => set((x) => { x.general.startWithWindows = v; })} />
      <Toggle label="Show the desktop interface (orb and tray) when JARVIS starts" checked={s.general.launchDesktopOnStart} onChange={(v) => set((x) => { x.general.launchDesktopOnStart = v; })} />
      <Toggle label="Show the floating orb" checked={s.general.showOrb} onChange={(v) => set((x) => { x.general.showOrb = v; })} />
    </Card>
  );
}

function Ai({ s, set }: P) {
  const secrets = useLoad(() => get<{ names: string[]; protection: string }>("/secrets"));
  const [statuses, setStatuses] = useState<Record<string, ProviderStatus>>({});
  const [keys, setKeys] = useState<Record<string, string>>({});
  const check = async (id: string) => {
    const st = await post<ProviderStatus>(`/ai/providers/${id}/check`);
    setStatuses((c) => ({ ...c, [id]: st }));
  };
  useEffect(() => {
    s.ai.providers.filter((p) => p.enabled).forEach((p) => void check(p.id).catch(() => {}));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  const allModels = (id: string) => statuses[id]?.models ?? [];

  return (
    <div className="stack">
      <Card title="Privacy">
        <Toggle
          label="Allow cloud AI"
          hint="Off = nothing you say leaves this computer. On = providers marked 'cloud' may be used when online, with your own API key."
          checked={s.ai.allowCloud}
          onChange={(v) => set((x) => { x.ai.allowCloud = v; })}
        />
      </Card>
      <Card title="Providers">
        <p className="muted small">
          Free and local: install <strong>Ollama</strong> (ollama.com) and run <code>ollama pull qwen2.5:7b</code>, or use LM Studio's local server. Any OpenAI-compatible server works.
        </p>
        {s.ai.providers.map((p, i) => {
          const st = statuses[p.id];
          return (
            <div key={p.id} className="provider">
              <div className="row between wrap">
                <div className="row">
                  <strong>{p.name}</strong>
                  <Badge tone={p.isLocal ? "good" : "warn"}>{p.isLocal ? "local" : "cloud"}</Badge>
                  {st && <Badge tone={st.available ? "good" : "bad"}>{st.available ? `${st.models.length} models` : "unavailable"}</Badge>}
                </div>
                <div className="row">
                  <button className="btn btn-ghost" onClick={() => check(p.id)} disabled={!p.enabled} title="Test connection"><RefreshCw size={14} /> Test</button>
                  <Toggle label="Enabled" checked={p.enabled} onChange={(v) => set((x) => { x.ai.providers[i].enabled = v; })} />
                </div>
              </div>
              {st && !st.available && <div className="muted small">{st.message}</div>}
              {p.kind !== "anthropic" && (
                <Field label="Server URL">
                  <input className="input mono" value={p.baseUrl} onChange={(e) => set((x) => { x.ai.providers[i].baseUrl = e.target.value; })} />
                </Field>
              )}
              {p.apiKeySecret && (
                <div className="row wrap">
                  <KeyRound size={16} />
                  {secrets.data?.names.includes(p.apiKeySecret) ? (
                    <>
                      <Badge tone="good">API key stored ({secrets.data.protection})</Badge>
                      <button className="btn btn-ghost" onClick={async () => { await del(`/secrets/${p.apiKeySecret}`); void secrets.reload(); }}><Trash2 size={14} /> Remove</button>
                    </>
                  ) : (
                    <>
                      <input className="input grow mono" type="password" placeholder="Paste API key (stored encrypted, never shown again)" value={keys[p.id] ?? ""} onChange={(e) => setKeys((k) => ({ ...k, [p.id]: e.target.value }))} />
                      <button className="btn" disabled={!keys[p.id]} onClick={async () => { await put(`/secrets/${p.apiKeySecret}`, { value: keys[p.id] }); setKeys((k) => ({ ...k, [p.id]: "" })); void secrets.reload(); }}>Save key</button>
                    </>
                  )}
                </div>
              )}
            </div>
          );
        })}
      </Card>
      <Card title="Which model does what">
        <p className="muted small">
          Direct commands never use AI. Other requests are classified (general / reasoning / coding) and go to the first available choice below, falling back to “general”. Empty model = the provider's first model.
        </p>
        {Object.entries(s.ai.roles).map(([role, bindings]) => (
          <div key={role} className="role">
            <div className="role-name">{role}</div>
            <div className="stack-sm grow">
              {bindings.map((b, j) => (
                <div key={j} className="row wrap">
                  <span className="muted small">{j + 1}.</span>
                  <select className="input" value={b.provider} onChange={(e) => set((x) => { x.ai.roles[role][j].provider = e.target.value; })}>
                    {s.ai.providers.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
                  </select>
                  <input className="input mono grow" list={`models-${role}-${j}`} placeholder="(first available)" value={b.model} onChange={(e) => set((x) => { x.ai.roles[role][j].model = e.target.value; })} />
                  <datalist id={`models-${role}-${j}`}>{allModels(b.provider).map((m) => <option key={m} value={m} />)}</datalist>
                  <button className="btn btn-ghost" onClick={() => set((x) => { x.ai.roles[role].splice(j, 1); })} aria-label="Remove"><Trash2 size={14} /></button>
                </div>
              ))}
              <button className="btn btn-ghost small" onClick={() => set((x) => { x.ai.roles[role].push({ provider: s.ai.providers[0]?.id ?? "", model: "" }); })}>+ Add fallback</button>
            </div>
          </div>
        ))}
        <div className="form-grid">
          <Field label="Max tool steps per request" hint="JARVIS stops and checks in after this many actions.">
            <input className="input" type="number" min={1} max={30} value={s.ai.maxAgentSteps} onChange={(e) => set((x) => { x.ai.maxAgentSteps = Number(e.target.value); })} />
          </Field>
          <Field label="Model timeout (seconds)">
            <input className="input" type="number" min={10} value={s.ai.requestTimeoutSeconds} onChange={(e) => set((x) => { x.ai.requestTimeoutSeconds = Number(e.target.value); })} />
          </Field>
        </div>
      </Card>
    </div>
  );
}

interface SpeechModel { name: string; approxBytes: number; description: string; installed: boolean }

function Voice({ s, set }: P) {
  const models = useLoad(() => get<{ downloading: boolean; models: SpeechModel[] }>("/voice/models"));
  const voices = useLoad(() => get<{ engine: string; available: boolean; voices: { id: string; name: string; language: string }[] }>("/voice/voices"));
  const [progress, setProgress] = useState<Record<string, { bytes: number; total?: number; error?: string }>>({});
  useEvents(["voice.model"], (e) => {
    const d = e.data;
    setProgress((p) => ({ ...p, [d.model]: { bytes: d.bytes ?? 0, total: d.total, error: d.error } }));
    if (d.done) void models.reload();
  });
  const enVoices = voices.data?.voices.filter((v) => v.language.startsWith("en")) ?? [];
  const arVoices = voices.data?.voices.filter((v) => v.language.startsWith("ar")) ?? [];

  return (
    <div className="stack">
      <Card title="Speech recognition (local Whisper)">
        <p className="muted small">Runs entirely on this computer. Models download once from the whisper.cpp project. For Egyptian Arabic, “small” is noticeably better than “base”.</p>
        <ul className="list list-rows">
          {models.data?.models.map((m) => {
            const pr = progress[m.name];
            return (
              <li key={m.name}>
                <input type="radio" name="stt" aria-label={`Use ${m.name}`} checked={s.voice.sttModel === m.name} disabled={!m.installed} onChange={() => set((x) => { x.voice.sttModel = m.name; })} />
                <div className="grow">
                  <strong>{m.name}</strong> <span className="muted small">~{Math.round(m.approxBytes / 1e6)} MB</span>
                  <div className="muted small">{m.description}</div>
                  {pr && !m.installed && (pr.error ? <div className="error-note">{pr.error}</div> : <progress max={pr.total ?? m.approxBytes} value={pr.bytes} />)}
                </div>
                {m.installed ? <Badge tone="good">installed</Badge> : (
                  <button className="btn" disabled={models.data?.downloading} onClick={() => post(`/voice/models/${m.name}/download`).then(() => models.reload())}><Download size={14} /> Download</button>
                )}
              </li>
            );
          })}
        </ul>
      </Card>
      <Card title="Wake word and conversation">
        <Toggle label="Listen for “Jarvis” (wake word)" hint="Keeps the microphone open while JARVIS runs. The orb shows when it's listening. Off by default." checked={s.voice.wakeWordEnabled} onChange={(v) => set((x) => { x.voice.wakeWordEnabled = v; })} />
        <div className="form-grid">
          <Field label="Wake words" hint="Comma separated. Arabic spellings are recognised too.">
            <input className="input" dir="auto" value={s.voice.wakeWords.join(", ")} onChange={(e) => set((x) => { x.voice.wakeWords = e.target.value.split(",").map((w) => w.trim()).filter(Boolean); })} />
          </Field>
          <Field label="Follow-up window (seconds)" hint="After answering, keep listening this long without needing the wake word.">
            <input className="input" type="number" min={0} max={60} value={s.voice.followUpSeconds} onChange={(e) => set((x) => { x.voice.followUpSeconds = Number(e.target.value); })} />
          </Field>
          <Field label="Speech sensitivity" hint="Higher = needs louder speech (fewer false triggers).">
            <input className="input" type="number" step={0.5} min={1.5} max={20} value={s.voice.vadSensitivity} onChange={(e) => set((x) => { x.voice.vadSensitivity = Number(e.target.value); })} />
          </Field>
          <Field label="Max command length (seconds)">
            <input className="input" type="number" min={3} max={60} value={s.voice.maxUtteranceSeconds} onChange={(e) => set((x) => { x.voice.maxUtteranceSeconds = Number(e.target.value); })} />
          </Field>
        </div>
      </Card>
      <Card title="Spoken replies">
        <Toggle label="Speak replies" checked={s.voice.ttsEnabled} onChange={(v) => set((x) => { x.voice.ttsEnabled = v; })} />
        <Toggle label="Only speak when I used my voice" checked={s.voice.speakOnlyForVoiceInput} onChange={(v) => set((x) => { x.voice.speakOnlyForVoiceInput = v; })} />
        {!voices.data?.available && <p className="muted small">No speech voices available on this system.</p>}
        <div className="form-grid">
          <Field label="English voice">
            <select className="input" value={s.voice.voiceEn} onChange={(e) => set((x) => { x.voice.voiceEn = e.target.value; })}>
              <option value="">Automatic</option>
              {enVoices.map((v) => <option key={v.id} value={v.id}>{v.name} ({v.language})</option>)}
            </select>
          </Field>
          <Field label="Arabic voice" hint={arVoices.length ? undefined : "No Arabic voice installed. Windows Settings → Time & language → Speech → Add voices (Arabic – Egypt)."}>
            <select className="input" value={s.voice.voiceAr} onChange={(e) => set((x) => { x.voice.voiceAr = e.target.value; })}>
              <option value="">Automatic</option>
              {arVoices.map((v) => <option key={v.id} value={v.id}>{v.name} ({v.language})</option>)}
            </select>
          </Field>
          <Field label="Speaking rate">
            <input className="input" type="number" step={0.1} min={0.5} max={2} value={s.voice.rate} onChange={(e) => set((x) => { x.voice.rate = Number(e.target.value); })} />
          </Field>
        </div>
        <button className="btn" onClick={() => post("/voice/speak", { text: "Good evening, Sir. All systems are operational." })}>Test English voice</button>{" "}
        <button className="btn" onClick={() => post("/voice/speak", { text: "مساء الخير يا فندم، كل حاجة شغالة تمام.", lang: "ar" })}>جرب الصوت العربي</button>
      </Card>
    </div>
  );
}

function Permissions({ s, set }: P) {
  return (
    <div className="stack">
      <Card title="Approvals">
        <Toggle
          label="Run sensitive actions without asking"
          hint="Sensitive = changing files, running programs, typing into apps. Critical actions (deleting, power, destructive commands, sending) always ask."
          checked={s.permissions.autoApproveSensitive}
          onChange={(v) => set((x) => { x.permissions.autoApproveSensitive = v; })}
        />
        <Field label="Approval timeout (seconds)" hint="If you don't answer, the action is not taken.">
          <input className="input" type="number" min={15} value={s.permissions.approvalTimeoutSeconds} onChange={(e) => set((x) => { x.permissions.approvalTimeoutSeconds = Number(e.target.value); })} />
        </Field>
        <p className="muted small">Per-tool permissions are in <a href="#/system">System → Tools and permissions</a>.</p>
      </Card>
      <Card title="Files">
        <Field label="Folders JARVIS may read freely" hint="One per line. Empty = your user folder. Writing anywhere still asks; system folders and credential files are always protected.">
          <textarea className="input mono" rows={4} value={s.files.allowedRoots.join("\n")} onChange={(e) => set((x) => { x.files.allowedRoots = e.target.value.split("\n").map((l) => l.trim()).filter(Boolean); })} />
        </Field>
      </Card>
    </div>
  );
}

const MEMORY_KINDS: [string, string][] = [
  ["fact", "Facts you tell me"],
  ["preference", "Your preferences"],
  ["person", "People"],
  ["project", "Projects"],
  ["context", "Temporary context"],
  ["pattern", "Learned patterns"],
];

function MemorySettings({ s, set }: P) {
  return (
    <Card>
      <Toggle label="Long-term memory" hint="When off, JARVIS won't store or use memories." checked={s.memory.enabled} onChange={(v) => set((x) => { x.memory.enabled = v; })} />
      <Toggle label="Keep conversation history" checked={s.memory.storeConversations} onChange={(v) => set((x) => { x.memory.storeConversations = v; })} />
      <Field label="Delete conversations older than (days)" hint="0 keeps them forever.">
        <input className="input" type="number" min={0} value={s.memory.conversationRetentionDays} onChange={(e) => set((x) => { x.memory.conversationRetentionDays = Number(e.target.value); })} />
      </Field>
      <div className="field">
        <span className="field-label">JARVIS may remember</span>
        {MEMORY_KINDS.map(([k, label]) => (
          <label key={k} className="row small">
            <input type="checkbox" checked={s.memory.allowedKinds.includes(k)} onChange={(e) => set((x) => { x.memory.allowedKinds = e.target.checked ? [...x.memory.allowedKinds, k] : x.memory.allowedKinds.filter((y) => y !== k); })} />
            {label}
          </label>
        ))}
      </div>
      <p className="muted small">
        Learning your patterns automatically is not active in this version (planned for Phase 8). When it arrives, learned items will be
        stored as unconfirmed, low-confidence hints that you can review and delete.
      </p>
      <div className="row">
        <ConfirmButton prompt="Delete all conversation history?" onConfirm={() => void del("/conversations")}><Trash2 size={14} /> Delete conversation history</ConfirmButton>
      </div>
    </Card>
  );
}

function Notifications({ s, set }: P) {
  return (
    <Card>
      <Toggle label="Windows notifications" checked={s.notifications.toastsEnabled} onChange={(v) => set((x) => { x.notifications.toastsEnabled = v; })} />
      <Toggle label="Speak important notifications" checked={s.notifications.speakImportant} onChange={(v) => set((x) => { x.notifications.speakImportant = v; })} />
      <Toggle label="Hold non-urgent notifications during meetings" checked={s.notifications.holdDuringMeetings} onChange={(v) => set((x) => { x.notifications.holdDuringMeetings = v; })} />
      <Toggle label="Hold non-urgent notifications during fullscreen apps and presentations" checked={s.notifications.holdDuringFullscreen} onChange={(v) => set((x) => { x.notifications.holdDuringFullscreen = v; })} />
      <div className="form-grid">
        <Field label="Quiet hours from" hint="24h, e.g. 23:00. Critical alerts still come through.">
          <input className="input" type="time" value={s.notifications.quietHoursStart} onChange={(e) => set((x) => { x.notifications.quietHoursStart = e.target.value; })} />
        </Field>
        <Field label="Quiet hours until">
          <input className="input" type="time" value={s.notifications.quietHoursEnd} onChange={(e) => set((x) => { x.notifications.quietHoursEnd = e.target.value; })} />
        </Field>
      </div>
    </Card>
  );
}

function Security() {
  const { status, refresh } = useStatus();
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [msg, setMsg] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const apply = async (newPin: string) => {
    try {
      await put("/auth/pin", { currentPin: current || null, newPin });
      setMsg(newPin ? "PIN saved." : "PIN removed.");
      setCurrent("");
      setNext("");
      setError(null);
      await refresh();
    } catch (e) {
      setError(e);
    }
  };
  return (
    <Card title="Dashboard PIN">
      <p className="muted small">
        A PIN locks this dashboard and the local API after inactivity. API keys are always stored encrypted ({status?.secretsProtection}). JARVIS's API only accepts connections from this computer.
      </p>
      <div className="form-grid">
        {status?.pinSet && (
          <Field label="Current PIN">
            <input className="input" type="password" inputMode="numeric" value={current} onChange={(e) => setCurrent(e.target.value)} />
          </Field>
        )}
        <Field label={status?.pinSet ? "New PIN" : "PIN"} hint="4–32 characters.">
          <input className="input" type="password" inputMode="numeric" value={next} onChange={(e) => setNext(e.target.value)} />
        </Field>
      </div>
      <div className="row">
        <button className="btn btn-primary" disabled={next.length < 4} onClick={() => apply(next)}>{status?.pinSet ? "Change PIN" : "Set PIN"}</button>
        {status?.pinSet && <button className="btn" disabled={!current} onClick={() => apply("")}>Remove PIN</button>}
      </div>
      {msg && <p className="muted">{msg}</p>}
      <ErrorNote error={error} />
    </Card>
  );
}
