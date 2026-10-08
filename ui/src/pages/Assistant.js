import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { useEffect, useRef, useState } from "react";
import { Brain, Mic, Plus, Send, Square } from "lucide-react";
import { get, post } from "../api";
import { useStatus } from "../App";
import { useEvents } from "../events";
import { ApprovalCard, useApprovals } from "../components/Approvals";
import { Orb } from "../components/Orb";
import { StepList } from "../components/Steps";
import { Timeline } from "../components/Timeline";
import { Badge } from "../components/ui";
import { useOrbState } from "../lib/orbState";
import { useTurns } from "../lib/turns";
import { tr } from "../lib/i18n";
export function Assistant() {
    const { status, refresh, connected } = useStatus();
    const [conversationId, setConversationId] = useState(null);
    const [messages, setMessages] = useState([]);
    const [text, setText] = useState("");
    const [busy, setBusy] = useState(false);
    const [listening, setListening] = useState(false);
    const [heard, setHeard] = useState(null);
    const approvals = useApprovals();
    const orb = useOrbState(status, connected, approvals.length);
    const turns = useTurns();
    const bottom = useRef(null);
    const input = useRef(null);
    useEffect(() => {
        const id = status?.activeConversation;
        if (!id || conversationId)
            return;
        setConversationId(id);
        get(`/conversations/${id}/messages`)
            .then((list) => setMessages(list.map(fromStored)))
            .catch(() => { });
    }, [status?.activeConversation, conversationId]);
    // Turns in progress for this conversation (any surface: typed here, console, voice).
    const running = turns.filter((t) => !t.result && (!conversationId || t.conversationId === conversationId));
    useEffect(() => {
        bottom.current?.scrollIntoView({ behavior: "smooth", block: "end" });
    }, [messages, approvals.length, running.length, running[running.length - 1]?.draft.length, running[running.length - 1]?.tools.length]);
    // Voice and other surfaces talk to the same conversation; show their turns too.
    useEvents(["agent", "voice"], (e) => {
        if (e.type === "voice.transcript" && e.data.text)
            setHeard(e.data.text);
        if (e.type === "agent.turn.started" && e.data.source !== "Text") {
            setMessages((m) => [...m, { key: `u-${e.data.turnId ?? Date.now()}`, role: "user", text: e.data.text, source: e.data.source }]);
        }
        if (e.type === "agent.turn.completed" && e.data.source !== "Text") {
            setMessages((m) => [...m, toMsg(e.data)]);
            setHeard(null);
        }
    });
    const send = async () => {
        const t = text.trim();
        if (!t || busy)
            return;
        setText("");
        setBusy(true);
        setMessages((m) => [...m, { key: `u-${Date.now()}`, role: "user", text: t }]);
        try {
            const r = await post("/chat", { text: t, conversationId });
            if (!conversationId)
                setConversationId(r.conversationId);
            setMessages((m) => [...m, toMsg(r)]);
        }
        catch (e) {
            setMessages((m) => [...m, { key: `e-${Date.now()}`, role: "assistant", text: e instanceof Error ? e.message : String(e), success: false }]);
        }
        finally {
            setBusy(false);
            input.current?.focus();
        }
    };
    const listen = async () => {
        setListening(true);
        setHeard(null);
        try {
            await post("/voice/listen");
        }
        catch (e) {
            setMessages((m) => [...m, { key: `e-${Date.now()}`, role: "assistant", text: e instanceof Error ? e.message : String(e), success: false }]);
        }
        finally {
            setListening(false);
        }
    };
    const newConversation = async () => {
        const r = await post("/conversations/new");
        setConversationId(r.id);
        setMessages([]);
        await refresh();
    };
    const voice = status?.voice;
    const voiceUsable = !!voice?.audioAvailable && !!voice?.sttReady && !status?.paused;
    const voiceTitle = !voice?.audioAvailable ? "No microphone detected" : !voice?.sttReady ? voice?.sttMessage ?? "Download a speech model in Settings → Voice" : "Push to talk";
    return (_jsxs("div", { className: "chat", children: [_jsxs("div", { className: "page-head", children: [_jsxs("div", { className: "row", children: [_jsx("h2", { children: tr("Assistant") }), _jsx("span", { className: "orb-label", "data-state": orb.state, style: { marginInlineStart: 8 }, children: orb.label })] }), _jsxs("button", { className: "btn btn-ghost", onClick: newConversation, children: [_jsx(Plus, { size: 15 }), " ", tr("New conversation")] })] }), _jsxs("div", { className: "chat-log", "aria-live": "polite", children: [messages.length === 0 && running.length === 0 && (_jsxs("div", { className: "chat-empty", children: [_jsx(Orb, { state: orb.state, size: 120, label: orb.label }), _jsxs("p", { children: [tr("How can I help"), status?.honorific ? `, ${status.honorific}` : "", "?"] }), _jsx("div", { className: "suggestions", children: ["What can you do?", "What's happening today?", "open calculator", "remind me in 20 minutes to call Ahmed", "الساعة كام؟", "how's the system"].map((s) => (_jsx("button", { className: "chip-btn", dir: "auto", onClick: () => { setText(s); input.current?.focus(); }, children: s }, s))) })] })), messages.map((m) => _jsx(Message, { m: m }, m.key)), running.map((t) => (_jsxs("div", { className: "msg", children: [_jsx(Orb, { state: t.phase === "executing" ? "executing" : "thinking", size: 30 }), _jsxs("div", { className: "msg-body", children: [_jsx(Timeline, { turn: t }), t.tools.length > 0 && _jsx(StepList, { steps: t.tools }), t.draft ? (_jsx("div", { className: "bubble", children: _jsx("div", { className: "bubble-text cursor", dir: "auto", children: t.draft }) })) : (_jsx("div", { className: "bubble", children: _jsxs("span", { className: "typing", children: [_jsx("i", {}), _jsx("i", {}), _jsx("i", {})] }) }))] })] }, t.turnId))), busy && running.length === 0 && (_jsxs("div", { className: "msg", children: [_jsx(Orb, { state: "thinking", size: 30 }), _jsx("div", { className: "bubble", children: _jsxs("span", { className: "typing", children: [_jsx("i", {}), _jsx("i", {}), _jsx("i", {})] }) })] })), approvals.map((a) => _jsx(ApprovalCard, { approval: a }, a.id)), _jsx("div", { ref: bottom })] }), (listening || heard) && (_jsx("div", { className: "heard", dir: "auto", children: listening && !heard ? "Listening… speak now." : `Heard: “${heard}”` })), _jsxs("form", { className: "composer", onSubmit: (e) => { e.preventDefault(); void send(); }, children: [_jsx("textarea", { ref: input, className: "input grow", dir: "auto", rows: 1, placeholder: tr("Message JARVIS — English or عربي"), value: text, onChange: (e) => setText(e.target.value), onKeyDown: (e) => {
                            if (e.key === "Enter" && !e.shiftKey) {
                                e.preventDefault();
                                void send();
                            }
                        } }), _jsx("button", { type: "button", className: `btn btn-icon ${listening ? "btn-danger" : ""}`, onClick: listening ? () => post("/voice/stop") : listen, disabled: !voiceUsable && !listening, title: voiceTitle, "aria-label": voiceTitle, children: listening ? _jsx(Square, { size: 16 }) : _jsx(Mic, { size: 16 }) }), _jsx("button", { type: "submit", className: "btn btn-primary btn-icon", disabled: busy || !text.trim(), "aria-label": "Send", children: _jsx(Send, { size: 16 }) })] })] }));
}
function Message({ m }) {
    const user = m.role === "user";
    return (_jsxs("div", { className: `msg ${user ? "msg-user" : ""} ${m.success === false ? "msg-error" : ""}`, children: [!user && _jsx(Orb, { state: m.success === false ? "error" : "idle", size: 30 }), _jsxs("div", { className: "msg-body", children: [!user && m.steps && m.steps.length > 0 && _jsx(StepList, { steps: m.steps }), _jsx("div", { className: "bubble", children: _jsx("div", { className: "bubble-text", dir: "auto", children: m.text }) }), !user && m.memories && m.memories.length > 0 && (_jsxs("div", { className: "memchips", title: "Memories JARVIS used for this answer", children: [_jsx(Brain, { size: 13, className: "dim" }), m.memories.slice(0, 4).map((x) => (_jsx(Badge, { tone: x.source === "user" || x.source === "confirmed" ? "accent" : "warn", title: `${x.kind} · ${x.source}`, children: _jsx("span", { className: "truncate", style: { maxWidth: 220, textTransform: "none" }, dir: "auto", children: x.content }) }, x.id)))] })), !user && m.route && (_jsxs("div", { className: "msg-meta meta", children: [m.route === "ai" ? `AI · ${m.model ?? ""}` : m.route === "deterministic" ? tr("Direct command") : "No model", m.fallbackFrom && _jsx(Badge, { tone: "warn", title: `${m.fallbackFrom} failed, another model answered`, children: "fallback" }), m.source?.toLowerCase() === "voice" && _jsx(Badge, { tone: "info", children: "voice" }), m.durationMs ? _jsxs("span", { children: [(m.durationMs / 1000).toFixed(1), " s"] }) : null] }))] })] }));
}
function toMsg(r) {
    return {
        key: `a-${r.turnId ?? Date.now()}-${Math.random()}`, role: "assistant", text: r.reply, steps: r.steps, route: r.route, model: r.model,
        success: r.success, source: r.source, memories: r.usedMemories, fallbackFrom: r.fallbackFrom, durationMs: r.durationMs, turnId: r.turnId,
    };
}
function fromStored(s) {
    let meta = {};
    try {
        meta = s.meta ? JSON.parse(s.meta) : {};
    }
    catch {
        /* ignore */
    }
    return {
        key: `s-${s.id}`, role: s.role, text: s.content, route: meta.route, model: meta.model, steps: meta.steps ?? [],
        source: s.source ?? undefined, memories: meta.usedMemories, fallbackFrom: meta.fallbackFrom,
    };
}
