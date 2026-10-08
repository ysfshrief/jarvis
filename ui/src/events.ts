import { useEffect, useRef } from "react";
import { getToken } from "./api";

export interface JarvisEvent {
  type: string;
  data: any; // eslint-disable-line @typescript-eslint/no-explicit-any
  timestamp: string;
}

type Handler = (e: JarvisEvent) => void;

/** Single shared WebSocket to the runtime's event stream, with automatic reconnect. */
class EventStream {
  private socket: WebSocket | null = null;
  private handlers = new Set<Handler>();
  private stateHandlers = new Set<(connected: boolean) => void>();
  private retry = 0;
  private timer: number | undefined;
  connected = false;

  start() {
    if (this.socket || !getToken()) return;
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
        const evt = JSON.parse(m.data) as JarvisEvent;
        this.handlers.forEach((h) => h(evt));
      } catch {
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

  subscribe(h: Handler) {
    this.handlers.add(h);
    return () => {
      this.handlers.delete(h);
    };
  }

  onConnection(h: (c: boolean) => void) {
    this.stateHandlers.add(h);
    return () => {
      this.stateHandlers.delete(h);
    };
  }

  private setConnected(c: boolean) {
    this.connected = c;
    this.stateHandlers.forEach((h) => h(c));
  }
}

export const events = new EventStream();

/** Subscribe to runtime events whose type matches one of the prefixes. */
export function useEvents(prefixes: string[], handler: Handler) {
  const ref = useRef(handler);
  ref.current = handler;
  const key = prefixes.join("|");
  useEffect(() => {
    return events.subscribe((e) => {
      if (prefixes.some((p) => e.type === p || e.type.startsWith(p + "."))) ref.current(e);
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);
}
