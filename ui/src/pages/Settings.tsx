import { useEffect, useState, type ReactNode } from "react";
import QRCode from "qrcode";
import {
  AtSign, Bell, Brain, Check, Cpu, Download, Eye, FolderSearch, Globe, Keyboard, KeyRound, Mic, Palette, Plug, RefreshCw, Save, Shield, SlidersHorizontal, Smartphone, Trash2, Undo2,
} from "lucide-react";
import { del, get, mergePatch, patch, post, put, type BrowserStatus, type DevicesStatus, type PairingInfo, type InboxStatus, type MailAccountConfig, type ModelsResponse, type ProviderStatus, type PullState, type Settings, type ToolInfo } from "../api";
import { useStatus } from "../App";
import { useEvents } from "../events";
import { Badge, Card, ConfirmButton, ErrorNote, Field, PageHead, RiskBadge, Segmented, Toggle, fmtBytes, timeAgo, useLoad } from "../components/ui";
import { applyAppearance, settingsStore } from "../lib/settings";
import { playCue, type SoundKind } from "../lib/sounds";
import { tr, uiLocale } from "../lib/i18n";
import { ClearIndexButton, FilesIndexHint } from "./Files";
import { ConnectorTable } from "./Inbox";
import { CalendarSubscriptions } from "./Calendar";

const SECTIONS = [
  { id: "general", label: "General", icon: SlidersHorizontal },
  { id: "voice", label: "Voice", icon: Mic },
  { id: "ai", label: "AI", icon: Cpu },
  { id: "memory", label: "Memory", icon: Brain },
  { id: "files", label: "Files", icon: FolderSearch },
  { id: "accounts", label: "Accounts", icon: AtSign },
  { id: "web", label: "Web", icon: Globe },
  { id: "devices", label: "Devices", icon: Smartphone },
  { id: "security", label: "Security", icon: Shield },
  { id: "notifications", label: "Notifications", icon: Bell },
  { id: "appearance", label: "Appearance", icon: Palette },
  { id: "shortcuts", label: "Shortcuts", icon: Keyboard },
  { id: "plugins", label: "Tools & plugins", icon: Plug },
  { id: "privacy", label: "Privacy", icon: Eye },
  { id: "system", label: "System", icon: Cpu },
] as const;
type SectionId = (typeof SECTIONS)[number]["id"];

