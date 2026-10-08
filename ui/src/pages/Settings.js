import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useEffect, useState } from "react";
import { Bell, Brain, Check, Cpu, Download, Eye, FolderSearch, Keyboard, KeyRound, Mic, Palette, Plug, RefreshCw, Save, Shield, SlidersHorizontal, Trash2, Undo2, } from "lucide-react";
import { del, get, post, put } from "../api";
import { useStatus } from "../App";
import { useEvents } from "../events";
import { Badge, Card, ConfirmButton, ErrorNote, Field, PageHead, RiskBadge, Segmented, Toggle, fmtBytes, useLoad } from "../components/ui";
import { applyAppearance, settingsStore } from "../lib/settings";
import { playCue } from "../lib/sounds";
import { tr } from "../lib/i18n";
import { ClearIndexButton, FilesIndexHint } from "./Files";
const SECTIONS = [
    { id: "general", label: "General", icon: SlidersHorizontal },
    { id: "voice", label: "Voice", icon: Mic },
    { id: "ai", label: "AI", icon: Cpu },
    { id: "memory", label: "Memory", icon: Brain },
    { id: "files", label: "Files", icon: FolderSearch },
    { id: "security", label: "Security", icon: Shield },
    { id: "notifications", label: "Notifications", icon: Bell },
    { id: "appearance", label: "Appearance", icon: Palette },
    { id: "shortcuts", label: "Shortcuts", icon: Keyboard },
    { id: "plugins", label: "Tools & plugins", icon: Plug },
    { id: "privacy", label: "Privacy", icon: Eye },
    { id: "system", label: "System", icon: Cpu },
];
function sectionFromHash() {
    const sub = location.hash.replace(/^#\/?settings\/?/, "").split(/[?&/]/)[0];
    return (SECTIONS.find((s) => s.id === sub)?.id ?? "general");
}
export function SettingsPage() {
    const [section, setSection] = useState(sectionFromHash);
    const loaded = useLoad(() => get("/settings"));
    const [draft, setDraft] = useState(null);
    const [saved, setSaved] = useState(false);
    const [error, setError] = useState(null);
    const { refresh } = useStatus();
    useEffect(() => {
        const onHash = () => setSection(sectionFromHash());
        window.addEventListener("hashchange", onHash);
        return () => window.removeEventListener("hashchange", onHash);
    }, []);
    useEffect(() => {
        if (loaded.data)
            setDraft(structuredClone(loaded.data));
    }, [loaded.data]);
    // Appearance previews live; leaving without saving restores the saved look.
    useEffect(() => {
        if (draft)
            applyAppearance(draft.appearance);
    }, [draft?.appearance]); // eslint-disable-line react-hooks/exhaustive-deps
    useEffect(() => () => applyAppearance(settingsStore.getSnapshot()?.appearance), []);
    if (!draft)
        return _jsx(ErrorNote, { error: loaded.error });
    const dirty = JSON.stringify(draft) !== JSON.stringify(loaded.data);
    const set = (fn) => {
        setDraft((cur) => {
            const next = structuredClone(cur);
            fn(next);
            return next;
        });
        setSaved(false);
    };
    const save = async () => {
        try {
            const result = await put("/settings", draft);
            loaded.setData(result);
            setDraft(structuredClone(result));
            settingsStore.set(result);
            setSaved(true);
            setError(null);
            await refresh();
        }
        catch (e) {
            setError(e);
        }
    };
    const go = (id) => { location.hash = `#/settings/${id}`; };
    const p = { s: draft, set };
    return (_jsxs("div", { className: "stack", children: [_jsx(PageHead, { title: tr("Settings"), sub: tr("Every switch here changes how JARVIS behaves right away once saved."), actions: saved && !dirty ? _jsxs("span", { className: "row muted small", children: [_jsx(Check, { size: 14 }), " ", tr("Saved")] }) : null }), _jsx(ErrorNote, { error: error }), _jsxs("div", { className: "settings", children: [_jsx("nav", { className: "settings-nav", "aria-label": "Settings sections", children: SECTIONS.map((x) => (_jsxs("button", { className: section === x.id ? "on" : "", onClick: () => go(x.id), "aria-current": section === x.id ? "page" : undefined, children: [_jsx(x.icon, { size: 15 }), " ", tr(x.label)] }, x.id))) }), _jsxs("div", { className: "stack", children: [section === "general" && _jsx(General, { ...p }), section === "voice" && _jsx(Voice, { ...p }), section === "ai" && _jsx(Ai, { ...p }), section === "memory" && _jsx(MemorySettings, { ...p }), section === "files" && _jsx(FilesSettings, { ...p }), section === "security" && _jsx(Security, { ...p }), section === "notifications" && _jsx(Notifications, { ...p }), section === "appearance" && _jsx(AppearanceSection, { ...p }), section === "shortcuts" && _jsx(Shortcuts, { ...p }), section === "plugins" && _jsx(Plugins, {}), section === "privacy" && _jsx(Privacy, { ...p }), section === "system" && _jsx(SystemSection, { ...p }), dirty && (_jsxs("div", { className: "savebar", children: [_jsx("span", { className: "small muted grow", children: tr("Unsaved changes") }), _jsxs("button", { className: "btn btn-ghost", onClick: () => loaded.data && setDraft(structuredClone(loaded.data)), children: [_jsx(Undo2, { size: 15 }), " ", tr("Discard")] }), _jsxs("button", { className: "btn btn-primary", onClick: save, children: [_jsx(Save, { size: 15 }), " ", tr("Save changes")] })] }))] })] })] }));
}
function General({ s, set }) {
    return (_jsxs(Card, { title: "General", children: [_jsxs("div", { className: "form-grid", children: [_jsx(Field, { label: "Your name", hint: "Optional. Used in greetings.", children: _jsx("input", { className: "input", dir: "auto", value: s.general.userName, onChange: (e) => set((x) => { x.general.userName = e.target.value; }) }) }), _jsx(Field, { label: "How JARVIS addresses you (English)", hint: "e.g. Sir, Boss, your name \u2014 or leave empty.", children: _jsx("input", { className: "input", value: s.general.honorific, onChange: (e) => set((x) => { x.general.honorific = e.target.value; }) }) }), _jsx(Field, { label: "How JARVIS addresses you (Arabic)", hint: "\u0645\u062B\u0644\u0627\u064B: \u064A\u0627 \u0641\u0646\u062F\u0645\u060C \u064A\u0627 \u0628\u0627\u0634\u0627\u060C \u064A\u0627 \u0647\u0646\u062F\u0633\u0629", children: _jsx("input", { className: "input", dir: "rtl", value: s.general.honorificAr, onChange: (e) => set((x) => { x.general.honorificAr = e.target.value; }) }) }), _jsx(Field, { label: "Reply language", hint: "Auto replies in the language you use.", children: _jsxs("select", { className: "input", value: s.general.language, onChange: (e) => set((x) => { x.general.language = e.target.value; }), children: [_jsx("option", { value: "auto", children: "Automatic (match me)" }), _jsx("option", { value: "en", children: "Always English" }), _jsx("option", { value: "ar", children: "Always Egyptian Arabic" })] }) }), _jsx(Field, { label: "Conversation window (minutes)", hint: "After this much silence, a new conversation context starts.", children: _jsx("input", { className: "input", type: "number", min: 1, value: s.general.conversationTimeoutMinutes, onChange: (e) => set((x) => { x.general.conversationTimeoutMinutes = Number(e.target.value); }) }) })] }), _jsx(Toggle, { label: "Start JARVIS when I sign in to Windows", checked: s.general.startWithWindows, onChange: (v) => set((x) => { x.general.startWithWindows = v; }) }), _jsx(Toggle, { label: "Show the desktop interface (orb and tray) when JARVIS starts", checked: s.general.launchDesktopOnStart, onChange: (v) => set((x) => { x.general.launchDesktopOnStart = v; }) }), _jsx(Toggle, { label: "Show the floating orb", checked: s.general.showOrb, onChange: (v) => set((x) => { x.general.showOrb = v; }) })] }));
}
function Ai({ s, set }) {
    const secrets = useLoad(() => get("/secrets"));
    const [statuses, setStatuses] = useState({});
    const [keys, setKeys] = useState({});
    const check = async (id) => {
        const st = await post(`/ai/providers/${id}/check`);
        setStatuses((c) => ({ ...c, [id]: st }));
    };
    useEffect(() => {
        s.ai.providers.filter((p) => p.enabled).forEach((p) => void check(p.id).catch(() => { }));
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);
    const allModels = (id) => statuses[id]?.models ?? [];
    return (_jsxs(_Fragment, { children: [_jsx(Models, { s: s, set: set }), _jsxs(Card, { title: "Behaviour", children: [_jsx(Toggle, { label: "Allow cloud AI", hint: "Off = nothing you say leaves this computer. On = providers marked 'cloud' may be used when online, with your own API key.", checked: s.ai.allowCloud, onChange: (v) => set((x) => { x.ai.allowCloud = v; }) }), _jsx(Toggle, { label: "Stream replies as they're written", hint: "Shows text word by word. Off waits for the complete answer.", checked: s.ai.streamResponses, onChange: (v) => set((x) => { x.ai.streamResponses = v; }) }), _jsxs("div", { className: "form-grid", children: [_jsx(Field, { label: "Local context window", hint: "How much conversation local models see. Bigger remembers more but is slower and uses more memory.", children: _jsx("select", { className: "input", value: s.ai.localContextTokens, onChange: (e) => set((x) => { x.ai.localContextTokens = Number(e.target.value); }), children: [4096, 8192, 16384, 32768].map((n) => _jsxs("option", { value: n, children: [n / 1024, "K tokens", n === 8192 ? " (recommended)" : ""] }, n)) }) }), _jsx(Field, { label: "Max tool steps per request", hint: "JARVIS stops and checks in after this many actions.", children: _jsx("input", { className: "input", type: "number", min: 1, max: 30, value: s.ai.maxAgentSteps, onChange: (e) => set((x) => { x.ai.maxAgentSteps = Number(e.target.value); }) }) }), _jsx(Field, { label: "Model timeout (seconds)", hint: "A model that takes longer is treated as failed and the next one is tried.", children: _jsx("input", { className: "input", type: "number", min: 10, value: s.ai.requestTimeoutSeconds, onChange: (e) => set((x) => { x.ai.requestTimeoutSeconds = Number(e.target.value); }) }) })] })] }), _jsx(Card, { title: "Providers", children: s.ai.providers.map((p, i) => {
                    const st = statuses[p.id];
                    return (_jsxs("div", { className: "provider", children: [_jsxs("div", { className: "row between wrap", children: [_jsxs("div", { className: "row wrap", children: [_jsx("strong", { children: p.name }), _jsx(Badge, { tone: p.isLocal ? "good" : "warn", children: p.isLocal ? "local" : "cloud" }), st && _jsx(Badge, { tone: st.available ? "good" : "bad", children: st.available ? `${st.models.length} models` : "unavailable" })] }), _jsxs("div", { className: "row", children: [_jsxs("button", { className: "btn btn-ghost btn-sm", onClick: () => check(p.id), disabled: !p.enabled, title: "Test connection", children: [_jsx(RefreshCw, { size: 13 }), " Test"] }), _jsx(Toggle, { label: "Enabled", checked: p.enabled, onChange: (v) => set((x) => { x.ai.providers[i].enabled = v; }) })] })] }), st && !st.available && _jsx("div", { className: "small muted", children: st.message }), p.kind !== "anthropic" && (_jsx(Field, { label: "Server URL", children: _jsx("input", { className: "input mono", value: p.baseUrl, onChange: (e) => set((x) => { x.ai.providers[i].baseUrl = e.target.value; }) }) })), p.apiKeySecret && (_jsxs("div", { className: "row wrap", children: [_jsx(KeyRound, { size: 16 }), secrets.data?.names.includes(p.apiKeySecret) ? (_jsxs(_Fragment, { children: [_jsxs(Badge, { tone: "good", children: ["API key stored (", secrets.data.protection, ")"] }), _jsxs("button", { className: "btn btn-ghost btn-sm", onClick: async () => { await del(`/secrets/${p.apiKeySecret}`); void secrets.reload(); }, children: [_jsx(Trash2, { size: 13 }), " Remove"] })] })) : (_jsxs(_Fragment, { children: [_jsx("input", { className: "input grow mono", type: "password", placeholder: "Paste API key (stored encrypted, never shown again)", value: keys[p.id] ?? "", onChange: (e) => setKeys((k) => ({ ...k, [p.id]: e.target.value })) }), _jsx("button", { className: "btn btn-sm", disabled: !keys[p.id], onClick: async () => { await put(`/secrets/${p.apiKeySecret}`, { value: keys[p.id] }); setKeys((k) => ({ ...k, [p.id]: "" })); void secrets.reload(); }, children: "Save key" })] }))] }))] }, p.id));
                }) }), _jsxs(Card, { title: "Which model does what", children: [_jsx("p", { className: "small muted", children: "Direct commands never use AI. Other requests are classified (general / reasoning / coding / vision) and go to the first available choice, then fall back down the list. An empty model means \u201Cthe best installed one\u201D (tool-capable, ~7-8B preferred)." }), Object.entries(s.ai.roles).map(([role, bindings]) => (_jsxs("div", { className: "role", children: [_jsx("div", { className: "role-name", children: role }), _jsxs("div", { className: "stack-sm grow", children: [bindings.map((b, j) => (_jsxs("div", { className: "row wrap", children: [_jsxs("span", { className: "meta", children: [j + 1, "."] }), _jsx("select", { className: "input", value: b.provider, onChange: (e) => set((x) => { x.ai.roles[role][j].provider = e.target.value; }), children: s.ai.providers.map((p) => _jsx("option", { value: p.id, children: p.name }, p.id)) }), _jsx("input", { className: "input mono grow", list: `models-${role}-${j}`, placeholder: "(best installed)", value: b.model, onChange: (e) => set((x) => { x.ai.roles[role][j].model = e.target.value; }) }), _jsx("datalist", { id: `models-${role}-${j}`, children: allModels(b.provider).map((m) => _jsx("option", { value: m }, m)) }), _jsx("button", { className: "btn btn-ghost btn-icon", onClick: () => set((x) => { x.ai.roles[role].splice(j, 1); }), "aria-label": "Remove", children: _jsx(Trash2, { size: 14 }) })] }, j))), _jsx("button", { className: "btn btn-ghost btn-sm", style: { alignSelf: "flex-start" }, onClick: () => set((x) => { x.ai.roles[role].push({ provider: s.ai.providers[0]?.id ?? "", model: "" }); }), children: "+ Add fallback" })] })] }, role)))] })] }));
}
/** Installed local models with their real capabilities, and one-click downloads of recommended ones. */
function Models({ s, set }) {
    const models = useLoad(() => get("/ai/models"));
    const [pulls, setPulls] = useState({});
    const [custom, setCustom] = useState("");
    const [error, setError] = useState(null);
    useEffect(() => {
        if (models.data)
            setPulls(Object.fromEntries(models.data.pulls.map((p) => [p.model, p])));
    }, [models.data]);
    useEvents(["ai.model"], (e) => {
        const st = e.data;
        setPulls((p) => ({ ...p, [st.model]: st }));
        if (st.done)
            void models.reload();
    });
    const pull = async (provider, model) => {
        try {
            setError(null);
            await post(`/ai/providers/${provider}/pull`, { model });
        }
        catch (e) {
            setError(e);
        }
    };
    const general = s.ai.roles.general ?? [];
    const pinned = (provider, model) => general.some((b) => b.provider === provider && b.model === model);
    const pin = (provider, model) => set((x) => {
        const list = x.ai.roles.general ?? (x.ai.roles.general = []);
        const i = list.findIndex((b) => b.provider === provider);
        if (i >= 0)
            list[i].model = model;
        else
            list.unshift({ provider, model });
    });
    const pullable = models.data?.providers.find((p) => p.canPull && p.reachable);
    const installed = new Set(models.data?.providers.flatMap((p) => p.models.map((m) => m.name)) ?? []);
    return (_jsxs(Card, { title: "Local models", actions: _jsxs("button", { className: "btn btn-ghost btn-sm", onClick: () => models.reload(), children: [_jsx(RefreshCw, { size: 13 }), " Refresh"] }), children: [_jsx(ErrorNote, { error: error ?? models.error }), models.data?.providers.map((p) => (_jsxs("div", { className: "stack-sm", children: [_jsxs("div", { className: "row", children: [_jsx("strong", { className: "small", children: p.name }), !p.reachable && _jsx(Badge, { tone: "bad", children: "not reachable" })] }), !p.reachable && p.provider === "ollama" && (_jsxs("div", { className: "note", children: ["Ollama isn't running. Install it free from ", _jsx("code", { children: "ollama.com" }), "; JARVIS will detect it automatically."] })), p.models.map((m) => (_jsxs("div", { className: "model-row", children: [_jsx("span", { className: "mono grow", children: m.name }), m.parameterSize && _jsx("span", { className: "meta", children: m.parameterSize }), m.sizeBytes ? _jsx("span", { className: "meta", children: fmtBytes(m.sizeBytes) }) : null, m.contextLength ? _jsxs("span", { className: "meta", children: [Math.round(m.contextLength / 1024), "K ctx"] }) : null, m.capabilities.map((c) => _jsx(Badge, { tone: c === "tools" ? "good" : c === "vision" ? "info" : c === "embedding" ? "accent" : "neutral", children: c }, c)), !m.capabilitiesReported && _jsx(Badge, { tone: "neutral", title: "This Ollama version doesn't report capabilities; guessed from the name", children: "guessed" }), !m.capabilities.includes("embedding") && (pinned(p.provider, m.name)
                                ? _jsx(Badge, { tone: "good", children: "default" })
                                : _jsx("button", { className: "btn btn-ghost btn-sm", onClick: () => pin(p.provider, m.name), title: "Use for general conversation", children: "Use" }))] }, m.name))), p.reachable && p.models.length === 0 && _jsx("div", { className: "small muted", children: "No models installed yet \u2014 download one below." })] }, p.provider))), pullable && (_jsxs(_Fragment, { children: [_jsx("div", { className: "divider" }), _jsx("div", { className: "hud-label", children: "Recommended (free)" }), models.data?.recommended.map((r) => {
                        const st = pulls[r.name];
                        const has = installed.has(r.name) || installed.has(`${r.name}:latest`);
                        const running = st && !st.done;
                        return (_jsxs("div", { className: "model-row", children: [_jsxs("div", { className: "grow", children: [_jsxs("div", { children: [_jsx("span", { className: "mono", children: r.name }), " ", _jsxs("span", { className: "meta", children: [r.size, " \u00B7 ", r.purpose] })] }), _jsx("div", { className: "small muted", children: r.notes }), running && (_jsxs("div", { className: "stack-sm", style: { marginTop: 6 }, children: [_jsx("progress", { max: st.total ?? 1, value: st.completed ?? 0 }), _jsxs("span", { className: "meta", children: [st.status, st.total ? ` · ${fmtBytes(st.completed ?? 0)} / ${fmtBytes(st.total)}` : ""] })] })), st?.error && _jsx("div", { className: "error-note small", children: st.error })] }), has ? _jsx(Badge, { tone: "good", children: "installed" }) : (_jsxs("button", { className: "btn btn-sm", disabled: !!running, onClick: () => pull(pullable.provider, r.name), children: [_jsx(Download, { size: 13 }), " ", running ? "Downloading" : "Download"] }))] }, r.name));
                    }), _jsxs("form", { className: "row", onSubmit: (e) => { e.preventDefault(); if (custom.trim()) {
                            void pull(pullable.provider, custom.trim());
                            setCustom("");
                        } }, children: [_jsx("input", { className: "input mono grow", placeholder: "Any Ollama model, e.g. llama3.1:8b", value: custom, onChange: (e) => setCustom(e.target.value) }), _jsxs("button", { className: "btn btn-sm", disabled: !custom.trim(), type: "submit", children: [_jsx(Download, { size: 13 }), " Pull"] })] }), Object.values(pulls).filter((x) => !models.data?.recommended.some((r) => r.name === x.model) && !x.done).map((x) => (_jsxs("div", { className: "stack-sm", children: [_jsxs("span", { className: "meta", children: [x.model, ": ", x.status] }), _jsx("progress", { max: x.total ?? 1, value: x.completed ?? 0 })] }, x.model)))] }))] }));
}
function Voice({ s, set }) {
    const models = useLoad(() => get("/voice/models"));
    const voices = useLoad(() => get("/voice/voices"));
    const [progress, setProgress] = useState({});
    useEvents(["voice.model"], (e) => {
        const d = e.data;
        setProgress((p) => ({ ...p, [d.model]: { bytes: d.bytes ?? 0, total: d.total, error: d.error } }));
        if (d.done)
            void models.reload();
    });
    const enVoices = voices.data?.voices.filter((v) => v.language.startsWith("en")) ?? [];
    const arVoices = voices.data?.voices.filter((v) => v.language.startsWith("ar")) ?? [];
    return (_jsxs(_Fragment, { children: [_jsxs(Card, { title: "Speech recognition (local Whisper)", children: [_jsx("p", { className: "small muted", children: "Runs entirely on this computer. Models download once from the whisper.cpp project. For Egyptian Arabic, \u201Csmall\u201D is noticeably better than \u201Cbase\u201D." }), _jsx("ul", { className: "list list-rows", children: models.data?.models.map((m) => {
                            const pr = progress[m.name];
                            return (_jsxs("li", { children: [_jsx("input", { type: "radio", name: "stt", "aria-label": `Use ${m.name}`, checked: s.voice.sttModel === m.name, disabled: !m.installed, onChange: () => set((x) => { x.voice.sttModel = m.name; }) }), _jsxs("div", { className: "grow", children: [_jsx("strong", { children: m.name }), " ", _jsxs("span", { className: "meta", children: ["~", Math.round(m.approxBytes / 1e6), " MB"] }), _jsx("div", { className: "small muted", children: m.description }), pr && !m.installed && (pr.error ? _jsx("div", { className: "error-note", children: pr.error }) : _jsx("progress", { max: pr.total ?? m.approxBytes, value: pr.bytes }))] }), m.installed ? _jsx(Badge, { tone: "good", children: "installed" }) : (_jsxs("button", { className: "btn btn-sm", disabled: models.data?.downloading, onClick: () => post(`/voice/models/${m.name}/download`).then(() => models.reload()), children: [_jsx(Download, { size: 13 }), " Download"] }))] }, m.name));
                        }) })] }), _jsxs(Card, { title: "Wake word and conversation", children: [_jsx(Toggle, { label: "Listen for \u201CJarvis\u201D (wake word)", hint: "Keeps the microphone open while JARVIS runs. The orb and the top bar show when it's listening. Off by default.", checked: s.voice.wakeWordEnabled, onChange: (v) => set((x) => { x.voice.wakeWordEnabled = v; }) }), _jsxs("div", { className: "form-grid", children: [_jsx(Field, { label: "Wake words", hint: "Comma separated. Arabic spellings are recognised too.", children: _jsx("input", { className: "input", dir: "auto", value: s.voice.wakeWords.join(", "), onChange: (e) => set((x) => { x.voice.wakeWords = e.target.value.split(",").map((w) => w.trim()).filter(Boolean); }) }) }), _jsx(Field, { label: "Follow-up window (seconds)", hint: "After answering, keep listening this long without needing the wake word.", children: _jsx("input", { className: "input", type: "number", min: 0, max: 60, value: s.voice.followUpSeconds, onChange: (e) => set((x) => { x.voice.followUpSeconds = Number(e.target.value); }) }) }), _jsx(Field, { label: "Speech sensitivity", hint: "Higher = needs louder speech (fewer false triggers).", children: _jsx("input", { className: "input", type: "number", step: 0.5, min: 1.5, max: 20, value: s.voice.vadSensitivity, onChange: (e) => set((x) => { x.voice.vadSensitivity = Number(e.target.value); }) }) }), _jsx(Field, { label: "Max command length (seconds)", children: _jsx("input", { className: "input", type: "number", min: 3, max: 60, value: s.voice.maxUtteranceSeconds, onChange: (e) => set((x) => { x.voice.maxUtteranceSeconds = Number(e.target.value); }) }) })] })] }), _jsxs(Card, { title: "Spoken replies", children: [_jsx(Toggle, { label: "Speak replies", checked: s.voice.ttsEnabled, onChange: (v) => set((x) => { x.voice.ttsEnabled = v; }) }), _jsx(Toggle, { label: "Only speak when I used my voice", checked: s.voice.speakOnlyForVoiceInput, onChange: (v) => set((x) => { x.voice.speakOnlyForVoiceInput = v; }) }), !voices.data?.available && _jsx("p", { className: "small muted", children: "No speech voices available on this system." }), _jsxs("div", { className: "form-grid", children: [_jsx(Field, { label: "English voice", children: _jsxs("select", { className: "input", value: s.voice.voiceEn, onChange: (e) => set((x) => { x.voice.voiceEn = e.target.value; }), children: [_jsx("option", { value: "", children: "Automatic" }), enVoices.map((v) => _jsxs("option", { value: v.id, children: [v.name, " (", v.language, ")"] }, v.id))] }) }), _jsx(Field, { label: "Arabic voice", hint: arVoices.length ? undefined : "No Arabic voice installed. Windows Settings → Time & language → Speech → Add voices (Arabic – Egypt).", children: _jsxs("select", { className: "input", value: s.voice.voiceAr, onChange: (e) => set((x) => { x.voice.voiceAr = e.target.value; }), children: [_jsx("option", { value: "", children: "Automatic" }), arVoices.map((v) => _jsxs("option", { value: v.id, children: [v.name, " (", v.language, ")"] }, v.id))] }) }), _jsx(Field, { label: "Speaking rate", children: _jsx("input", { className: "input", type: "number", step: 0.1, min: 0.5, max: 2, value: s.voice.rate, onChange: (e) => set((x) => { x.voice.rate = Number(e.target.value); }) }) })] }), _jsxs("div", { className: "row wrap", children: [_jsx("button", { className: "btn btn-sm", onClick: () => post("/voice/speak", { text: "Good evening, Sir. All systems are operational." }), children: "Test English voice" }), _jsx("button", { className: "btn btn-sm", onClick: () => post("/voice/speak", { text: "مساء الخير يا فندم، كل حاجة شغالة تمام.", lang: "ar" }), children: "\u062C\u0631\u0628 \u0627\u0644\u0635\u0648\u062A \u0627\u0644\u0639\u0631\u0628\u064A" })] })] })] }));
}
const MEMORY_KINDS = [
    ["fact", "Facts you tell me"],
    ["preference", "Your preferences"],
    ["person", "People"],
    ["project", "Projects"],
    ["context", "Temporary context"],
    ["pattern", "Learned patterns"],
];
function MemorySettings({ s, set }) {
    return (_jsxs(Card, { title: "Memory", children: [_jsx(Toggle, { label: "Long-term memory", hint: "When off, JARVIS won't store or use memories.", checked: s.memory.enabled, onChange: (v) => set((x) => { x.memory.enabled = v; }) }), _jsx(Toggle, { label: "Keep conversation history", checked: s.memory.storeConversations, onChange: (v) => set((x) => { x.memory.storeConversations = v; }) }), _jsx(Toggle, { label: "Learn from my activity", hint: "Off by default. JARVIS reviews its own activity log every few hours for routines and preferences, and proposes them as unconfirmed items you confirm or reject in Memory \u2192 To review. Nothing becomes a fact without you.", checked: s.memory.learnPatterns, onChange: (v) => set((x) => { x.memory.learnPatterns = v; }) }), _jsx(Field, { label: "Delete conversations older than (days)", hint: "0 keeps them forever.", children: _jsx("input", { className: "input", type: "number", min: 0, value: s.memory.conversationRetentionDays, onChange: (e) => set((x) => { x.memory.conversationRetentionDays = Number(e.target.value); }) }) }), _jsx(Field, { label: "Semantic search model", hint: "A local embedding model (Ollama) lets JARVIS find memories by meaning, in English and Arabic. bge-m3 is recommended; empty turns semantic search off.", children: _jsx("input", { className: "input mono", placeholder: "(off)", value: s.ai.embeddingModel, onChange: (e) => set((x) => { x.ai.embeddingModel = e.target.value.trim(); }) }) }), _jsxs("div", { className: "field", children: [_jsx("span", { className: "field-label", children: "JARVIS may remember" }), MEMORY_KINDS.map(([k, label]) => (_jsxs("label", { className: "row small", children: [_jsx("input", { type: "checkbox", checked: s.memory.allowedKinds.includes(k), onChange: (e) => set((x) => { x.memory.allowedKinds = e.target.checked ? [...x.memory.allowedKinds, k] : x.memory.allowedKinds.filter((y) => y !== k); }) }), label] }, k)))] }), _jsx("div", { className: "row", children: _jsxs(ConfirmButton, { prompt: "Delete all conversation history?", onConfirm: () => void del("/conversations"), children: [_jsx(Trash2, { size: 14 }), " Delete conversation history"] }) })] }));
}
function Notifications({ s, set }) {
    const cues = [
        ["wake", "Wake", "When JARVIS starts listening"],
        ["accepted", "Accepted", "When a request is received"],
        ["processing", "Processing", "A soft tick for each action (off by default)"],
        ["completed", "Completed", "When a request finishes"],
        ["warning", "Warning", "When an approval is needed"],
        ["error", "Error", "When something fails"],
        ["notification", "Notification", "When a notification is delivered"],
    ];
    return (_jsxs(_Fragment, { children: [_jsxs(Card, { title: "Notifications", children: [_jsx(Toggle, { label: "Windows notifications", checked: s.notifications.toastsEnabled, onChange: (v) => set((x) => { x.notifications.toastsEnabled = v; }) }), _jsx(Toggle, { label: "Speak important notifications", checked: s.notifications.speakImportant, onChange: (v) => set((x) => { x.notifications.speakImportant = v; }) }), _jsx(Toggle, { label: "Hold non-urgent notifications during meetings", checked: s.notifications.holdDuringMeetings, onChange: (v) => set((x) => { x.notifications.holdDuringMeetings = v; }) }), _jsx(Toggle, { label: "Hold non-urgent notifications during fullscreen apps and presentations", checked: s.notifications.holdDuringFullscreen, onChange: (v) => set((x) => { x.notifications.holdDuringFullscreen = v; }) }), _jsxs("div", { className: "form-grid", children: [_jsx(Field, { label: "Quiet hours from", hint: "24h, e.g. 23:00. Critical alerts still come through.", children: _jsx("input", { className: "input", type: "time", value: s.notifications.quietHoursStart, onChange: (e) => set((x) => { x.notifications.quietHoursStart = e.target.value; }) }) }), _jsx(Field, { label: "Quiet hours until", children: _jsx("input", { className: "input", type: "time", value: s.notifications.quietHoursEnd, onChange: (e) => set((x) => { x.notifications.quietHoursEnd = e.target.value; }) }) })] })] }), _jsxs(Card, { title: "Interface sounds", children: [_jsx(Toggle, { label: "Play interface sounds", hint: "Short synthesized cues from the dashboard and command console while they're open. Off silences everything.", checked: s.sounds.enabled, onChange: (v) => set((x) => { x.sounds.enabled = v; }) }), _jsx(Field, { label: `Volume ${Math.round(s.sounds.volume * 100)}%`, children: _jsx("input", { type: "range", min: 0, max: 1, step: 0.05, value: s.sounds.volume, disabled: !s.sounds.enabled, onChange: (e) => set((x) => { x.sounds.volume = Number(e.target.value); }) }) }), cues.map(([k, label, hint]) => (_jsxs("div", { className: "row", children: [_jsx("div", { className: "grow", children: _jsx(Toggle, { label: label, hint: hint, checked: s.sounds[k], onChange: (v) => set((x) => { x.sounds[k] = v; }) }) }), _jsx("button", { className: "btn btn-ghost btn-sm", disabled: !s.sounds.enabled, onClick: () => playCue(k, s.sounds.volume), children: "Play" })] }, k)))] })] }));
}
function AppearanceSection({ s, set }) {
    const a = s.appearance;
    const accents = [
        ["cyan", "Cyan", "#3fd0ff"], ["amber", "Amber", "#ffba52"], ["violet", "Violet", "#aa8cff"], ["green", "Green", "#46e8aa"],
    ];
    return (_jsxs(Card, { title: "Appearance", children: [_jsx("p", { className: "small muted", children: "Changes preview immediately; save to keep them (the desktop orb follows too)." }), _jsx(Row, { label: "Interface language", hint: "\u0627\u0644\u0639\u0631\u0628\u064A\u0629 switches the whole interface to right-to-left. Replies follow Settings \u2192 General \u2192 Reply language.", children: _jsx(Segmented, { label: "Interface language", value: a.language, onChange: (v) => set((x) => { x.appearance.language = v; }), options: [["en", "English"], ["ar", "العربية"]] }) }), _jsx(Row, { label: "Theme", hint: "Dark is the HUD; light suits bright rooms; auto follows Windows.", children: _jsx(Segmented, { label: "Theme", value: a.theme, onChange: (v) => set((x) => { x.appearance.theme = v; }), options: [["dark", "Dark"], ["light", "Light"], ["auto", "Auto"]] }) }), _jsx(Row, { label: "Energy colour", children: _jsx("div", { className: "swatches", role: "radiogroup", "aria-label": "Energy colour", children: accents.map(([v, label, hex]) => (_jsx("button", { type: "button", role: "radio", "aria-checked": a.accent === v, "aria-label": label, title: label, className: `swatch ${a.accent === v ? "on" : ""}`, style: { background: hex, color: hex }, onClick: () => set((x) => { x.appearance.accent = v; }) }, v))) }) }), _jsx(Row, { label: "Motion", hint: "Reduced keeps state changes but stops ambient motion; Off removes all animation.", children: _jsx(Segmented, { label: "Motion", value: a.motion, onChange: (v) => set((x) => { x.appearance.motion = v; }), options: [["full", "Full"], ["reduced", "Reduced"], ["off", "Off"]] }) }), _jsx(Row, { label: "Density", children: _jsx(Segmented, { label: "Density", value: a.density, onChange: (v) => set((x) => { x.appearance.density = v; }), options: [["comfortable", "Comfortable"], ["compact", "Compact"]] }) }), _jsx(Row, { label: `Text size ${Math.round(a.textScale * 100)}%`, children: _jsx("input", { type: "range", min: 0.85, max: 1.4, step: 0.05, value: a.textScale, onChange: (e) => set((x) => { x.appearance.textScale = Number(e.target.value); }) }) }), _jsx(Row, { label: `Desktop orb size ${a.orbSize}px`, children: _jsx("input", { type: "range", min: 48, max: 128, step: 4, value: a.orbSize, onChange: (e) => set((x) => { x.appearance.orbSize = Number(e.target.value); }) }) }), _jsx(Toggle, { label: "Holographic grid and scan lines", checked: a.hudEffects, onChange: (v) => set((x) => { x.appearance.hudEffects = v; }) }), _jsx(Toggle, { label: "Context panel on wide screens", hint: "The right-hand panel with what's happening now and next.", checked: a.contextPanel, onChange: (v) => set((x) => { x.appearance.contextPanel = v; }) })] }));
}
function Row({ label, hint, children }) {
    return (_jsxs("div", { className: "toggle-row", style: { cursor: "default" }, children: [_jsxs("span", { className: "toggle-text", children: [_jsx("span", { children: label }), hint && _jsx("small", { children: hint })] }), children] }));
}
function Shortcuts({ s, set }) {
    const items = [
        ["commandConsole", "Command console", "Opens the cinematic command console from anywhere."],
        ["pushToTalk", "Push to talk", "Starts listening for one command."],
        ["dashboard", "Open dashboard", "Optional."],
    ];
    return (_jsxs(Card, { title: "Keyboard shortcuts", children: [_jsx("p", { className: "small muted", children: "Global shortcuts work in every app. Click a box and press the new combination (it must include Ctrl, Alt, Shift or Win). If another app already owns a combination, the tray icon tells you." }), items.map(([k, label, hint]) => (_jsx(Row, { label: label, hint: hint, children: _jsxs("div", { className: "row", children: [_jsx(ShortcutInput, { value: s.shortcuts[k], onChange: (v) => set((x) => { x.shortcuts[k] = v; }) }), s.shortcuts[k] && _jsx("button", { className: "btn btn-ghost btn-icon", onClick: () => set((x) => { x.shortcuts[k] = ""; }), "aria-label": `Clear ${label}`, children: _jsx(Trash2, { size: 14 }) })] }) }, k))), _jsx(Row, { label: "Inside the dashboard", hint: "Ctrl+K or / opens the console; Esc closes it.", children: _jsx("span", { className: "kbd", children: "Ctrl K" }) })] }));
}
function ShortcutInput({ value, onChange }) {
    const [recording, setRecording] = useState(false);
    return (_jsx("input", { className: "input shortcut-input", readOnly: true, value: recording ? "Press keys…" : value || "None", onFocus: () => setRecording(true), onBlur: () => setRecording(false), onKeyDown: (e) => {
            e.preventDefault();
            if (e.key === "Escape") {
                e.target.blur();
                return;
            }
            const combo = comboFrom(e);
            if (combo) {
                onChange(combo);
                e.target.blur();
            }
        }, "aria-label": "Shortcut", style: { width: 190 } }));
}
function comboFrom(e) {
    const key = e.key;
    if (["Control", "Alt", "Shift", "Meta"].includes(key))
        return null;
    const mods = [e.ctrlKey && "Ctrl", e.altKey && "Alt", e.shiftKey && "Shift", e.metaKey && "Win"].filter(Boolean);
    if (mods.length === 0)
        return null;
    const name = key === " " ? "Space" : /^F\d{1,2}$/.test(key) ? key : key.length === 1 ? key.toUpperCase() : null;
    if (!name || !/^([A-Z0-9]|Space|F\d{1,2})$/.test(name))
        return null;
    return [...mods, name].join("+");
}
function Plugins() {
    const tools = useLoad(() => get("/tools"));
    return (_jsxs(Card, { title: "Tools & plugins", children: [_jsx("p", { className: "small muted", children: "Everything JARVIS can do is a tool with a declared risk. Safe tools run immediately; sensitive tools ask first unless you allow them; critical actions (deleting, power, destructive commands, sending) always ask, whatever you choose here. Third-party and generated plugins will appear here with the permissions they request \u2014 never with unrestricted access." }), _jsx("div", { className: "table-wrap", children: _jsxs("table", { className: "table", children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Tool" }), _jsx("th", { children: "What it does" }), _jsx("th", { children: "Risk" }), _jsx("th", { children: "Permission" })] }) }), _jsx("tbody", { children: tools.data?.map((t) => (_jsxs("tr", { children: [_jsxs("td", { className: "mono small nowrap", children: [t.name, " ", t.requiresInternet && _jsx(Badge, { tone: "info", children: "web" })] }), _jsx("td", { className: "small", children: t.description }), _jsx("td", { children: _jsx(RiskBadge, { risk: t.risk }) }), _jsx("td", { children: _jsxs("select", { className: "input input-sm", value: t.policy, onChange: async (e) => { await put(`/tools/${t.name}/policy`, { policy: e.target.value }); void tools.reload(); }, children: [_jsx("option", { value: "Default", children: "Default" }), _jsx("option", { value: "Allow", children: "Always allow" }), _jsx("option", { value: "Ask", children: "Always ask" }), _jsx("option", { value: "Block", children: "Block" })] }) })] }, t.name))) })] }) })] }));
}
function Security({ s, set }) {
    const { status, refresh } = useStatus();
    const [current, setCurrent] = useState("");
    const [next, setNext] = useState("");
    const [msg, setMsg] = useState(null);
    const [error, setError] = useState(null);
    const secrets = useLoad(() => get("/secrets"));
    const apply = async (newPin) => {
        try {
            await put("/auth/pin", { currentPin: current || null, newPin });
            setMsg(newPin ? "PIN saved." : "PIN removed.");
            setCurrent("");
            setNext("");
            setError(null);
            await refresh();
        }
        catch (e) {
            setError(e);
        }
    };
    return (_jsxs(_Fragment, { children: [_jsxs(Card, { title: "Approvals", children: [_jsx(Toggle, { label: "Run sensitive actions without asking", hint: "Sensitive = changing files, running programs, typing into apps. Critical actions always ask.", checked: s.permissions.autoApproveSensitive, onChange: (v) => set((x) => { x.permissions.autoApproveSensitive = v; }) }), _jsx(Field, { label: "Approval timeout (seconds)", hint: "If you don't answer, the action is not taken.", children: _jsx("input", { className: "input", type: "number", min: 15, value: s.permissions.approvalTimeoutSeconds, onChange: (e) => set((x) => { x.permissions.approvalTimeoutSeconds = Number(e.target.value); }) }) }), _jsx(Field, { label: "Folders JARVIS may read freely", hint: "One per line. Empty = your user folder. Writing anywhere still asks; system folders and credential files are always protected.", children: _jsx("textarea", { className: "input mono", rows: 3, value: s.files.allowedRoots.join("\n"), onChange: (e) => set((x) => { x.files.allowedRoots = e.target.value.split("\n").map((l) => l.trim()).filter(Boolean); }) }) })] }), _jsxs(Card, { title: "Dashboard PIN", children: [_jsx("p", { className: "small muted", children: "A PIN locks this dashboard and the local API after inactivity. JARVIS's API only accepts connections from this computer." }), _jsxs("div", { className: "form-grid", children: [status?.pinSet && (_jsx(Field, { label: "Current PIN", children: _jsx("input", { className: "input", type: "password", inputMode: "numeric", value: current, onChange: (e) => setCurrent(e.target.value) }) })), _jsx(Field, { label: status?.pinSet ? "New PIN" : "PIN", hint: "4\u201332 characters.", children: _jsx("input", { className: "input", type: "password", inputMode: "numeric", value: next, onChange: (e) => setNext(e.target.value) }) }), _jsx(Field, { label: "Lock after (minutes idle)", children: _jsx("input", { className: "input", type: "number", min: 1, value: s.security.unlockMinutes, onChange: (e) => set((x) => { x.security.unlockMinutes = Number(e.target.value); }) }) })] }), _jsxs("div", { className: "row", children: [_jsx("button", { className: "btn btn-primary btn-sm", disabled: next.length < 4, onClick: () => apply(next), children: status?.pinSet ? "Change PIN" : "Set PIN" }), status?.pinSet && _jsx("button", { className: "btn btn-sm", disabled: !current, onClick: () => apply(""), children: "Remove PIN" })] }), msg && _jsx("p", { className: "muted small", children: msg }), _jsx(ErrorNote, { error: error })] }), _jsxs(Card, { title: "Stored secrets", children: [_jsxs("p", { className: "small muted", children: ["API keys and connector tokens, encrypted with ", secrets.data?.protection ?? "…", ". Values are never shown or sent to the dashboard."] }), secrets.data?.names.length ? (_jsx("ul", { className: "list", children: secrets.data.names.map((n) => (_jsxs("li", { children: [_jsx("span", { className: "mono small", children: n }), _jsxs(ConfirmButton, { className: "btn btn-ghost btn-sm", prompt: `Delete the secret "${n}"?`, onConfirm: async () => { await del(`/secrets/${n}`); void secrets.reload(); }, children: [_jsx(Trash2, { size: 13 }), " Delete"] })] }, n))) })) : _jsx("p", { className: "small muted", children: "No secrets stored." })] })] }));
}
function Privacy({ s, set }) {
    const { status } = useStatus();
    return (_jsxs(_Fragment, { children: [_jsxs(Card, { title: "What JARVIS may see and hear", children: [_jsx(Toggle, { label: "Allow screen capture", hint: "Screenshots by command or by the AI. Off blocks every screen-capture tool.", checked: s.privacy.allowScreenCapture, onChange: (v) => set((x) => { x.privacy.allowScreenCapture = v; }) }), _jsx(Toggle, { label: "Wake word (always-on microphone)", hint: "Off by default. When on, the top bar shows \u201CMic on\u201D.", checked: s.voice.wakeWordEnabled, onChange: (v) => set((x) => { x.voice.wakeWordEnabled = v; }) }), _jsx("div", { className: "note", children: "JARVIS has no camera access in this build. Presence uses only Windows signals (active window, idle time, fullscreen, whether another app is using the microphone)." })] }), _jsxs(Card, { title: "What leaves this computer", children: [_jsx(Toggle, { label: "Allow cloud AI", hint: "Off = conversations never leave this PC.", checked: s.ai.allowCloud, onChange: (v) => set((x) => { x.ai.allowCloud = v; }) }), _jsx("p", { className: "small muted", children: "Web search and page reading go to the sites involved only when you (or the AI, through an audited tool) ask. Nothing is sent anywhere else." })] }), _jsxs(Card, { title: "What JARVIS keeps", children: [_jsx(Toggle, { label: "Long-term memory", checked: s.memory.enabled, onChange: (v) => set((x) => { x.memory.enabled = v; }) }), _jsx(Toggle, { label: "Conversation history", checked: s.memory.storeConversations, onChange: (v) => set((x) => { x.memory.storeConversations = v; }) }), _jsx(Field, { label: "Delete conversations older than (days)", hint: "0 keeps them forever.", children: _jsx("input", { className: "input", type: "number", min: 0, value: s.memory.conversationRetentionDays, onChange: (e) => set((x) => { x.memory.conversationRetentionDays = Number(e.target.value); }) }) }), _jsxs("p", { className: "meta", children: ["All data lives in ", status?.dataDir] }), _jsxs("div", { className: "row wrap", children: [_jsxs(ConfirmButton, { prompt: "Delete all conversation history?", onConfirm: () => void del("/conversations"), children: [_jsx(Trash2, { size: 14 }), " Delete conversations"] }), _jsxs(ConfirmButton, { prompt: "Delete ALL memories? This cannot be undone.", onConfirm: () => void del("/memory?confirm=true"), children: [_jsx(Trash2, { size: 14 }), " Delete all memories"] })] })] })] }));
}
function SystemSection({ s, set }) {
    const { status, refresh } = useStatus();
    return (_jsxs(_Fragment, { children: [_jsxs(Card, { title: "Runtime", children: [_jsxs("dl", { className: "kv small", children: [_jsx("dt", { children: "Version" }), _jsx("dd", { children: status?.version }), _jsx("dt", { children: "Platform" }), _jsx("dd", { children: status?.platformDescription }), _jsx("dt", { children: "Data folder" }), _jsx("dd", { className: "mono", children: status?.dataDir })] }), _jsxs("div", { className: "row wrap", children: [_jsx("button", { className: "btn btn-sm", onClick: async () => { await post(status?.paused ? "/runtime/resume" : "/runtime/pause"); await refresh(); }, children: status?.paused ? "Resume JARVIS" : "Pause JARVIS" }), _jsx(ConfirmButton, { className: "btn btn-danger btn-sm", prompt: "Shut down JARVIS completely? Reminders and voice stop until you start it again.", onConfirm: () => void post("/runtime/shutdown"), children: "Shut down JARVIS" }), _jsx("a", { className: "btn btn-ghost btn-sm", href: "#/system", children: "Health & capabilities" })] })] }), _jsx(Card, { title: "Network", children: _jsxs("div", { className: "form-grid", children: [_jsx(Field, { label: "Local API port", hint: "Takes effect after JARVIS restarts. If it's busy, the next free port is used.", children: _jsx("input", { className: "input", type: "number", min: 1024, max: 65535, value: s.runtime.port, onChange: (e) => set((x) => { x.runtime.port = Number(e.target.value); }) }) }), _jsx(Field, { label: "Connectivity check every (seconds)", children: _jsx("input", { className: "input", type: "number", min: 5, value: s.runtime.connectivityProbeSeconds, onChange: (e) => set((x) => { x.runtime.connectivityProbeSeconds = Number(e.target.value); }) }) }), _jsx(Field, { label: "Connectivity probe URL", hint: "Used with other well-known endpoints to decide online/offline.", children: _jsx("input", { className: "input mono", value: s.runtime.connectivityProbeUrl, onChange: (e) => set((x) => { x.runtime.connectivityProbeUrl = e.target.value; }) }) })] }) })] }));
}
function FilesSettings({ s, set }) {
    return (_jsx(_Fragment, { children: _jsxs(Card, { title: "File knowledge", children: [_jsx(Toggle, { label: "Read and index my documents", hint: "Builds a private index on this PC so JARVIS can find, summarise and compare your files. Off by default.", checked: s.files.indexEnabled, onChange: (v) => set((x) => { x.files.indexEnabled = v; }) }), _jsx(Field, { label: "Folders to index", hint: "One per line. Empty = Documents, Desktop and Downloads. Protected places (credential stores, system folders) are always skipped.", children: _jsx("textarea", { className: "input mono", rows: 3, value: s.files.indexRoots.join("\n"), onChange: (e) => set((x) => { x.files.indexRoots = e.target.value.split("\n").map((l) => l.trim()).filter(Boolean); }) }) }), _jsx(Field, { label: "Largest file to read (MB)", hint: "Bigger files are indexed by name only.", children: _jsx("input", { className: "input", type: "number", min: 1, max: 500, value: s.files.indexMaxFileMb, onChange: (e) => set((x) => { x.files.indexMaxFileMb = Number(e.target.value); }) }) }), _jsx(FilesIndexHint, {}), _jsx("div", { className: "row", children: _jsx(ClearIndexButton, {}) })] }) }));
}
