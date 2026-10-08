import { useEffect, useRef, useState } from "react";
import { Brain, Mic, Plus, Send, Square } from "lucide-react";
import { get, post, type StoredMessage, type ToolStep, type TurnResult, type UsedMemory } from "../api";
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

interface Msg {
  key: string;
  role: "user" | "assistant";
  text: string;
  steps?: ToolStep[];
  route?: string;
  model?: string | null;
  success?: boolean;
  source?: string;
  memories?: UsedMemory[];
  fallbackFrom?: string | null;
  durationMs?: number;
  turnId?: string;
}

export function Assistant() {
  const { status, refresh, connected } = useStatus();
  const [conversationId, setConversationId] = useState<string | null>(null);
  const [messages, setMessages] = useState<Msg[]>([]);
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [listening, setListening] = useState(false);
  const [heard, setHeard] = useState<string | null>(null);
  const approvals = useApprovals();
  const orb = useOrbState(status, connected, approvals.length);
  const turns = useTurns();
  const bottom = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    const id = status?.activeConversation;
    if (!id || conversationId) return;
    setConversationId(id);
    get<StoredMessage[]>(`/conversations/${id}/messages`)
      .then((list) => setMessages(list.map(fromStored)))
      .catch(() => {});
  }, [status?.activeConversation, conversationId]);

  // Turns in progress for this conversation (any surface: typed here, console, voice).
  const running = turns.filter((t) => !t.result && (!conversationId || t.conversationId === conversationId));

  useEffect(() => {
    bottom.current?.scrollIntoView({ behavior: "smooth", block: "end" });
  }, [messages, approvals.length, running.length, running[running.length - 1]?.draft.length, running[running.length - 1]?.tools.length]);

  // Voice and other surfaces talk to the same conversation; show their turns too.
  useEvents(["agent", "voice"], (e) => {
    if (e.type === "voice.transcript" && e.data.text) setHeard(e.data.text);
    if (e.type === "agent.turn.started" && e.data.source !== "Text") {
      setMessages((m) => [...m, { key: `u-${e.data.turnId ?? Date.now()}`, role: "user", text: e.data.text, source: e.data.source }]);
    }
    if (e.type === "agent.turn.completed" && e.data.source !== "Text") {
      setMessages((m) => [...m, toMsg(e.data as TurnResult)]);
      setHeard(null);
    }
  });

  const send = async () => {
    const t = text.trim();
    if (!t || busy) return;
    setText("");
    setBusy(true);
    setMessages((m) => [...m, { key: `u-${Date.now()}`, role: "user", text: t }]);
    try {
      const r = await post<TurnResult>("/chat", { text: t, conversationId });
      if (!conversationId) setConversationId(r.conversationId);
      setMessages((m) => [...m, toMsg(r)]);
    } catch (e) {
      setMessages((m) => [...m, { key: `e-${Date.now()}`, role: "assistant", text: e instanceof Error ? e.message : String(e), success: false }]);
    } finally {
      setBusy(false);
      input.current?.focus();
    }
  };

  const listen = async () => {
    setListening(true);
    setHeard(null);
    try {
      await post("/voice/listen");
    } catch (e) {
      setMessages((m) => [...m, { key: `e-${Date.now()}`, role: "assistant", text: e instanceof Error ? e.message : String(e), success: false }]);
    } finally {
      setListening(false);
    }
  };

  const newConversation = async () => {
    const r = await post<{ id: string }>("/conversations/new");
    setConversationId(r.id);
    setMessages([]);
    await refresh();
  };

  const voice = status?.voice;
  const voiceUsable = !!voice?.audioAvailable && !!voice?.sttReady && !status?.paused;
  const voiceTitle = !voice?.audioAvailable ? "No microphone detected" : !voice?.sttReady ? voice?.sttMessage ?? "Download a speech model in Settings → Voice" : "Push to talk";

  return (
    <div className="chat">
      <div className="page-head">
        <div className="row">
          <h2>{tr("Assistant")}</h2>
          <span className="orb-label" data-state={orb.state} style={{ marginInlineStart: 8 }}>{orb.label}</span>
        </div>
        <button className="btn btn-ghost" onClick={newConversation}><Plus size={15} /> {tr("New conversation")}</button>
      </div>

      <div className="chat-log" aria-live="polite">
        {messages.length === 0 && running.length === 0 && (
          <div className="chat-empty">
            <Orb state={orb.state} size={120} label={orb.label} />
            <p>{tr("How can I help")}{status?.honorific ? `, ${status.honorific}` : ""}?</p>
            <div className="suggestions">
              {["What can you do?", "What's happening today?", "open calculator", "remind me in 20 minutes to call Ahmed", "الساعة كام؟", "how's the system"].map((s) => (
                <button key={s} className="chip-btn" dir="auto" onClick={() => { setText(s); input.current?.focus(); }}>{s}</button>
              ))}
            </div>
          </div>
        )}
        {messages.map((m) => <Message key={m.key} m={m} />)}
        {running.map((t) => (
          <div key={t.turnId} className="msg">
            <Orb state={t.phase === "executing" ? "executing" : "thinking"} size={30} />
            <div className="msg-body">
              <Timeline turn={t} />
              {t.tools.length > 0 && <StepList steps={t.tools} />}
              {t.draft ? (
                <div className="bubble"><div className="bubble-text cursor" dir="auto">{t.draft}</div></div>
              ) : (
                <div className="bubble"><span className="typing"><i /><i /><i /></span></div>
              )}
            </div>
          </div>
        ))}
        {busy && running.length === 0 && (
          <div className="msg"><Orb state="thinking" size={30} /><div className="bubble"><span className="typing"><i /><i /><i /></span></div></div>
        )}
        {approvals.map((a) => <ApprovalCard key={a.id} approval={a} />)}
        <div ref={bottom} />
      </div>

      {(listening || heard) && (
        <div className="heard" dir="auto">{listening && !heard ? "Listening… speak now." : `Heard: “${heard}”`}</div>
      )}

      <form className="composer" onSubmit={(e) => { e.preventDefault(); void send(); }}>
        <textarea
          ref={input}
          className="input grow"
          dir="auto"
          rows={1}
          placeholder={tr("Message JARVIS — English or عربي")}
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !e.shiftKey) {
              e.preventDefault();
              void send();
            }
          }}
        />
        <button type="button" className={`btn btn-icon ${listening ? "btn-danger" : ""}`} onClick={listening ? () => post("/voice/stop") : listen} disabled={!voiceUsable && !listening} title={voiceTitle} aria-label={voiceTitle}>
          {listening ? <Square size={16} /> : <Mic size={16} />}
        </button>
        <button type="submit" className="btn btn-primary btn-icon" disabled={busy || !text.trim()} aria-label="Send"><Send size={16} /></button>
      </form>
    </div>
  );
}