function sectionFromHash(): SectionId {
  const sub = location.hash.replace(/^#\/?settings\/?/, "").split(/[?&/]/)[0];
  return (SECTIONS.find((s) => s.id === sub)?.id ?? "general") as SectionId;
}

export function SettingsPage() {
  const [section, setSection] = useState<SectionId>(sectionFromHash);
  const loaded = useLoad(() => get<Settings>("/settings"));
  const [draft, setDraft] = useState<Settings | null>(null);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const { refresh } = useStatus();

  useEffect(() => {
    const onHash = () => setSection(sectionFromHash());
    window.addEventListener("hashchange", onHash);
    return () => window.removeEventListener("hashchange", onHash);
  }, []);
  useEffect(() => {
    if (loaded.data) setDraft(structuredClone(loaded.data));
  }, [loaded.data]);
  // Appearance previews live; leaving without saving restores the saved look.
  useEffect(() => {
    if (draft) applyAppearance(draft.appearance);
  }, [draft?.appearance]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => () => applyAppearance(settingsStore.getSnapshot()?.appearance), []);

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
      // Only what changed is sent, so a change made elsewhere meanwhile (e.g. a downloaded speech model) is kept.
      const result = await patch<Settings>("/settings", mergePatch(loaded.data, draft) ?? {});
      loaded.setData(result);
      setDraft(structuredClone(result));
      settingsStore.set(result);
      setSaved(true);
      setError(null);
      await refresh();
    } catch (e) {
      setError(e);
    }
  };

  const go = (id: SectionId) => { location.hash = `#/settings/${id}`; };
  const p = { s: draft, set };

  return (
    <div className="stack">
      <PageHead title={tr("Settings")} sub={tr("Every switch here changes how JARVIS behaves right away once saved.")} actions={saved && !dirty ? <span className="row muted small"><Check size={14} /> {tr("Saved")}</span> : null} />
      <ErrorNote error={error} />
      <div className="settings">
        <nav className="settings-nav" aria-label={tr("Settings sections")}>
          {SECTIONS.map((x) => (
            <button key={x.id} className={section === x.id ? "on" : ""} onClick={() => go(x.id)} aria-current={section === x.id ? "page" : undefined}>
              <x.icon size={15} /> {tr(x.label)}
            </button>
          ))}
        </nav>
        <div className="stack">
          {section === "general" && <General {...p} />}
          {section === "voice" && <Voice {...p} />}
          {section === "ai" && <Ai {...p} />}
          {section === "memory" && <MemorySettings {...p} />}
          {section === "files" && <FilesSettings {...p} />}
          {section === "accounts" && <AccountsSettings {...p} />}
          {section === "web" && <WebSettings {...p} />}
          {section === "devices" && <DevicesSettings {...p} />}
          {section === "security" && <Security {...p} />}
          {section === "notifications" && <Notifications {...p} />}
          {section === "appearance" && <AppearanceSection {...p} />}
          {section === "shortcuts" && <Shortcuts {...p} />}
          {section === "plugins" && <Plugins />}
          {section === "privacy" && <Privacy {...p} />}
          {section === "system" && <SystemSection {...p} />}
          {dirty && (
            <div className="savebar">
              <span className="small muted grow">{tr("Unsaved changes")}</span>
              <button className="btn btn-ghost" onClick={() => loaded.data && setDraft(structuredClone(loaded.data))}><Undo2 size={15} /> {tr("Discard")}</button>
              <button className="btn btn-primary" onClick={save}><Save size={15} /> {tr("Save changes")}</button>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

type P = { s: Settings; set: (fn: (s: Settings) => void) => void };

function General({ s, set }: P) {
  return (
    <Card title="General">
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
            <option value="auto">{tr("Automatic (match me)")}</option>
            <option value="en">{tr("Always English")}</option>
            <option value="ar">{tr("Always Egyptian Arabic")}</option>
          </select>
        </Field>
        <Field label="Conversation window (minutes)" hint="After this much silence, a new conversation context starts.">
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
    <>
      <Models s={s} set={set} />
      <Card title="Behaviour">
        <Toggle label="Allow cloud AI" hint="Off = nothing you say leaves this computer. On = providers marked 'cloud' may be used when online, with your own API key."
          checked={s.ai.allowCloud} onChange={(v) => set((x) => { x.ai.allowCloud = v; })} />
        <Toggle label="Stream replies as they're written" hint="Shows text word by word. Off waits for the complete answer."
          checked={s.ai.streamResponses} onChange={(v) => set((x) => { x.ai.streamResponses = v; })} />
        <Toggle label="Get the local model ready when JARVIS starts" hint="Loads it and lets it read JARVIS's instructions in the background, so your first message is answered sooner. Uses memory while the model is loaded."
          checked={s.ai.prepareModelAtStartup} onChange={(v) => set((x) => { x.ai.prepareModelAtStartup = v; })} />
        <div className="form-grid">
          <Field label="Local context window" hint="How much conversation local models see. Bigger remembers more but is slower and uses more memory.">
            <select className="input" value={s.ai.localContextTokens} onChange={(e) => set((x) => { x.ai.localContextTokens = Number(e.target.value); })}>
              {[4096, 8192, 16384, 32768].map((n) => <option key={n} value={n}>{tr("{n}K tokens", { n: n / 1024 })}{n === 8192 ? ` ${tr("(recommended)")}` : ""}</option>)}
            </select>
          </Field>
          <Field label="Max tool steps per request" hint="JARVIS stops and checks in after this many actions.">
            <input className="input" type="number" min={1} max={30} value={s.ai.maxAgentSteps} onChange={(e) => set((x) => { x.ai.maxAgentSteps = Number(e.target.value); })} />
          </Field>
          <Field label="Model timeout (seconds)" hint="How long a model may go without writing anything before JARVIS gives up. Local models also get extra time to load and to read a long request.">
            <input className="input" type="number" min={10} value={s.ai.requestTimeoutSeconds} onChange={(e) => set((x) => { x.ai.requestTimeoutSeconds = Number(e.target.value); })} />
          </Field>
        </div>
      </Card>
      <Card title="Providers">
        {s.ai.providers.map((p, i) => {
          const st = statuses[p.id];
          return (
            <div key={p.id} className="provider">
              <div className="row between wrap">
                <div className="row wrap">
                  <strong>{p.name}</strong>
                  <Badge tone={p.isLocal ? "good" : "warn"}>{p.isLocal ? tr("local") : tr("cloud")}</Badge>
                  {st && <Badge tone={st.available ? "good" : "bad"}>{st.available ? tr("{n} models", { n: st.models.length }) : tr("unavailable")}</Badge>}
                </div>
                <div className="row">
                  <button className="btn btn-ghost btn-sm" onClick={() => check(p.id)} disabled={!p.enabled} title={tr("Test connection")}><RefreshCw size={13} /> {tr("Test")}</button>
                  <Toggle label="Enabled" checked={p.enabled} onChange={(v) => set((x) => { x.ai.providers[i].enabled = v; })} />
                </div>
              </div>
              {st && !st.available && <div className="small muted">{st.message}</div>}
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
                      <Badge tone="good">{tr("API key stored ({protection})", { protection: secrets.data.protection })}</Badge>
                      <button className="btn btn-ghost btn-sm" onClick={async () => { await del(`/secrets/${p.apiKeySecret}`); void secrets.reload(); }}><Trash2 size={13} /> {tr("Remove")}</button>
                    </>
                  ) : (
                    <>
                      <input className="input grow mono" type="password" placeholder={tr("Paste API key (stored encrypted, never shown again)")} value={keys[p.id] ?? ""} onChange={(e) => setKeys((k) => ({ ...k, [p.id]: e.target.value }))} />
                      <button className="btn btn-sm" disabled={!keys[p.id]} onClick={async () => { await put(`/secrets/${p.apiKeySecret}`, { value: keys[p.id] }); setKeys((k) => ({ ...k, [p.id]: "" })); void secrets.reload(); }}>{tr("Save key")}</button>
                    </>
                  )}
                </div>
              )}
            </div>
          );
        })}
      </Card>
      <Card title="Which model does what">
        <p className="small muted">
          {tr("Direct commands never use AI. Other requests are classified (general / reasoning / coding / vision) and go to the first available choice, then fall back down the list. An empty model means “the best installed one” (tool-capable, ~7-8B preferred).")}
        </p>
        {Object.entries(s.ai.roles).map(([role, bindings]) => (
          <div key={role} className="role">
            <div className="role-name">{role}</div>
            <div className="stack-sm grow">
              {bindings.map((b, j) => (
                <div key={j} className="row wrap">
                  <span className="meta">{j + 1}.</span>
                  <select className="input" value={b.provider} onChange={(e) => set((x) => { x.ai.roles[role][j].provider = e.target.value; })}>
                    {s.ai.providers.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
                  </select>
                  <input className="input mono grow" list={`models-${role}-${j}`} placeholder={tr("(best installed)")} value={b.model} onChange={(e) => set((x) => { x.ai.roles[role][j].model = e.target.value; })} />
                  <datalist id={`models-${role}-${j}`}>{allModels(b.provider).map((m) => <option key={m} value={m} />)}</datalist>
                  <button className="btn btn-ghost btn-icon" onClick={() => set((x) => { x.ai.roles[role].splice(j, 1); })} aria-label={tr("Remove")}><Trash2 size={14} /></button>
                </div>
              ))}
              <button className="btn btn-ghost btn-sm" style={{ alignSelf: "flex-start" }} onClick={() => set((x) => { x.ai.roles[role].push({ provider: s.ai.providers[0]?.id ?? "", model: "" }); })}>{tr("+ Add fallback")}</button>
            </div>
          </div>
        ))}
      </Card>
    </>
  );
}

