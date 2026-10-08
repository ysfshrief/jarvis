import { useSyncExternalStore } from "react";
import { events } from "../events";
/**
 * A tiny external store fed by the runtime's event stream. Every surface (orb, assistant, console,
 * context panel) reads the same live state, so a request started by voice shows up everywhere.
 */
class TurnStore {
    turns = new Map();
    order = [];
    listeners = new Set();
    version = 0;
    snapshot = { version: 0, turns: [] };
    constructor() {
        events.subscribe((e) => {
            const d = e.data ?? {};
            switch (e.type) {
                case "agent.turn.started":
                    if (!d.turnId)
                        return;
                    this.put({
                        turnId: d.turnId, conversationId: d.conversationId, text: d.text ?? "", source: d.source ?? "Text",
                        phase: "understanding", visited: ["understanding"], draft: "", round: 0, tools: [], startedAt: Date.now(),
                    });
                    return;
                case "agent.turn.phase":
                    this.update(d.turnId, (t) => {
                        t.phase = d.phase;
                        t.detail = d.detail;
                        if (!t.visited.includes(d.phase))
                            t.visited = [...t.visited, d.phase];
                    });
                    return;
                case "agent.turn.delta":
                    this.update(d.turnId, (t) => {
                        if (d.reset) {
                            t.draft = "";
                            return;
                        }
                        // A new model round starts a fresh draft (the previous one led to tool calls).
                        if (d.round !== t.round) {
                            t.draft = "";
                            t.round = d.round;
                        }
                        t.draft += d.text ?? "";
                    });
                    return;
                case "tool.started":
                    this.update(d.turnId, (t) => { t.tools = [...t.tools, { tool: d.tool, summary: d.summary, risk: d.risk, status: "running" }]; });
                    return;
                case "tool.completed":
                    this.update(d.turnId, (t) => {
                        const step = d.step;
                        const i = t.tools.findIndex((x) => x.tool === step.tool && x.status === "running");
                        const done = { tool: step.tool, summary: step.summary, risk: step.risk, status: step.status, message: step.message, durationMs: step.durationMs };
                        t.tools = i >= 0 ? t.tools.map((x, j) => (j === i ? done : x)) : [...t.tools, done];
                    });
                    return;
                case "agent.turn.completed": {
                    const r = d;
                    if (!r.turnId)
                        return;
                    this.update(r.turnId, (t) => {
                        t.result = r;
                        t.finishedAt = Date.now();
                        t.phase = r.success ? "completed" : "failed";
                        if (!t.visited.includes(t.phase))
                            t.visited = [...t.visited, t.phase];
                    });
                    return;
                }
            }
        });
    }
    put(t) {
        this.turns.set(t.turnId, t);
        this.order = [...this.order.filter((id) => id !== t.turnId), t.turnId].slice(-25);
        for (const id of [...this.turns.keys()])
            if (!this.order.includes(id))
                this.turns.delete(id);
        this.emit();
    }
    update(id, fn) {
        if (!id)
            return;
        const cur = this.turns.get(id);
        if (!cur)
            return;
        const next = { ...cur };
        fn(next);
        this.turns.set(id, next);
        this.emit();
    }
    emit() {
        this.version++;
        this.listeners.forEach((l) => l());
    }
    subscribe = (l) => {
        this.listeners.add(l);
        return () => { this.listeners.delete(l); };
    };
    getSnapshot = () => {
        if (this.snapshot.version !== this.version)
            this.snapshot = { version: this.version, turns: this.order.map((id) => this.turns.get(id)).filter(Boolean) };
        return this.snapshot;
    };
    get(id) {
        return this.turns.get(id);
    }
}
export const turnStore = new TurnStore();
/** All recent turns, oldest first. */
export function useTurns() {
    return useSyncExternalStore(turnStore.subscribe, turnStore.getSnapshot).turns;
}
/** The turn in progress, if any (else null). */
export function useActiveTurn() {
    const turns = useTurns();
    for (let i = turns.length - 1; i >= 0; i--)
        if (!turns[i].result)
            return turns[i];
    return null;
}
/** The most recent turn: running, or finished within the last few seconds. */
export function useLatestTurn(windowMs = 8000) {
    const turns = useTurns();
    const last = turns[turns.length - 1];
    if (!last)
        return null;
    if (!last.finishedAt || Date.now() - last.finishedAt < windowMs)
        return last;
    return null;
}
