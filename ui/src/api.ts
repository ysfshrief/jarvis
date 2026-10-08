// Thin client for the JARVIS runtime's local API. Every request carries the runtime token,
// which the desktop shell passes in the URL fragment (#token=...) and we keep in sessionStorage.

const TOKEN_KEY = "jarvis.token";

export function initToken(): string | null {
  // The desktop shell opens "#token=...&page=assistant"; keep the token, then route to the page.
  const raw = window.location.hash.replace(/^#/, "");
  if (!raw.startsWith("/")) {
    const params = new URLSearchParams(raw);
    const fromHash = params.get("token");
    if (fromHash) {
      sessionStorage.setItem(TOKEN_KEY, fromHash);
      const page = params.get("page");
      history.replaceState(null, "", window.location.pathname + window.location.search + (page ? `#/${page}` : ""));
    }
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

export interface UsedMemory {
  id: string;
  kind: string;
  source: string;
  content: string;
}

export interface TurnResult {
  conversationId: string;
  turnId?: string;
  fallbackFrom?: string | null;
  contextTrimmed?: boolean;
  usedMemories?: UsedMemory[];
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
  honorificAr?: string;
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
    localContextTokens: number;
    streamResponses: boolean;
    embeddingModel: string;
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
  appearance: Appearance;
  sounds: SoundSettings;
  shortcuts: { commandConsole: string; pushToTalk: string; dashboard: string };
  privacy: { allowScreenCapture: boolean };
}

export interface Appearance {
  theme: "dark" | "light" | "auto";
  accent: "cyan" | "amber" | "violet" | "green";
  motion: "full" | "reduced" | "off";
  hudEffects: boolean;
  density: "comfortable" | "compact";
  textScale: number;
  orbSize: number;
  contextPanel: boolean;
  language: "en" | "ar";
}

export interface SoundSettings {
  enabled: boolean;
  volume: number;
  wake: boolean;
  accepted: boolean;
  processing: boolean;
  completed: boolean;
  warning: boolean;
  error: boolean;
  notification: boolean;
}

export interface MetricsSample {
  timestamp: string;
  cpuPercent?: number | null;
  memoryPercent?: number | null;
  memoryUsedGb?: number | null;
  memoryTotalGb?: number | null;
  gpuPercent?: number | null;
  netDownBytesPerSec: number;
  netUpBytesPerSec: number;
  batteryPercent?: number | null;
  charging?: boolean | null;
  temperatureC?: number | null;
  runtimeMemoryMb: number;
  runtimeCpuPercent: number;
}

export interface MetricsResponse {
  source: string;
  current: MetricsSample;
  history: MetricsSample[];
}

export interface DiskInfo { name: string; label: string; format: string; totalGb: number; freeGb: number; usedPercent: number }
export interface ProcessInfo { pid: number; name: string; memoryMb: number; cpuPercent?: number | null }

export interface ModelInfo {
  name: string;
  family?: string | null;
  parameterSize?: string | null;
  quantization?: string | null;
  sizeBytes?: number | null;
  contextLength?: number | null;
  capabilities: string[];
  capabilitiesReported: boolean;
}

export interface ModelsResponse {
  providers: { provider: string; name: string; reachable: boolean; canPull: boolean; models: ModelInfo[]; error?: string }[];
  recommended: { name: string; purpose: string; size: string; notes: string }[];
  pulls: PullState[];
}

export interface PullState { providerId: string; model: string; status: string; completed?: number | null; total?: number | null; done: boolean; error?: string | null }

export interface ProviderStatus {
  providerId: string;
  available: boolean;
  message: string;
  models: string[];
  checkedAt: string;
}