/** Installed local models with their real capabilities, and one-click downloads of recommended ones. */
function Models({ s, set }: P) {
  const models = useLoad(() => get<ModelsResponse>("/ai/models"));
  const [pulls, setPulls] = useState<Record<string, PullState>>({});
  const [custom, setCustom] = useState("");
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    if (models.data) setPulls(Object.fromEntries(models.data.pulls.map((p) => [p.model, p])));
  }, [models.data]);
  useEvents(["ai.model"], (e) => {
    const st = e.data as PullState;
    setPulls((p) => ({ ...p, [st.model]: st }));
    if (st.done) void models.reload();
  });
  const pull = async (provider: string, model: string) => {
    try { setError(null); await post(`/ai/providers/${provider}/pull`, { model }); } catch (e) { setError(e); }
  };
  const general = s.ai.roles.general ?? [];
  const pinned = (provider: string, model: string) => general.some((b) => b.provider === provider && b.model === model);
  const pin = (provider: string, model: string) => set((x) => {
    const list = x.ai.roles.general ?? (x.ai.roles.general = []);
    const i = list.findIndex((b) => b.provider === provider);
    if (i >= 0) list[i].model = model; else list.unshift({ provider, model });
  });

  const pullable = models.data?.providers.find((p) => p.canPull && p.reachable);
  const installed = new Set(models.data?.providers.flatMap((p) => p.models.map((m) => m.name)) ?? []);

  return (
    <Card title="Local models" actions={<button className="btn btn-ghost btn-sm" onClick={() => models.reload()}><RefreshCw size={13} /> {tr("Refresh")}</button>}>
      <ErrorNote error={error ?? models.error} />
      {models.data?.providers.map((p) => (
        <div key={p.provider} className="stack-sm">
          <div className="row"><strong className="small">{p.name}</strong>{!p.reachable && <Badge tone="bad">{tr("not reachable")}</Badge>}</div>
          {!p.reachable && p.provider === "ollama" && (
            <div className="note">{tr("Ollama isn't running. Install it free from")} <code>ollama.com</code>{tr("; JARVIS will detect it automatically.")}</div>
          )}
          {p.models.map((m) => (
            <div key={m.name} className="model-row">
              <span className="mono grow">{m.name}</span>
              {m.parameterSize && <span className="meta">{m.parameterSize}</span>}
              {m.sizeBytes ? <span className="meta">{fmtBytes(m.sizeBytes)}</span> : null}
              {m.contextLength ? <span className="meta">{Math.round(m.contextLength / 1024)}K ctx</span> : null}
              {m.capabilities.map((c) => <Badge key={c} tone={c === "tools" ? "good" : c === "vision" ? "info" : c === "embedding" ? "accent" : "neutral"}>{c}</Badge>)}
              {!m.capabilitiesReported && <Badge tone="neutral" title={tr("This Ollama version doesn't report capabilities; guessed from the name")}>{tr("guessed")}</Badge>}
              {!m.capabilities.includes("embedding") && (
                pinned(p.provider, m.name)
                  ? <Badge tone="good">{tr("default")}</Badge>
                  : <button className="btn btn-ghost btn-sm" onClick={() => pin(p.provider, m.name)} title={tr("Use for general conversation")}>{tr("Use")}</button>
              )}
            </div>
          ))}
          {p.reachable && p.models.length === 0 && <div className="small muted">{tr("No models installed yet — download one below.")}</div>}
        </div>
      ))}
      {pullable && (
        <>
          <div className="divider" />
          <div className="hud-label">{tr("Recommended (free)")}</div>
          {models.data?.recommended.map((r) => {
            const st = pulls[r.name];
            const has = installed.has(r.name) || installed.has(`${r.name}:latest`);
            const running = st && !st.done;
            return (
              <div key={r.name} className="model-row">
                <div className="grow">
                  <div><span className="mono">{r.name}</span> <span className="meta">{r.size} · {r.purpose}</span></div>
                  <div className="small muted">{r.notes}</div>
                  {running && (
                    <div className="stack-sm" style={{ marginTop: 6 }}>
                      <progress max={st.total ?? 1} value={st.completed ?? 0} />
                      <span className="meta">{st.status}{st.total ? ` · ${fmtBytes(st.completed ?? 0)} / ${fmtBytes(st.total)}` : ""}</span>
                    </div>
                  )}
                  {st?.error && <div className="error-note small">{st.error}</div>}
                </div>
                {has ? <Badge tone="good">{tr("installed")}</Badge> : (
                  <button className="btn btn-sm" disabled={!!running} onClick={() => pull(pullable.provider, r.name)}><Download size={13} /> {running ? tr("Downloading") : tr("Download")}</button>
                )}
              </div>
            );
          })}
          <form className="row" onSubmit={(e) => { e.preventDefault(); if (custom.trim()) { void pull(pullable.provider, custom.trim()); setCustom(""); } }}>
            <input className="input mono grow" placeholder={tr("Any Ollama model, e.g. llama3.1:8b")} value={custom} onChange={(e) => setCustom(e.target.value)} />
            <button className="btn btn-sm" disabled={!custom.trim()} type="submit"><Download size={13} /> {tr("Pull")}</button>
          </form>
          {Object.values(pulls).filter((x) => !models.data?.recommended.some((r) => r.name === x.model) && !x.done).map((x) => (
            <div key={x.model} className="stack-sm"><span className="meta">{x.model}: {x.status}</span><progress max={x.total ?? 1} value={x.completed ?? 0} /></div>
          ))}
        </>
      )}
    </Card>
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
    <>
      <Card title="Speech recognition (local Whisper)">
        <p className="small muted">{tr("Runs entirely on this computer. Models download once from the whisper.cpp project. For Egyptian Arabic, “small” is noticeably better than “base”.")}</p>
        <ul className="list list-rows">
          {models.data?.models.map((m) => {
            const pr = progress[m.name];
            return (
              <li key={m.name}>
                <input type="radio" name="stt" aria-label={tr("Use {name}", { name: m.name })} checked={s.voice.sttModel === m.name} disabled={!m.installed} onChange={() => set((x) => { x.voice.sttModel = m.name; })} />
                <div className="grow">
                  <strong>{m.name}</strong> <span className="meta">~{Math.round(m.approxBytes / 1e6)} MB</span>
                  <div className="small muted">{m.description}</div>
                  {pr && !m.installed && (pr.error ? <div className="error-note">{pr.error}</div> : <progress max={pr.total ?? m.approxBytes} value={pr.bytes} />)}
                </div>
                {m.installed ? <Badge tone="good">{tr("installed")}</Badge> : (
                  <button className="btn btn-sm" disabled={models.data?.downloading} onClick={() => post(`/voice/models/${m.name}/download`).then(() => models.reload())}><Download size={13} /> {tr("Download")}</button>
                )}
              </li>
            );
          })}
        </ul>
      </Card>
      <Card title="Wake word and conversation">
        <Toggle label="Listen for “Jarvis” (wake word)" hint="Keeps the microphone open while JARVIS runs. The orb and the top bar show when it's listening. Off by default." checked={s.voice.wakeWordEnabled} onChange={(v) => set((x) => { x.voice.wakeWordEnabled = v; })} />
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
        {!voices.data?.available && <p className="small muted">{tr("No speech voices available on this system.")}</p>}
        <div className="form-grid">
          <Field label="English voice">
            <select className="input" value={s.voice.voiceEn} onChange={(e) => set((x) => { x.voice.voiceEn = e.target.value; })}>
              <option value="">{tr("Automatic")}</option>
              {enVoices.map((v) => <option key={v.id} value={v.id}>{v.name} ({v.language})</option>)}
            </select>
          </Field>
          <Field label="Arabic voice" hint={arVoices.length ? undefined : "No Arabic voice installed. Windows Settings → Time & language → Speech → Add voices (Arabic – Egypt)."}>
            <select className="input" value={s.voice.voiceAr} onChange={(e) => set((x) => { x.voice.voiceAr = e.target.value; })}>
              <option value="">{tr("Automatic")}</option>
              {arVoices.map((v) => <option key={v.id} value={v.id}>{v.name} ({v.language})</option>)}
            </select>
          </Field>
          <Field label="Speaking rate">
            <input className="input" type="number" step={0.1} min={0.5} max={2} value={s.voice.rate} onChange={(e) => set((x) => { x.voice.rate = Number(e.target.value); })} />
          </Field>
        </div>
        <div className="row wrap">
          <button className="btn btn-sm" onClick={() => post("/voice/speak", { text: "Good evening, Sir. All systems are operational." })}>{tr("Test English voice")}</button>
          <button className="btn btn-sm" onClick={() => post("/voice/speak", { text: "مساء الخير يا فندم، كل حاجة شغالة تمام.", lang: "ar" })}>جرب الصوت العربي</button>
        </div>
      </Card>
    </>
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
    <Card title="Memory">
      <Toggle label="Long-term memory" hint="When off, JARVIS won't store or use memories." checked={s.memory.enabled} onChange={(v) => set((x) => { x.memory.enabled = v; })} />
      <Toggle label="Keep conversation history" checked={s.memory.storeConversations} onChange={(v) => set((x) => { x.memory.storeConversations = v; })} />
      <Toggle label="Learn from my activity" hint="Off by default. JARVIS reviews its own activity log every few hours for routines and preferences, and proposes them as unconfirmed items you confirm or reject in Memory → To review. Nothing becomes a fact without you."
        checked={s.memory.learnPatterns} onChange={(v) => set((x) => { x.memory.learnPatterns = v; })} />
      <Toggle label="Learn my writing style" hint="Off by default. JARVIS reads the email you sent (your Sent folder, read-only, and drafts you approved) and proposes a description of how you write — greeting, sign-off, length, language — for drafts in your voice. You confirm or reject it in Memory → To review. Turning this off deletes the collected samples."
        checked={s.memory.learnWritingStyle} onChange={(v) => set((x) => { x.memory.learnWritingStyle = v; })} />
      <Toggle label="Remember things from conversations" hint="Off by default. When a conversation has gone quiet, the AI picks out lasting things you said about yourself — preferences, people, projects, commitments. Each must quote your own words (checked), and each waits for you in Memory → To review."
        checked={s.memory.summarizeConversations} onChange={(v) => set((x) => { x.memory.summarizeConversations = v; })} />
      <Field label="Delete conversations older than (days)" hint="0 keeps them forever.">
        <input className="input" type="number" min={0} value={s.memory.conversationRetentionDays} onChange={(e) => set((x) => { x.memory.conversationRetentionDays = Number(e.target.value); })} />
      </Field>
      <Field label="Semantic search model" hint="A local embedding model (Ollama) lets JARVIS find memories by meaning, in English and Arabic. bge-m3 is recommended; empty turns semantic search off.">
        <input className="input mono" placeholder={tr("(off)")} value={s.ai.embeddingModel} onChange={(e) => set((x) => { x.ai.embeddingModel = e.target.value.trim(); })} />
      </Field>
      <div className="field">
        <span className="field-label">{tr("JARVIS may remember")}</span>
        {MEMORY_KINDS.map(([k, label]) => (
          <label key={k} className="row small">
            <input type="checkbox" checked={s.memory.allowedKinds.includes(k)} onChange={(e) => set((x) => { x.memory.allowedKinds = e.target.checked ? [...x.memory.allowedKinds, k] : x.memory.allowedKinds.filter((y) => y !== k); })} />
            {tr(label)}
          </label>
        ))}
      </div>
      <div className="row">
        <ConfirmButton prompt="Delete all conversation history?" onConfirm={() => void del("/conversations")}><Trash2 size={14} /> {tr("Delete conversation history")}</ConfirmButton>
      </div>
    </Card>
  );
}

