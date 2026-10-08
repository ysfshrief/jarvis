import type { SoundSettings } from "../api";
import { events } from "../events";
import { settingsStore } from "./settings";

export type SoundKind = "wake" | "accepted" | "processing" | "completed" | "warning" | "error" | "notification";

type Note = { f: number; to?: number; t: number; d: number; type?: OscillatorType; g?: number };

/** Short synthesized cues — no audio files, nothing loud. Each is under half a second. */
const CUES: Record<SoundKind, Note[]> = {
  wake: [{ f: 523, to: 784, t: 0, d: 0.16, g: 0.5 }, { f: 784, to: 1046, t: 0.12, d: 0.22, g: 0.45 }],
  accepted: [{ f: 880, t: 0, d: 0.07, g: 0.35 }],
  processing: [{ f: 1320, t: 0, d: 0.03, g: 0.12 }],
  completed: [{ f: 784, t: 0, d: 0.12, g: 0.35 }, { f: 1175, t: 0.09, d: 0.22, g: 0.3 }],
  warning: [{ f: 466, t: 0, d: 0.14, type: "triangle", g: 0.45 }, { f: 466, t: 0.2, d: 0.14, type: "triangle", g: 0.45 }],
  error: [{ f: 392, to: 262, t: 0, d: 0.32, type: "triangle", g: 0.45 }],
  notification: [{ f: 1046, t: 0, d: 0.35, g: 0.3 }, { f: 1568, t: 0.06, d: 0.3, g: 0.12 }],
};

let ctx: AudioContext | null = null;
const channel = typeof BroadcastChannel !== "undefined" ? new BroadcastChannel("jarvis-sound") : null;
const claimed = new Map<string, number>();
channel?.addEventListener("message", (m) => claimed.set(String(m.data), Date.now()));

function audio(): AudioContext | null {
  try {
    ctx ??= new AudioContext();
    if (ctx.state === "suspended") void ctx.resume();
    return ctx;
  } catch {
    return null;
  }
}

// Browsers only allow sound after the user has interacted with the page once.
if (typeof window !== "undefined") {
  const unlock = () => { audio(); window.removeEventListener("pointerdown", unlock); window.removeEventListener("keydown", unlock); };
  window.addEventListener("pointerdown", unlock);
  window.addEventListener("keydown", unlock);
}

export function soundEnabled(s: SoundSettings | undefined, kind: SoundKind) {
  return !!s && s.enabled && s.volume > 0 && s[kind];
}

/** Plays a cue now, regardless of other windows (used by the settings "test" buttons). */
export function playCue(kind: SoundKind, volume: number) {
  const ac = audio();
  if (!ac || ac.state !== "running") return;
  const master = ac.createGain();
  master.gain.value = Math.max(0, Math.min(1, volume)) * 0.5;
  master.connect(ac.destination);
  const now = ac.currentTime + 0.01;
  for (const n of CUES[kind]) {
    const osc = ac.createOscillator();
    const g = ac.createGain();
    osc.type = n.type ?? "sine";
    osc.frequency.setValueAtTime(n.f, now + n.t);
    if (n.to) osc.frequency.exponentialRampToValueAtTime(n.to, now + n.t + n.d);
    g.gain.setValueAtTime(0.0001, now + n.t);
    g.gain.exponentialRampToValueAtTime(n.g ?? 0.3, now + n.t + 0.012);
    g.gain.exponentialRampToValueAtTime(0.0001, now + n.t + n.d);
    osc.connect(g).connect(master);
    osc.start(now + n.t);
    osc.stop(now + n.t + n.d + 0.02);
  }
}

/**
 * Plays a cue for a runtime event once across all open JARVIS windows: the first visible window
 * claims the event key over a BroadcastChannel and the others stay quiet.
 */
function cue(kind: SoundKind, key: string) {
  const s = settingsStore.getSnapshot()?.sounds;
  if (!soundEnabled(s, kind) || document.visibilityState !== "visible") return;
  const delay = document.hasFocus() ? 0 : 60; // the focused window wins ties
  window.setTimeout(() => {
    const at = claimed.get(key);
    if (at && Date.now() - at < 3000) return;
    claimed.set(key, Date.now());
    channel?.postMessage(key);
    playCue(kind, s!.volume);
  }, delay);
  if (claimed.size > 200) claimed.clear();
}

let lastVoice = "";
events.subscribe((e) => {
  const d = e.data ?? {};
  switch (e.type) {
    case "voice.state":
      if (d.state === "Listening" && lastVoice !== "Listening") cue("wake", `wake-${e.timestamp}`);
      lastVoice = d.state;
      break;
    case "agent.turn.started":
      cue("accepted", `acc-${d.turnId}`);
      break;
    case "tool.started":
      cue("processing", `proc-${d.turnId}-${d.tool}-${e.timestamp}`);
      break;
    case "agent.turn.completed":
      cue(d.success ? "completed" : "error", `done-${d.turnId}`);
      break;
    case "approval.requested":
      cue("warning", `appr-${d.id}`);
      break;
    case "notification":
      if (d.decision?.deliverNow) cue("notification", `note-${d.notification?.id}`);
      break;
  }
});
