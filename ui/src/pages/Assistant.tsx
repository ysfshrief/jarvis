import { useEffect, useRef, useState } from "react";
import { Mic, Plus, Send, Square, Wrench } from "lucide-react";
import { get, post, type StoredMessage, type ToolStep, type TurnResult } from "../api";
import { useStatus } from "../App";
import { useEvents } from "../events";
import { ApprovalCard, useApprovals } from "../components/Approvals";
import { Badge, RiskBadge } from "../components/ui";

interface Msg {
  key: string;
  role: "user" | "assistant";
  text: string;
  steps?: ToolStep[];
  route?: string;
  model?: string | null;
  success?: boolean;
  source?: string;
  pending?: boolean;
}

export function Assistant() {
  const { status, refresh } = useStatus();
  const [conversationId, setConversationId] = useState<string | null>(null);
  const [messages, setMessages] = useState<Msg[]>([]);
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [listening, setListening] = useState(false);
  const [heard, setHeard] = useState<string | null>(null);
  const approvals = useApprovals();
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

  useEffect(() => {
    bottom.current?.scrollIntoView({ behavior: "smooth", block: "end" });
  }, [messages, approvals.length]);

  // Voice and other surfaces (quick bar, phone later) talk to the same conversation; show their turns too.
  useEvents(["agent", "voice"], (e) => {
    if (e.type === "voice.transcript" && e.data.text) setHeard(e.data.text);
    if (e.type === "agent.turn.started" && e.data.source !== "Text") {
      setMessages((m) => [...m, { key: `u-${Date.now()}`, role: "user", text: e.data.text, source: e.data.source }]);
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
    setMessages((m) => [...m, { key: `u-${Date.now()}`, role: "user", text: t }, { key: "pending", role: "assistant", text: "", pending: true }]);
    try {
      const r = await post<TurnResult>("/chat", { text: t, conversationId });
      if (!conversationId) setConversationId(r.conversationId);
      setMessages((m) => [...m.filter((x) => !x.pending), toMsg(r)]);
    } catch (e) {
      setMessages((m) => [...m.filter((x) => !x.pending), { key: `e-${Date.now()}`, role: "assistant", text: e instanceof Error ? e.message : String(e), success: false }]);
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
      <div className="chat-head">
        <h2>Assistant</h2>
        <button className="btn btn-ghost" onClick={newConversation}>
          <Plus size={16} /> New conversation
        </button>
      </div>

      <div className="chat-log" aria-live="polite">
        {messages.length === 0 && (
          <div className="chat-empty">
            <p>Try:</p>
            <div className="suggestions">
              {["What can you do?", "open calculator", "remind me in 20 minutes to call Ahmed", "الساعة كام؟", "how's the system", "run git --version"].map((s) => (
                <button key={s} className="chip-btn" dir="auto" onClick={() => setText(s)}>
                  {s}
                </button>
              ))}
            </div>
          </div>
        )}
        {messages.map((m) => (
          <Bubble key={m.key} m={m} />
        ))}
        {approvals.map((a) => (
          <ApprovalCard key={a.id} approval={a} />
        ))}
        <div ref={bottom} />
      </div>

      {(listening || heard) && (
        <div className="heard" dir="auto">
          {listening && !heard ? "Listening… speak now." : `Heard: “${heard}”`}
        </div>
      )}

      <form className="composer" onSubmit={(e) => { e.preventDefault(); void send(); }}>
        <textarea
          ref={input}
          className="input grow"
          dir="auto"
          rows={1}
          placeholder="Message JARVIS — English or عربي"
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !e.shiftKey) {
              e.preventDefault();
              void send();
            }
          }}
        />
        <button type="button" className={`btn ${listening ? "btn-danger" : ""}`} onClick={listening ? () => post("/voice/stop") : listen} disabled={!voiceUsable && !listening} title={voiceTitle} aria-label={voiceTitle}>
          {listening ? <Square size={16} /> : <Mic size={16} />}
        </button>
        <button type="submit" className="btn btn-primary" disabled={busy || !text.trim()} aria-label="Send">
          <Send size={16} />
        </button>
      </form>
    </div>
  );
}

function Bubble({ m }: { m: Msg }) {
  if (m.pending)
    return (
      <div className="bubble bubble-assistant">
        <span className="typing"><i /><i /><i /></span>
      </div>
    );
  return (
    <div className={`bubble bubble-${m.role} ${m.success === false ? "bubble-error" : ""}`}>
      <div className="bubble-text" dir="auto">{m.text}</div>
      {m.steps && m.steps.length > 0 && (
        <div className="steps-list">
          {m.steps.map((s, i) => (
            <div key={i} className={`step step-${s.status.toLowerCase()}`} title={s.message}>
              <Wrench size={12} />
              <span className="step-tool">{s.tool}</span>
              <span className="truncate" dir="auto">{s.summary}</span>
              <RiskBadge risk={s.risk} />
              <Badge tone={s.status === "Ok" ? "good" : s.status === "Queued" ? "info" : "bad"}>{s.status}</Badge>
            </div>
          ))}
        </div>
      )}
      {m.role === "assistant" && m.route && (
        <div className="bubble-meta">
          {m.route === "ai" ? `AI · ${m.model ?? ""}` : m.route === "deterministic" ? "Direct command" : "No model"}
          {m.source === "Voice" && " · voice"}
        </div>
      )}
    </div>
  );
}

function toMsg(r: TurnResult): Msg {
  return { key: `a-${Date.now()}-${Math.random()}`, role: "assistant", text: r.reply, steps: r.steps, route: r.route, model: r.model, success: r.success, source: r.source };
}

function fromStored(s: StoredMessage): Msg {
  let meta: { route?: string; model?: string | null; steps?: ToolStep[] } = {};
  try {
    meta = s.meta ? JSON.parse(s.meta) : {};
  } catch {
    /* ignore */
  }
  return { key: `s-${s.id}`, role: s.role, text: s.content, route: meta.route, model: meta.model, steps: meta.steps ?? [], source: s.source ?? undefined };
}