function Notifications({ s, set }: P) {
  const cues: [SoundKind, string, string][] = [
    ["wake", "Wake", "When JARVIS starts listening"],
    ["accepted", "Accepted", "When a request is received"],
    ["processing", "Processing", "A soft tick for each action (off by default)"],
    ["completed", "Completed", "When a request finishes"],
    ["warning", "Warning", "When an approval is needed"],
    ["error", "Error", "When something fails"],
    ["notification", "Notification", "When a notification is delivered"],
  ];
  return (
    <>
      <Card title="Notifications">
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
      <Card title="Interface sounds">
        <Toggle label="Play interface sounds" hint="Short synthesized cues from the dashboard and command console while they're open. Off silences everything." checked={s.sounds.enabled} onChange={(v) => set((x) => { x.sounds.enabled = v; })} />
        <Field label={tr("Volume {n}%", { n: Math.round(s.sounds.volume * 100) })}>
          <input type="range" min={0} max={1} step={0.05} value={s.sounds.volume} disabled={!s.sounds.enabled} onChange={(e) => set((x) => { x.sounds.volume = Number(e.target.value); })} />
        </Field>
        {cues.map(([k, label, hint]) => (
          <div key={k} className="row">
            <div className="grow"><Toggle label={label} hint={hint} checked={s.sounds[k]} onChange={(v) => set((x) => { x.sounds[k] = v; })} /></div>
            <button className="btn btn-ghost btn-sm" disabled={!s.sounds.enabled} onClick={() => playCue(k, s.sounds.volume)}>{tr("Play")}</button>
          </div>
        ))}
      </Card>
    </>
  );
}

function AppearanceSection({ s, set }: P) {
  const a = s.appearance;
  const accents: [Settings["appearance"]["accent"], string, string][] = [
    ["cyan", "Cyan", "#3fd0ff"], ["amber", "Amber", "#ffba52"], ["violet", "Violet", "#aa8cff"], ["green", "Green", "#46e8aa"],
  ];
  return (
    <Card title="Appearance">
      <p className="small muted">{tr("Changes preview immediately; save to keep them (the desktop orb follows too).")}</p>
      <Row label={tr("Interface language")} hint={tr("العربية switches the whole interface to right-to-left. Replies follow Settings → General → Reply language.")}>
        <Segmented label="Interface language" value={a.language} onChange={(v) => set((x) => { x.appearance.language = v; })} options={[["en", "English"], ["ar", "العربية"]]} />
      </Row>
      <Row label={tr("Theme")} hint={tr("Dark is the HUD; light suits bright rooms; auto follows Windows.")}>
        <Segmented label="Theme" value={a.theme} onChange={(v) => set((x) => { x.appearance.theme = v; })} options={[["dark", "Dark"], ["light", "Light"], ["auto", "Auto"]]} />
      </Row>
      <Row label={tr("Energy colour")}>
        <div className="swatches" role="radiogroup" aria-label={tr("Energy colour")}>
          {accents.map(([v, label, hex]) => (
            <button key={v} type="button" role="radio" aria-checked={a.accent === v} aria-label={tr(label)} title={tr(label)}
              className={`swatch ${a.accent === v ? "on" : ""}`} style={{ background: hex, color: hex }} onClick={() => set((x) => { x.appearance.accent = v; })} />
          ))}
        </div>
      </Row>
      <Row label={tr("Motion")} hint={tr("Reduced keeps state changes but stops ambient motion; Off removes all animation.")}>
        <Segmented label="Motion" value={a.motion} onChange={(v) => set((x) => { x.appearance.motion = v; })} options={[["full", "Full"], ["reduced", "Reduced"], ["off", "Off"]]} />
      </Row>
      <Row label={tr("Density")}>
        <Segmented label="Density" value={a.density} onChange={(v) => set((x) => { x.appearance.density = v; })} options={[["comfortable", "Comfortable"], ["compact", "Compact"]]} />
      </Row>
      <Row label={tr("Text size {n}%", { n: Math.round(a.textScale * 100) })}>
        <input type="range" min={0.85} max={1.4} step={0.05} value={a.textScale} onChange={(e) => set((x) => { x.appearance.textScale = Number(e.target.value); })} />
      </Row>
      <Row label={tr("Desktop orb size {n}px", { n: a.orbSize })}>
        <input type="range" min={48} max={128} step={4} value={a.orbSize} onChange={(e) => set((x) => { x.appearance.orbSize = Number(e.target.value); })} />
      </Row>
      <Toggle label="Holographic grid and scan lines" checked={a.hudEffects} onChange={(v) => set((x) => { x.appearance.hudEffects = v; })} />
      <Toggle label="Context panel on wide screens" hint="The right-hand panel with what's happening now and next." checked={a.contextPanel} onChange={(v) => set((x) => { x.appearance.contextPanel = v; })} />
    </Card>
  );
}

