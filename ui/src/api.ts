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
  recording?: { id: string; title: string; startedAt: string; source: string } | null;
  recordingBlocker?: string | null;
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

export interface MemoryProvenance { via: string; conversationId?: string | null; turnId?: string | null; quote?: string | null; reason?: string | null; tool?: string | null }

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
  provenance?: MemoryProvenance | null;
  confirmedAt?: string | null;
  isConfirmed: boolean;
  score?: number | null;
  semantic?: boolean | null;
  entities: { id: string; name: string; type: string }[];
}

export interface Entity { id: string; type: string; name: string; aliases: string[]; notes?: string | null; source: string; updatedAt: string; memories?: number }
export interface Relation { id: string; fromId: string; fromName: string; type: string; toId: string; toName: string; source: string; confidence: number; createdAt: string }
export interface EntityProfile { entity: Entity; memories: MemoryItem[]; relations: Relation[]; tasks: TaskItem[]; reminders: Reminder[]; files: IndexedFile[] }
export interface MemoryStatus {
  total: number; confirmed: number; inferred: number; entities: number; learning: boolean; enabled: boolean;
  semantic: { available: boolean; model?: string | null; provider?: string | null; indexed: number; total: number; message: string };
}

export interface TaskItem {
  recurrence?: string | null;
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
  calendar: { reminderMinutes: number; syncMinutes: number };
  inbox: { syncMinutes: number; initialDays: number; notifyUrgent: boolean; vipSenders: string[] };
  web: { browserEnabled: boolean; browserPath: string; headless: boolean; allowLocalPages: boolean };
  files: { allowedRoots: string[]; maxReadBytes: number; indexEnabled: boolean; indexRoots: string[]; indexMaxFileMb: number };
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
  privacy: { allowScreenCapture: boolean; allowCamera: boolean };
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

export interface WorkflowStep {
  id: string; workflowId: string; order: number; title: string; status: string; dependsOn: string[];
  dueAt?: string | null; waitingFor?: string | null; followUpAt?: string | null; requiresApproval: boolean;
  action?: { tool: string; args?: unknown } | null; notes?: string | null; createdAt: string; updatedAt: string; completedAt?: string | null; ready: boolean;
}
export interface Workflow {
  id: string; title: string; goal?: string | null; template: string; status: string; entityId?: string | null; entityName?: string | null;
  dueAt?: string | null; recurrence?: string | null; createdAt: string; updatedAt: string; completedAt?: string | null;
  steps: WorkflowStep[]; doneCount: number; progress: number; next?: WorkflowStep | null; isOpen: boolean;
}
export interface WorkflowEvent { id: number; workflowId: string; stepId?: string | null; timestamp: string; kind: string; text: string }
export interface WorkflowTemplate { id: string; name: string; description: string; steps: string[]; entityType: string }

export interface IndexedFile {
  id: string; path: string; name: string; ext: string; kind: string; size: number; modifiedAt: string; createdAt?: string | null; indexedAt: string;
  status: string; note?: string | null; title?: string | null; author?: string | null; pages?: number | null; project?: string | null; textChars: number;
  metadata: Record<string, string>;
}
export interface FileHit { file: IndexedFile; snippet?: string | null; score: number; semantic: boolean }
export interface IndexProgress { running: boolean; root?: string | null; scanned: number; indexed: number; skipped: number; errors: number; current?: string | null; startedAt?: string | null; finishedAt?: string | null }
export interface SemanticStatus { available: boolean; model?: string | null; provider?: string | null; indexed: number; total: number; message: string }
export interface FilesStatus {
  enabled: boolean; roots: string[]; files: number; withText: number; chars: number; lastIndexed?: string | null;
  kinds: Record<string, number>; progress: IndexProgress; ocr: { isAvailable: boolean; name: string }; semantic: SemanticStatus;
}
export interface FileDetail { file: IndexedFile; entities: Entity[]; keyPoints: string[]; preview: string; previous?: IndexedFile | null }
export interface FileComparison {
  older: IndexedFile; newer: IndexedFile; added: number; removed: number; unchanged: number; identical: boolean; addedLines: string[]; removedLines: string[];
}
export interface BrowserStatus { enabled: boolean; browserPath?: string | null; running: boolean; url?: string | null }
export type MailCategory = "urgent" | "important" | "needs_response" | "fyi" | "noise";
export interface MailAccountConfig { imapHost: string; imapPort: number; imapSecurity: string; smtpHost: string; smtpPort: number; smtpSecurity: string; username: string; folder: string }
export interface MailAccount { id: string; kind: string; address: string; displayName?: string | null; enabled: boolean; status: string; statusMessage?: string | null; lastSync?: string | null; config: MailAccountConfig }
export interface MailPreset { id: string; name: string; config: MailAccountConfig; note: string }
export interface ConnectorInfo { id: string; name: string; status: string; how: string; note: string }
export interface InboxStatus { accounts: MailAccount[]; counts: Record<MailCategory, number>; drafts: number; presets: MailPreset[]; connectors: ConnectorInfo[] }
export interface InboxMessage {
  id: string; accountId: string; fromName?: string | null; fromAddress: string; to: string[]; cc: string[]; subject: string; snippet: string; body: string;
  receivedAt: string; isRead: boolean; bulk: boolean; category: MailCategory; categorySource: string; reason?: string | null; handled: boolean;
}
export interface MailDraft {
  id: string; accountId: string; replyToId?: string | null; to: string[]; cc: string[]; subject: string; body: string; status: string; createdBy: string;
  createdAt: string; updatedAt: string; sentAt?: string | null; error?: string | null;
}
export interface MessageDetail { message: InboxMessage; entities: Entity[]; drafts: MailDraft[]; fromSender: InboxMessage[] }
export interface AgendaCalendar { id: string; name: string; kind: string; color?: string | null; enabled: boolean; status: string; statusMessage?: string | null; lastSync?: string | null }
export interface Attendee { name?: string | null; email?: string | null }
export interface AgendaEvent {
  id: string; calendarId: string; uid: string; title: string; start: string; end: string; allDay: boolean; location?: string | null; description?: string | null;
  organizer?: Attendee | null; attendees: Attendee[]; source: string; reminded: boolean;
}
export interface MeetingPrep {
  text: string; people: { name: string; known?: string | null; entityId?: string | null; facts: string[] }[]; workflows: { id: string; title: string }[];
  tasks: { id: string; title: string }[]; mail: { id: string; sender: string; subject: string; receivedAt: string }[]; files: { id: string; name: string }[];
}
export interface ActionItem { text: string; owner?: string | null; due?: string | null }
export interface MeetingNotes { keyPoints: string[]; decisions: string[]; actionItems: ActionItem[]; openQuestions: string[] }
export interface Meeting { id: string; title: string; eventId?: string | null; startedAt: string; endedAt?: string | null; status: string; transcript: string; notes?: MeetingNotes | null; audioSeconds: number; error?: string | null }
export interface MeetingSummary { id: string; title: string; startedAt: string; endedAt?: string | null; status: string; audioSeconds: number; error?: string | null; actionItems: number; decisions: number }
export interface PluginTest { tool: string; passed: boolean; detail: string; log: string[]; skipped: boolean }
export interface PluginReport { problems: string[]; tests: PluginTest[]; at: string; networkTested: boolean }
export interface PluginView {
  id: string; status: string; source: string; code: string; report?: PluginReport | null; error?: string | null; createdAt: string; installedAt?: string | null;
  permissions: string;
  manifest: { id: string; name: string; version: string; description: string; author?: string | null; permissions: { http: string[]; httpSend: string[]; notify: boolean; storage: boolean } };
  tools: { name: string; toolName: string; risk: string; description: string }[];
}
