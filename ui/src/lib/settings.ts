import { useSyncExternalStore } from "react";
import { get, type Appearance, type Settings } from "../api";
import { events } from "../events";

type Listener = () => void;

/** Shared copy of the runtime settings, refreshed whenever any surface saves. */
class SettingsStore {
  private value: Settings | null = null;
  private listeners = new Set<Listener>();
  private loading: Promise<void> | null = null;

  constructor() {
    events.subscribe((e) => {
      if (e.type === "settings.changed") void this.load();
    });
    events.onConnection((c) => { if (c) void this.load(); });
  }

  load(): Promise<void> {
    this.loading ??= get<Settings>("/settings")
      .then((s) => this.set(s))
      .catch(() => {})
      .finally(() => { this.loading = null; });
    return this.loading;
  }

  set(s: Settings) {
    this.value = s;
    applyAppearance(s.appearance);
    this.listeners.forEach((l) => l());
  }

  subscribe = (l: Listener) => {
    this.listeners.add(l);
    return () => { this.listeners.delete(l); };
  };

  getSnapshot = () => this.value;
}

export const settingsStore = new SettingsStore();

export function useSettings(): Settings | null {
  return useSyncExternalStore(settingsStore.subscribe, settingsStore.getSnapshot);
}

const media = typeof window !== "undefined" ? window.matchMedia("(prefers-color-scheme: light)") : null;
let lastAppearance: Appearance | undefined;
media?.addEventListener("change", () => { if (lastAppearance) applyAppearance(lastAppearance); });

/** Appearance settings become attributes on <html>; the stylesheet does the rest. */
export function applyAppearance(a: Appearance | undefined) {
  if (!a) return;
  lastAppearance = a;
  const root = document.documentElement;
  const theme = a.theme === "auto" ? (media?.matches ? "light" : "dark") : a.theme;
  root.dataset.theme = theme;
  root.dataset.accent = a.accent;
  root.dataset.motion = a.motion;
  root.dataset.hud = a.hudEffects ? "on" : "off";
  root.dataset.density = a.density;
  root.style.setProperty("--text-scale", String(a.textScale || 1));
  root.lang = a.language === "ar" ? "ar" : "en";
  root.dir = a.language === "ar" ? "rtl" : "ltr";
}