function Row({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <div className="toggle-row" style={{ cursor: "default" }}>
      <span className="toggle-text"><span>{label}</span>{hint && <small>{hint}</small>}</span>
      {children}
    </div>
  );
}

function Shortcuts({ s, set }: P) {
  const items: [keyof Settings["shortcuts"], string, string][] = [
    ["commandConsole", "Command console", "Opens the cinematic command console from anywhere."],
    ["pushToTalk", "Push to talk", "Starts listening for one command."],
    ["dashboard", "Open dashboard", "Optional."],
  ];
  return (
    <Card title="Keyboard shortcuts">
      <p className="small muted">{tr("Global shortcuts work in every app. Click a box and press the new combination (it must include Ctrl, Alt, Shift or Win). If another app already owns a combination, the tray icon tells you.")}</p>
      {items.map(([k, label, hint]) => (
        <Row key={k} label={tr(label)} hint={tr(hint)}>
          <div className="row">
            <ShortcutInput value={s.shortcuts[k]} onChange={(v) => set((x) => { x.shortcuts[k] = v; })} />
            {s.shortcuts[k] && <button className="btn btn-ghost btn-icon" onClick={() => set((x) => { x.shortcuts[k] = ""; })} aria-label={tr("Clear {label}", { label: tr(label) })}><Trash2 size={14} /></button>}
          </div>
        </Row>
      ))}
      <Row label={tr("Inside the dashboard")} hint={tr("Ctrl+K or / opens the console; Esc closes it.")}><span className="kbd">Ctrl K</span></Row>
    </Card>
  );
}

