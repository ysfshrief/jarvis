import { useEffect, useRef } from "react";
import { getToken } from "./api";
/** Single shared WebSocket to the runtime's event stream, with automatic reconnect. */
class EventStream {
    socket = null;
    handlers = new Set();
    stateHandlers = new Set();
    retry = 0;
    timer;
    connected = false;
    start() {
        if (this.socket || !getToken())
            return;
        const proto = location.protocol === "https:" ? "wss" : "ws";
        const url = `${proto}://${location.host}/ws?access_token=${encodeURIComponent(getToken() ?? "")}`;
        const ws = new WebSocket(url);
        this.socket = ws;
        ws.onopen = () => {
            this.retry = 0;
            this.setConnected(true);
        };
        ws.onmessage = (m) => {
            try {
                const evt = JSON.parse(m.data);
                this.handlers.forEach((h) => h(evt));
            }
            catch {
                /* ignore malformed */
            }
        };
        ws.onclose = () => {
            this.socket = null;
            this.setConnected(false);
            const delay = Math.min(10000, 500 * 2 ** this.retry++);
            window.clearTimeout(this.timer);
            this.timer = window.setTimeout(() => this.start(), delay);
        };
        ws.onerror = () => ws.close();
    }
    restart() {
        this.socket?.close();
        this.socket = null;
        this.start();
    }
    subscribe(h) {
        this.handlers.add(h);
        return () => {
            this.handlers.delete(h);
        };
    }
    onConnection(h) {
        this.stateHandlers.add(h);
        return () => {
            this.stateHandlers.delete(h);
        };
    }
    setConnected(c) {
        this.connected = c;
        this.stateHandlers.forEach((h) => h(c));
    }
}
export const events = new EventStream();
/** Subscribe to runtime events whose type matches one of the prefixes. */
export function useEvents(prefixes, handler) {
    const ref = useRef(handler);
    ref.current = handler;
    const key = prefixes.join("|");
    useEffect(() => {
        return events.subscribe((e) => {
            if (prefixes.some((p) => e.type === p || e.type.startsWith(p + ".")))
                ref.current(e);
        });
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [key]);
}
