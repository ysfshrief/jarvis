// Thin client for the JARVIS runtime's local API. Every request carries the runtime token,
// which the desktop shell passes in the URL fragment (#token=...) and we keep in sessionStorage.

const TOKEN_KEY = "jarvis.token";

export function initToken(): string | null {
  const hash = new URLSearchParams(window.location.hash.replace(/^#/, ""));
  const fromHash = hash.get("token");
  if (fromHash) {
    sessionStorage.setItem(TOKEN_KEY, fromHash);
    hash.delete("token");
    const rest = hash.toString();
    history.replaceState(null, "", window.location.pathname + window.location.search + (rest ? "#" + rest : ""));
  }
  return sessionStorage.getItem(TOKEN_KEY);
}

export function setToken(token: string) {
  sessionStorage.setItem(TOKEN_KEY, token.trim());
}

export function getToken(): string | null {
  return sessionStorage.getItem(TOKEN_KEY);
}

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

type Listener = (status: number) => void;
const authListeners = new Set<Listener>();
export function onAuthProblem(l: Listener) {
  authListeners.add(l);
  return () => authListeners.delete(l);
}

export async function api<T = unknown>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(`./api${path}`, {
    method,
    headers: {
      "X-Jarvis-Token": getToken() ?? "",
      ...(body !== undefined ? { "Content-Type": "application/json" } : {}),
    },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  if (res.status === 401 || res.status === 423) authListeners.forEach((l) => l(res.status));
  const text = await res.text();
  const data = text ? safeJson(text) : null;
  if (!res.ok) {
    const msg = (data && typeof data === "object" && "error" in data ? String((data as { error: unknown }).error) : null) ?? `${res.status} ${res.statusText}`;
    throw new ApiError(res.status, msg);
  }
  return data as T;
}

function safeJson(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

export const get = <T,>(p: string) => api<T>("GET", p);
export const post = <T,>(p: string, b?: unknown) => api<T>("POST", p, b ?? {});
export const put = <T,>(p: string, b?: unknown) => api<T>("PUT", p, b ?? {});
export const del = <T,>(p: string) => api<T>("DELETE", p);

// ---------- Types mirrored from the runtime ----------

export type Risk = "Safe" | "Sensitive" | "Critical";
export type ToolStatus = "Ok" | "Failed" | "Denied" | "Queued" | "TimedOut" | "NotFound";

export interface ToolStep {
  tool: string;
  summary: string;
  risk: Risk;
  status: ToolStatus;
  message: string;
  durationMs: number;
  data?: unknown;
}

export interface TurnResult {
  conversationId: string;
  reply: string;
  lang: "en" | "ar";
  route: "deterministic" | "ai" | "none";
  model?: string | null;
  steps: ToolStep[];
  success: boolean;
  durationMs: number;
  source: string;
}

export interface Approval {
  id: string;
  conversationId: string;
  tool: string;
  risk: Risk;
  summary: string;
  reason: string;
  arguments: string;
  createdAt: string;
  expiresAt: string;
}

export interface Presence {
  state: string;
  activeProcess?: string | null;
  activeWindowTitle?: string | null;
  idleSeconds: number;
  isFullscreen: boolean;
  microphoneInUse: boolean;
  inMeeting: boolean;
  meetingApp?: string | null;
  sessionLocked: boolean;
  activity: string;
}

export interface VoiceStatus {
  state: string;
  microphoneActive: boolean;
  wakeWordEnabled: boolean;
  sttReady: boolean;
  sttMessage?: string | null;
  sttEngine: string;
  ttsAvailable: boolean;
  ttsEngine: string;
  audioAvailable: boolean;
  audioDevice?: string | null;
  lastTranscript?: string | null;
}

export interface Status {
  version: string;
  locked: boolean;
  pinSet: boolean;
  platform: string;
  platformDescription?: string;
  uptimeSeconds?: number;
  paused?: boolean;
  online?: boolean;
  presence?: { supported: boolean; snapshot: Presence };
  voice?: VoiceStatus;
  ai?: {
    allowCloud: boolean;
    anyAvailable: boolean;
    providers: { id: string; name: string; isLocal: boolean; available?: boolean | null; message?: string | null; models: number }[];
  };
  pendingApprovals?: number;
  queuedActions?: number;
  counts?: { memories: number; openTasks: number; upcomingReminders: number };
  activeConversation?: string;
  dataDir?: string;
  secretsProtection?: string;
  honorific?: string;
  userName?: string;
}

export interface ActivityEntry {
  id: number;
  timestamp: string;
  kind: string;
  tool?: string | null;
  summary: string;
  risk?: string | null;
  status?: string | null;
  details?: string | null;
  conversationId?: string | null;
  durationMs?: number | null;
}

export interface MemoryItem {
  id: string;
  kind: string;
  content: string;
  subject?: string | null;
  source: string;
  confidence: number;
  tags?: string | null;
  createdAt: string;
  updatedAt: string;
  expiresAt?: string | null;
  lastUsedAt?: string | null;
  useCount: number;
}

export interface TaskItem {
  id: string;
  title: string;
  notes?: string | null;
  state: string;
  priority: string;
  project?: string | null;
  dueAt?: string | null;
  createdAt: string;
  updatedAt: string;
  completedAt?: string | null;
}

export interface Reminder {
  id: string;
  text: string;
  dueAt: string;
  status: string;
  createdAt: string;
  firedAt?: string | null;
}

export interface NotificationItem {
  id: string;
  title: string;
  body?: string | null;
  priority: string;
  source: string;
  timestamp: string;
  status: string;
}

export interface QueuedAction {
  id: string;
  createdAt: string;
  tool: string;
  args: string;
  summary: string;
  status: string;
}

export interface ToolInfo {
  name: string;
  description: string;
  category: string;
  risk: Risk;
  requiresInternet: boolean;
  policy: "Default" | "Allow" | "Ask" | "Block";
}

export interface Capability {
  area: string;
  name: string;
  status: "Working" | "Partial" | "Foundation" | "Planned";
  notes: string;
  phase: number;
}

export interface StoredMessage {
  id: number;
  conversationId: string;
  role: "user" | "assistant";
  content: string;
  lang?: string | null;
  source?: string | null;
  meta?: string | null;
  createdAt: string;
}

// Settings mirror JarvisSettings (camelCase).
export interface ProviderConfig {
  id: string;
  name: string;
  kind: string;
  baseUrl: string;
  isLocal: boolean;
  enabled: boolean;
  apiKeySecret?: string | null;
}

export interface Settings {
  general: {
    userName: string;
    honorific: string;
    honorificAr: string;
    language: "auto" | "en" | "ar";
    conversationTimeoutMinutes: number;
    startWithWindows: boolean;
    launchDesktopOnStart: boolean;
    showOrb: boolean;
  };
  ai: {
    allowCloud: boolean;
    providers: ProviderConfig[];
    roles: Record<string, { provider: string; model: string }[]>;
    maxAgentSteps: number;
    requestTimeoutSeconds: number;
  };
  voice: {
    ttsEnabled: boolean;
    speakOnlyForVoiceInput: boolean;
    voiceEn: string;
    voiceAr: string;
    rate: number;
    sttModel: string;
    wakeWordEnabled: boolean;
    wakeWords: string[];
    followUpSeconds: number;
    maxUtteranceSeconds: number;
    vadSensitivity: number;
    inputDeviceIndex: number;
  };
  permissions: { autoApproveSensitive: boolean; toolOverrides: Record<string, string>; approvalTimeoutSeconds: number };
  files: { allowedRoots: string[]; maxReadBytes: number };
  memory: { enabled: boolean; storeConversations: boolean; conversationRetentionDays: number; allowedKinds: string[]; learnPatterns: boolean };
  notifications: {
    toastsEnabled: boolean;
    speakImportant: boolean;
    holdDuringMeetings: boolean;
    holdDuringFullscreen: boolean;
    quietHoursStart: string;
    quietHoursEnd: string;
  };
  security: { pinHash: string; unlockMinutes: number };
  runtime: { port: number; connectivityProbeUrl: string; connectivityProbeSeconds: number };
}

export interface ProviderStatus {
  providerId: string;
  available: boolean;
  message: string;
  models: string[];
  checkedAt: string;
}