function Message({ m }: { m: Msg }) {
  const user = m.role === "user";
  return (
    <div className={`msg ${user ? "msg-user" : ""} ${m.success === false ? "msg-error" : ""}`}>
      {!user && <Orb state={m.success === false ? "error" : "idle"} size={30} />}
      <div className="msg-body">
        {!user && m.steps && m.steps.length > 0 && <StepList steps={m.steps} />}
        <div className="bubble"><div className="bubble-text" dir="auto">{m.text}</div></div>
        {!user && m.memories && m.memories.length > 0 && (
          <div className="memchips" title="Memories JARVIS used for this answer">
            <Brain size={13} className="dim" />
            {m.memories.slice(0, 4).map((x) => (
              <Badge key={x.id} tone={x.source === "user" || x.source === "confirmed" ? "accent" : "warn"} title={`${x.kind} · ${x.source}`}>
                <span className="truncate" style={{ maxWidth: 220, textTransform: "none" }} dir="auto">{x.content}</span>
              </Badge>
            ))}
          </div>
        )}
        {!user && m.route && (
          <div className="msg-meta meta">
            {m.route === "ai" ? `AI · ${m.model ?? ""}` : m.route === "deterministic" ? tr("Direct command") : "No model"}
            {m.fallbackFrom && <Badge tone="warn" title={`${m.fallbackFrom} failed, another model answered`}>fallback</Badge>}
            {m.source?.toLowerCase() === "voice" && <Badge tone="info">voice</Badge>}
            {m.durationMs ? <span>{(m.durationMs / 1000).toFixed(1)} s</span> : null}
          </div>
        )}
      </div>
    </div>
  );
}

function toMsg(r: TurnResult): Msg {
  return {
    key: `a-${r.turnId ?? Date.now()}-${Math.random()}`, role: "assistant", text: r.reply, steps: r.steps, route: r.route, model: r.model,
    success: r.success, source: r.source, memories: r.usedMemories, fallbackFrom: r.fallbackFrom, durationMs: r.durationMs, turnId: r.turnId,
  };
}

function fromStored(s: StoredMessage): Msg {
  let meta: { route?: string; model?: string | null; steps?: ToolStep[]; usedMemories?: UsedMemory[]; fallbackFrom?: string | null } = {};
  try {
    meta = s.meta ? JSON.parse(s.meta) : {};
  } catch {
    /* ignore */
  }
  return {
    key: `s-${s.id}`, role: s.role, text: s.content, route: meta.route, model: meta.model, steps: meta.steps ?? [],
    source: s.source ?? undefined, memories: meta.usedMemories, fallbackFrom: meta.fallbackFrom,
  };
}
