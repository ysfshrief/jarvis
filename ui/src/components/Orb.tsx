import { useId } from "react";
import type { OrbState } from "../lib/orbState";

/**
 * JARVIS's visual identity: concentric HUD rings around a luminous core. Motion encodes state
 * (see styles.css) and uses only transform/opacity, so it stays on the GPU and costs little.
 * The waveform shows that the microphone or voice is active; it isn't a level meter.
 */
export function Orb({ state, size = 48, label, hollow = false }: { state: OrbState | string; size?: number | string; label?: string; hollow?: boolean }) {
  const id = useId().replace(/:/g, "");
  const st = normalize(state);
  const small = typeof size === "number" && size < 80;
  const bars = small ? 12 : 28;
  return (
    <div className={`orb ${small ? "small" : ""} ${hollow ? "hollow" : ""}`} data-state={st} style={{ width: size, height: size }} role="img" aria-label={`JARVIS: ${label ?? st}`}>
      <svg viewBox="0 0 200 200">
        <defs>
          <radialGradient id={`g${id}`}>
            <stop offset="0%" stopColor="currentColor" stopOpacity="0.55" />
            <stop offset="45%" stopColor="currentColor" stopOpacity="0.16" />
            <stop offset="100%" stopColor="currentColor" stopOpacity="0" />
          </radialGradient>
          <radialGradient id="hollowCore" cx="50%" cy="50%" r="50%">
            <stop offset="0%" stopColor="#020a12" stopOpacity="0.92" />
            <stop offset="78%" stopColor="#04121e" stopOpacity="0.9" />
            <stop offset="100%" stopColor="currentColor" stopOpacity="0.35" />
          </radialGradient>
          <radialGradient id={`c${id}`} cx="50%" cy="42%" r="60%">
            <stop offset="0%" stopColor="#f2fdff" />
            <stop offset="22%" stopColor="#d8f7ff" />
            <stop offset="55%" stopColor="currentColor" />
            <stop offset="100%" stopColor="#031522" />
          </radialGradient>
        </defs>
        <circle className="o-glow" cx="100" cy="100" r="98" style={{ fill: `url(#g${id})` }} />
        <circle className="o-ticks" cx="100" cy="100" r="93" />
        <circle className="o-seg" cx="100" cy="100" r="84" />
        <circle className="o-arc" cx="100" cy="100" r="75" strokeDasharray="70 401" />
        <circle className="o-inner" cx="100" cy="100" r="66" />
        <g className="o-wave">
          {Array.from({ length: bars }, (_, i) => {
            const a = (360 / bars) * i;
            return (
              <rect
                key={i}
                x="98.6" y={hollow ? 37 : 40} width="2.8" height={hollow ? 8 : 12} rx="1.4"
                transform={`rotate(${a} 100 100)`}
                style={{ animationDelay: `${((i * 7) % bars) * 0.045}s` }}
              />
            );
          })}
        </g>
        <circle className="o-core" cx="100" cy="100" r={hollow ? 54 : 42} style={{ fill: `url(#c${id})` }} />
        <circle className="o-core-ring" cx="100" cy="100" r={hollow ? 54 : 42} />
        {!small && (
          <g className="o-particles">
            <circle cx="100" cy="7" r="1.6" />
            <circle cx="186" cy="118" r="1.2" />
            <circle cx="40" cy="168" r="1.4" />
            <circle cx="22" cy="62" r="1" />
          </g>
        )}
      </svg>
    </div>
  );
}

/** Accepts both the new state names and the runtime's voice states. */
function normalize(s: string): OrbState {
  const v = s.toLowerCase();
  if (v === "paused" || v === "disconnected" || v === "unavailable") return "offline";
  if (v === "wakelistening") return "idle";
  if (v === "transcribing") return "thinking";
  return (["idle", "listening", "thinking", "speaking", "executing", "warning", "error", "offline"].includes(v) ? v : "idle") as OrbState;
}
