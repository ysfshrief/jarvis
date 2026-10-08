import { useEffect, useState } from "react";
import { useActiveTurn, useTurns } from "./turns";
import { tr } from "../lib/i18n";
export const ORB_LABEL = {
    idle: "Standing by",
    listening: "Listening",
    thinking: "Thinking",
    speaking: "Speaking",
    executing: "Executing",
    warning: "Needs your approval",
    error: "Something failed",
    offline: "Offline",
};
/**
 * One source of truth for what JARVIS is doing, in priority order:
 * offline (runtime unreachable or paused) > error (briefly, after a failure) > warning (approval
 * waiting) > speaking > listening > executing > thinking > idle.
 */
export function useOrbState(status, connected, approvals) {
    const active = useActiveTurn();
    const turns = useTurns();
    const last = turns[turns.length - 1];
    const [now, setNow] = useState(() => Date.now());
    const failedAt = last?.result && !last.result.success ? last.finishedAt ?? 0 : 0;
    // Re-render once when the error flash should end; no running timer otherwise.
    useEffect(() => {
        if (!failedAt)
            return;
        const left = failedAt + 4000 - Date.now();
        if (left <= 0)
            return;
        const t = window.setTimeout(() => setNow(Date.now()), left + 20);
        return () => window.clearTimeout(t);
    }, [failedAt]);
    const voice = status?.voice?.state;
    let state = "idle";
    let label = ORB_LABEL.idle;
    if (!connected) {
        state = "offline";
        label = "Runtime unreachable";
    }
    else if (status?.paused) {
        state = "offline";
        label = "Paused";
    }
    else if (failedAt && now - failedAt < 4000 && Date.now() - failedAt < 4000)
        state = "error";
    else if (approvals > 0)
        state = "warning";
    else if (voice === "Speaking")
        state = "speaking";
    else if (voice === "Listening")
        state = "listening";
    else if (voice === "Transcribing")
        state = "thinking";
    else if (active?.phase === "executing")
        state = "executing";
    else if (active)
        state = "thinking";
    if (state !== "offline")
        label = ORB_LABEL[state];
    if (state === "idle" && voice === "WakeListening")
        label = "Listening for “Jarvis”";
    label = tr(label);
    if (state === "executing" && active?.detail)
        label = `${tr("Executing")} · ${active.detail}`;
    return { state, label };
}