function ShortcutInput({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  const [recording, setRecording] = useState(false);
  return (
    <input
      className="input shortcut-input"
      readOnly
      value={recording ? tr("Press keys…") : value || tr("None")}
      onFocus={() => setRecording(true)}
      onBlur={() => setRecording(false)}
      onKeyDown={(e) => {
        e.preventDefault();
        if (e.key === "Escape") { (e.target as HTMLInputElement).blur(); return; }
        const combo = comboFrom(e);
        if (combo) { onChange(combo); (e.target as HTMLInputElement).blur(); }
      }}
      aria-label={tr("Shortcut")}
      style={{ width: 190 }}
    />
  );
}

function comboFrom(e: React.KeyboardEvent): string | null {
  const key = e.key;
  if (["Control", "Alt", "Shift", "Meta"].includes(key)) return null;
  const mods = [e.ctrlKey && "Ctrl", e.altKey && "Alt", e.shiftKey && "Shift", e.metaKey && "Win"].filter(Boolean) as string[];
  if (mods.length === 0) return null;
  const name = key === " " ? "Space" : /^F\d{1,2}$/.test(key) ? key : key.length === 1 ? key.toUpperCase() : null;
  if (!name || !/^([A-Z0-9]|Space|F\d{1,2})$/.test(name)) return null;
  return [...mods, name].join("+");
}

function Plugins() {
  const tools = useLoad(() => get<ToolInfo[]>("/tools"));
  return (
    <Card title="Tools & plugins">
      <p className="small muted">
        {tr("Everything JARVIS can do is a tool with a declared risk. Safe tools run immediately; sensitive tools ask first unless you allow them; critical actions (deleting, power, destructive commands, sending) always ask, whatever you choose here. Third-party and generated plugins will appear here with the permissions they request — never with unrestricted access.")}
      </p>
      <div className="table-wrap">
        <table className="table">
          <thead><tr><th>{tr("Tool")}</th><th>{tr("What it does")}</th><th>{tr("Risk")}</th><th>{tr("Permission")}</th></tr></thead>
          <tbody>
            {tools.data?.map((t) => (
              <tr key={t.name}>
                <td className="mono small nowrap">{t.name} {t.requiresInternet && <Badge tone="info">{tr("web")}</Badge>}</td>
                <td className="small">{t.description}</td>
                <td><RiskBadge risk={t.risk} /></td>
                <td>
                  <select className="input input-sm" value={t.policy} onChange={async (e) => { await put(`/tools/${t.name}/policy`, { policy: e.target.value }); void tools.reload(); }}>
                    <option value="Default">{tr("Default")}</option>
                    <option value="Allow">{tr("Always allow")}</option>
                    <option value="Ask">{tr("Always ask")}</option>
                    <option value="Block">{tr("Block")}</option>
                  </select>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Card>
  );
}

function DevicesSettings({ s, set }: P) {
  const status = useLoad(() => get<DevicesStatus>("/devices"));
  const [pairing, setPairing] = useState<PairingInfo | null>(null);
  const [qr, setQr] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEvents(["devices."], () => { void status.reload(); setPairing(null); });
  useEffect(() => {
    if (!pairing) { setQr(null); return; }
    // The QR holds the app link (URL, code and certificate fingerprint); the browser page link is shown below it.
    void QRCode.toDataURL(pairing.appLink, { margin: 1, width: 240, errorCorrectionLevel: "M" }).then(setQr);
    const left = new Date(pairing.expires).getTime() - Date.now();
    const t = setTimeout(() => setPairing(null), Math.max(0, left));
    return () => clearTimeout(t);
  }, [pairing]);
  const d = status.data;
  const savedEnabled = d?.enabled ?? false;
  const pair = async () => {
    try { setPairing(await post<PairingInfo>("/devices/pairing")); setError(null); }
    catch (e) { setError(e); }
  };
  const active = d?.devices.filter((x) => !x.revokedAt) ?? [];
  return (
    <>
      <Card title={tr("Phone companion")}>
        <p className="small muted">
          {tr("Use JARVIS from your phone on the same Wi-Fi: today's briefing, approvals, alerts and chat. Off by default. When on, a second, separate server listens on your network with JARVIS's own certificate; only phones you pair (with a one-time code shown here) can use it, and you can remove any phone at any time. The dashboard and full API still only accept this PC.")}
        </p>
        <Toggle label={tr("Allow paired phones")} hint={tr("Starts the companion server after you save.")} checked={s.companion.enabled} onChange={(v) => set((x) => { x.companion.enabled = v; })} />
        <Toggle label={tr("Phones may approve or refuse actions")} hint={tr("Off = phones only see what's waiting; you approve here.")} checked={s.companion.allowApprovals} onChange={(v) => set((x) => { x.companion.allowApprovals = v; })} />
        <Field label={tr("Port")} hint={tr("Windows may ask to allow JARVIS through the firewall on private networks.")}>
          <input className="input" type="number" min={1024} max={65535} value={s.companion.port} onChange={(e) => set((x) => { x.companion.port = Number(e.target.value); })} style={{ width: 120 }} />
        </Field>
        <div className="note small">{tr("Anything a phone asks for that changes something on this PC always waits for your confirmation, even if you normally allow it.")}</div>
        <div className="row wrap">
          {d?.running ? <Badge tone="good">{tr("Listening")} · {d.addresses.join(", ") || "—"} : {d.port}</Badge>
            : savedEnabled ? <Badge tone="bad">{tr("Not running")}</Badge> : <Badge>{tr("Off")}</Badge>}
          {d?.error && <span className="small" style={{ color: "var(--bad)" }}>{d.error}</span>}
        </div>
      </Card>
      <Card title={tr("Pair a phone")} actions={<button className="btn btn-primary btn-sm" disabled={!d?.running} onClick={pair}><Smartphone size={14} /> {tr("Show pairing code")}</button>}>
        {!d?.running && <p className="small muted">{tr("Turn on the phone companion and save first.")}</p>}
        <ErrorNote error={error} />
        {pairing && (
          <div className="pairing">
            {qr && <img src={qr} width={240} height={240} alt={tr("Pairing QR code")} className="pairing-qr" />}
            <div className="stack">
              <p className="small">{tr("Scan with the JARVIS Android app, or open the link on your phone and enter the code. The code works once, for five minutes.")}</p>
              <div className="pairing-code mono">{pairing.code}</div>
              <p className="small mono" dir="ltr">{pairing.link}</p>
              <p className="small muted">
                {tr("The phone's browser will warn that the certificate isn't trusted — JARVIS makes its own. Check the fingerprint matches before continuing:")}
              </p>
              <p className="small mono" dir="ltr" style={{ wordBreak: "break-all" }}>{pairing.fingerprint}</p>
            </div>
          </div>
        )}
      </Card>
      <Card title={tr("Paired phones")}>
        {active.length === 0 ? <p className="small muted">{tr("No phones paired.")}</p> : (
          <ul className="list">
            {active.map((x) => (
              <li key={x.id}>
                <span className="grow"><strong>{x.name}</strong> <span className="meta">{tr("paired")} {timeAgo(x.createdAt)} · {tr("last seen")} {timeAgo(x.lastSeen)}{x.lastAddress ? ` · ${x.lastAddress}` : ""}</span></span>
                <ConfirmButton className="btn btn-ghost btn-sm" prompt={`${tr("Remove this phone? It will need to pair again.")} (${x.name})`} onConfirm={async () => { await del(`/devices/${x.id}`); void status.reload(); }}><Trash2 size={13} /> {tr("Remove")}</ConfirmButton>
              </li>
            ))}
          </ul>
        )}
      </Card>
    </>
  );
}

function Security({ s, set }: P) {
  const { status, refresh } = useStatus();
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [msg, setMsg] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const secrets = useLoad(() => get<{ names: string[]; protection: string }>("/secrets"));
  const apply = async (newPin: string) => {
    try {
      await put("/auth/pin", { currentPin: current || null, newPin });
      setMsg(newPin ? tr("PIN saved.") : tr("PIN removed."));
      setCurrent("");
      setNext("");
      setError(null);
      await refresh();
    } catch (e) {
      setError(e);
    }
  };
  return (
    <>
      <Card title="Approvals">
        <Toggle label="Run sensitive actions without asking" hint="Sensitive = changing files, running programs, typing into apps. Critical actions always ask."
          checked={s.permissions.autoApproveSensitive} onChange={(v) => set((x) => { x.permissions.autoApproveSensitive = v; })} />
        <Field label="Approval timeout (seconds)" hint="If you don't answer, the action is not taken.">
          <input className="input" type="number" min={15} value={s.permissions.approvalTimeoutSeconds} onChange={(e) => set((x) => { x.permissions.approvalTimeoutSeconds = Number(e.target.value); })} />
        </Field>
        <Field label="Folders JARVIS may read freely" hint="One per line. Empty = your user folder. Writing anywhere still asks; system folders and credential files are always protected.">
          <textarea className="input mono" rows={3} value={s.files.allowedRoots.join("\n")} onChange={(e) => set((x) => { x.files.allowedRoots = e.target.value.split("\n").map((l) => l.trim()).filter(Boolean); })} />
        </Field>
      </Card>
      <Card title="Dashboard PIN">
        <p className="small muted">{tr("A PIN locks this dashboard and the local API after inactivity. JARVIS's API only accepts connections from this computer.")}</p>
        <div className="form-grid">
          {status?.pinSet && (
            <Field label="Current PIN">
              <input className="input" type="password" inputMode="numeric" value={current} onChange={(e) => setCurrent(e.target.value)} />
            </Field>
          )}
          <Field label={status?.pinSet ? tr("New PIN") : "PIN"} hint="4–32 characters.">
            <input className="input" type="password" inputMode="numeric" value={next} onChange={(e) => setNext(e.target.value)} />
          </Field>
          <Field label="Lock after (minutes idle)">
            <input className="input" type="number" min={1} value={s.security.unlockMinutes} onChange={(e) => set((x) => { x.security.unlockMinutes = Number(e.target.value); })} />
          </Field>
        </div>
        <div className="row">
          <button className="btn btn-primary btn-sm" disabled={next.length < 4} onClick={() => apply(next)}>{status?.pinSet ? tr("Change PIN") : tr("Set PIN")}</button>
          {status?.pinSet && <button className="btn btn-sm" disabled={!current} onClick={() => apply("")}>{tr("Remove PIN")}</button>}
        </div>
        {msg && <p className="muted small">{msg}</p>}
        <ErrorNote error={error} />
      </Card>
      <Card title="Stored secrets">
        <p className="small muted">{tr("API keys and connector tokens, encrypted with {protection}. Values are never shown or sent to the dashboard.", { protection: secrets.data?.protection ?? "…" })}</p>
        {secrets.data?.names.length ? (
          <ul className="list">
            {secrets.data.names.map((n) => (
              <li key={n}><span className="mono small">{n}</span>
                <ConfirmButton className="btn btn-ghost btn-sm" prompt={tr("Delete the secret \"{name}\"?", { name: n })} onConfirm={async () => { await del(`/secrets/${n}`); void secrets.reload(); }}><Trash2 size={13} /> {tr("Delete")}</ConfirmButton>
              </li>
            ))}
          </ul>
        ) : <p className="small muted">{tr("No secrets stored.")}</p>}
      </Card>
    </>
  );
}

function Privacy({ s, set }: P) {
  const { status } = useStatus();
  return (
    <>
      <Card title="What JARVIS may see and hear">
        <Toggle label="Allow screen capture" hint="Screenshots by command or by the AI. Off blocks every screen-capture tool." checked={s.privacy.allowScreenCapture} onChange={(v) => set((x) => { x.privacy.allowScreenCapture = v; })} />
        <Toggle label="Wake word (always-on microphone)" hint="Off by default. When on, the top bar shows “Mic on”." checked={s.voice.wakeWordEnabled} onChange={(v) => set((x) => { x.voice.wakeWordEnabled = v; })} />
        <Toggle label="Allow the camera (one photo when you ask)" hint="Off by default. Even when on, JARVIS asks every time, takes a single photo, never saves it and never records video. Windows' camera light turns on."
          checked={s.privacy.allowCamera} onChange={(v) => set((x) => { x.privacy.allowCamera = v; })} />
        <div className="note">{tr("Presence uses only Windows signals (active window, idle time, fullscreen, whether another app is using the microphone) — never the camera.")}</div>
      </Card>
      <Card title="What leaves this computer">
        <Toggle label="Allow cloud AI" hint="Off = conversations never leave this PC." checked={s.ai.allowCloud} onChange={(v) => set((x) => { x.ai.allowCloud = v; })} />
        <p className="small muted">{tr("Web search and page reading go to the sites involved only when you (or the AI, through an audited tool) ask. Nothing is sent anywhere else.")}</p>
      </Card>
      <Card title="What JARVIS keeps">
        <Toggle label="Long-term memory" checked={s.memory.enabled} onChange={(v) => set((x) => { x.memory.enabled = v; })} />
        <Toggle label="Conversation history" checked={s.memory.storeConversations} onChange={(v) => set((x) => { x.memory.storeConversations = v; })} />
        <Field label="Delete conversations older than (days)" hint="0 keeps them forever.">
          <input className="input" type="number" min={0} value={s.memory.conversationRetentionDays} onChange={(e) => set((x) => { x.memory.conversationRetentionDays = Number(e.target.value); })} />
        </Field>
        <p className="meta">{tr("All data lives in {dir}", { dir: status?.dataDir ?? "" })}</p>
        <div className="row wrap">
          <ConfirmButton prompt="Delete all conversation history?" onConfirm={() => void del("/conversations")}><Trash2 size={14} /> {tr("Delete conversations")}</ConfirmButton>
          <ConfirmButton prompt="Delete ALL memories? This cannot be undone." onConfirm={() => void del("/memory?confirm=true")}><Trash2 size={14} /> {tr("Delete all memories")}</ConfirmButton>
        </div>
      </Card>
    </>
  );
}

function SystemSection({ s, set }: P) {
  const { status, refresh } = useStatus();
  return (
    <>
      <Card title="Runtime">
        <dl className="kv small">
          <dt>{tr("Version")}</dt><dd>{status?.version}</dd>
          <dt>{tr("Platform")}</dt><dd>{status?.platformDescription}</dd>
          <dt>{tr("Data folder")}</dt><dd className="mono">{status?.dataDir}</dd>
        </dl>
        <div className="row wrap">
          <button className="btn btn-sm" onClick={async () => { await post(status?.paused ? "/runtime/resume" : "/runtime/pause"); await refresh(); }}>
            {status?.paused ? tr("Resume JARVIS") : tr("Pause JARVIS")}
          </button>
          <ConfirmButton className="btn btn-danger btn-sm" prompt="Shut down JARVIS completely? Reminders and voice stop until you start it again." onConfirm={() => void post("/runtime/shutdown")}>{tr("Shut down JARVIS")}</ConfirmButton>
          <a className="btn btn-ghost btn-sm" href="#/system">{tr("Health & capabilities")}</a>
        </div>
      </Card>
      <Card title="Network">
        <div className="form-grid">
          <Field label="Local API port" hint="Takes effect after JARVIS restarts. If it's busy, the next free port is used.">
            <input className="input" type="number" min={1024} max={65535} value={s.runtime.port} onChange={(e) => set((x) => { x.runtime.port = Number(e.target.value); })} />
          </Field>
          <Field label="Connectivity check every (seconds)">
            <input className="input" type="number" min={5} value={s.runtime.connectivityProbeSeconds} onChange={(e) => set((x) => { x.runtime.connectivityProbeSeconds = Number(e.target.value); })} />
          </Field>
          <Field label="Connectivity probe URL" hint="Used with other well-known endpoints to decide online/offline.">
            <input className="input mono" value={s.runtime.connectivityProbeUrl} onChange={(e) => set((x) => { x.runtime.connectivityProbeUrl = e.target.value; })} />
          </Field>
        </div>
      </Card>
    </>
  );
}

function FilesSettings({ s, set }: P) {
  return (
    <>
      <Card title="File knowledge">
        <Toggle label="Read and index my documents" hint="Builds a private index on this PC so JARVIS can find, summarise and compare your files. Off by default."
          checked={s.files.indexEnabled} onChange={(v) => set((x) => { x.files.indexEnabled = v; })} />
        <Field label="Folders to index" hint="One per line. Empty = Documents, Desktop and Downloads. Protected places (credential stores, system folders) are always skipped.">
          <textarea className="input mono" rows={3} value={s.files.indexRoots.join("\n")} onChange={(e) => set((x) => { x.files.indexRoots = e.target.value.split("\n").map((l) => l.trim()).filter(Boolean); })} />
        </Field>
        <Field label="Largest file to read (MB)" hint="Bigger files are indexed by name only.">
          <input className="input" type="number" min={1} max={500} value={s.files.indexMaxFileMb} onChange={(e) => set((x) => { x.files.indexMaxFileMb = Number(e.target.value); })} />
        </Field>
        <FilesIndexHint />
        <div className="row"><ClearIndexButton /></div>
      </Card>
    </>
  );
}

function WebSettings({ s, set }: P) {
  const b = useLoad(() => get<BrowserStatus>("/browser"));
  useEvents(["browser."], () => void b.reload());
  return (
    <>
      <Card title="JARVIS's browser" actions={b.data?.running ? <Badge tone="good">{tr("open")}</Badge> : <Badge>{tr("closed")}</Badge>}>
        <p className="small muted">
          {tr("For things a page can't do without clicking — forms, sign-ins you've done yourself, bookings — JARVIS drives its own Edge/Chrome window with a separate profile. You see every step. Sending, buying, publishing, deleting, booking or submitting a form always asks you first, and anything suggested after reading a page needs your OK. JARVIS never types passwords or payment details.")}
        </p>
        <Toggle label="Let JARVIS use a browser" checked={s.web.browserEnabled} onChange={(v) => set((x) => { x.web.browserEnabled = v; })} />
        <Field label="Browser" hint={b.data?.browserPath ? tr("Found: {path}", { path: b.data.browserPath }) : tr("None found. Install Microsoft Edge or Chrome, or give the path.")}>
          <input className="input mono" dir="ltr" placeholder={tr("Automatic (Microsoft Edge, then Chrome)")} value={s.web.browserPath} onChange={(e) => set((x) => { x.web.browserPath = e.target.value; })} />
        </Field>
        <Toggle label="Run hidden" hint="Off = you see the browser window while JARVIS works (recommended)." checked={s.web.headless} onChange={(v) => set((x) => { x.web.headless = v; })} />
        <Toggle label="Allow pages on this computer or local network" hint="e.g. a dev server on localhost or your router. Off by default so web content can't reach local devices."
          checked={s.web.allowLocalPages} onChange={(v) => set((x) => { x.web.allowLocalPages = v; })} />
        {b.data?.running && (
          <div className="row small">
            <span className="muted grow mono ellipsis" dir="ltr">{b.data.url}</span>
            <button className="btn btn-sm" onClick={() => post("/browser/close", {}).then(() => b.reload())}>{tr("Close browser")}</button>
          </div>
        )}
      </Card>
    </>
  );
}

function AccountsSettings({ s, set }: P) {
  const st = useLoad(() => get<InboxStatus>("/inbox/status"));
  useEvents(["inbox."], () => void st.reload());
  const [preset, setPreset] = useState("gmail");
  const [address, setAddress] = useState("");
  const [name, setName] = useState("");
  const [password, setPassword] = useState("");
  const [cfg, setCfg] = useState<MailAccountConfig | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [ok, setOk] = useState<string | null>(null);
  const p = st.data?.presets.find((x) => x.id === preset);
  const config = cfg ?? p?.config;
  const connect = async () => {
    setBusy(true);
    setError(null);
    setOk(null);
    try {
      await post("/inbox/accounts", { preset, address, displayName: name || null, password, config: preset === "custom" || cfg ? config : null });
      setOk(tr("Connected {address}. Checking your inbox now.", { address }));
      setAddress(""); setPassword(""); setName(""); setCfg(null);
      await st.reload();
    } catch (e) { setError(e); } finally { setBusy(false); setPassword(""); }
  };
  const setC = (patch: Partial<MailAccountConfig>) => setCfg({ ...(config as MailAccountConfig), ...patch });
  return (
    <>
      <Card title="Email accounts">
        {st.data?.accounts.length ? (
          <ul className="list">
            {st.data.accounts.map((a) => (
              <li key={a.id}>
                <span className="grow" style={{ display: "flex", flexDirection: "column" }}>
                  <span className="row" style={{ gap: 8 }}><strong dir="ltr">{a.address}</strong> <Badge tone={a.status === "connected" ? "good" : a.status === "error" ? "bad" : "neutral"}>{a.status}</Badge></span>
                  <span className="small muted">{a.statusMessage ?? (a.lastSync ? tr("Checked {time}", { time: new Date(a.lastSync).toLocaleString(uiLocale()) }) : tr("Not checked yet"))} · {a.config.imapHost}</span>
                </span>
                <ConfirmButton className="btn btn-ghost btn-sm" prompt={tr("Disconnect {address}? Its synced mail and drafts are removed from JARVIS (nothing changes in your mailbox).", { address: a.address })}
                  onConfirm={async () => { await del(`/inbox/accounts/${a.id}`); void st.reload(); }}><Trash2 size={13} /> {tr("Disconnect")}</ConfirmButton>
              </li>
            ))}
          </ul>
        ) : <p className="small muted">{tr("No email connected yet.")}</p>}
      </Card>
      <Card title="Connect email">
        <div className="form-grid">
          <Field label="Provider" hint={p?.note}>
            <select className="input" value={preset} onChange={(e) => { setPreset(e.target.value); setCfg(null); }}>
              {st.data?.presets.map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}
            </select>
          </Field>
          <Field label="Email address"><input className="input" dir="ltr" type="email" autoComplete="off" value={address} onChange={(e) => setAddress(e.target.value)} /></Field>
          <Field label="Your name (for sent mail)"><input className="input" dir="auto" value={name} onChange={(e) => setName(e.target.value)} /></Field>
          <Field label="Password or app password" hint="Stored encrypted on this PC only; never shown again.">
            <input className="input" type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} />
          </Field>
        </div>
        {config && (
          <details open={preset === "custom"}>
            <summary className="small muted">{tr("Server settings")}</summary>
            <div className="form-grid" style={{ marginTop: 8 }}>
              <Field label="IMAP server"><input className="input mono" dir="ltr" value={config.imapHost} onChange={(e) => setC({ imapHost: e.target.value })} /></Field>
              <Field label="IMAP port"><input className="input" type="number" value={config.imapPort} onChange={(e) => setC({ imapPort: Number(e.target.value) })} /></Field>
              <Field label="IMAP security"><select className="input" value={config.imapSecurity} onChange={(e) => setC({ imapSecurity: e.target.value })}><option value="ssl">SSL/TLS</option><option value="starttls">STARTTLS</option><option value="none">{tr("None")}</option></select></Field>
              <Field label="SMTP server"><input className="input mono" dir="ltr" value={config.smtpHost} onChange={(e) => setC({ smtpHost: e.target.value })} /></Field>
              <Field label="SMTP port"><input className="input" type="number" value={config.smtpPort} onChange={(e) => setC({ smtpPort: Number(e.target.value) })} /></Field>
              <Field label="SMTP security"><select className="input" value={config.smtpSecurity} onChange={(e) => setC({ smtpSecurity: e.target.value })}><option value="ssl">SSL/TLS</option><option value="starttls">STARTTLS</option><option value="none">{tr("None")}</option></select></Field>
              <Field label="Username" hint="Usually the email address."><input className="input mono" dir="ltr" value={config.username} onChange={(e) => setC({ username: e.target.value })} /></Field>
            </div>
          </details>
        )}
        <div className="row"><button className="btn btn-primary btn-sm" disabled={busy || !address.includes("@") || !password} onClick={connect}>{busy ? tr("Testing the connection…") : tr("Connect")}</button></div>
        {ok && <p className="small">{ok}</p>}
        <ErrorNote error={error} />
      </Card>
      <Card title="Inbox">
        <div className="form-grid">
          <Field label="Check every (minutes)"><input className="input" type="number" min={1} value={s.inbox.syncMinutes} onChange={(e) => set((x) => { x.inbox.syncMinutes = Number(e.target.value); })} /></Field>
          <Field label="First sync goes back (days)"><input className="input" type="number" min={1} value={s.inbox.initialDays} onChange={(e) => set((x) => { x.inbox.initialDays = Number(e.target.value); })} /></Field>
        </div>
        <Toggle label="Tell me about urgent mail" hint="Through the notification centre, so meetings and quiet hours still hold it." checked={s.inbox.notifyUrgent} onChange={(v) => set((x) => { x.inbox.notifyUrgent = v; })} />
        <Field label="VIP senders" hint="One address or domain per line; their mail is always at least important.">
          <textarea className="input mono" rows={3} dir="ltr" value={s.inbox.vipSenders.join("\n")} onChange={(e) => set((x) => { x.inbox.vipSenders = e.target.value.split("\n").map((l) => l.trim()).filter(Boolean); })} />
        </Field>
      </Card>
      <CalendarSubscriptions />
      <Card title="Meeting reminders">
        <div className="form-grid">
          <Field label="Remind me before meetings (minutes)" hint="0 turns reminders off."><input className="input" type="number" min={0} value={s.calendar.reminderMinutes} onChange={(e) => set((x) => { x.calendar.reminderMinutes = Number(e.target.value); })} /></Field>
          <Field label="Refresh subscribed calendars every (minutes)"><input className="input" type="number" min={5} value={s.calendar.syncMinutes} onChange={(e) => set((x) => { x.calendar.syncMinutes = Number(e.target.value); })} /></Field>
        </div>
      </Card>
      {st.data && <ConnectorTable s={st.data} />}
    </>
  );
}
