using System.Text.Json.Serialization;

namespace Jarvis.Core.Settings;

/// <summary>
/// User-editable configuration, stored as JSON in the data folder. Secrets (API keys)
/// are never stored here; see <see cref="Security.ISecretStore"/>.
/// </summary>
public sealed class JarvisSettings
{
    public GeneralSettings General { get; set; } = new();
    public AiSettings Ai { get; set; } = new();
    public VoiceSettings Voice { get; set; } = new();
    public PermissionSettings Permissions { get; set; } = new();
    public FileSettings Files { get; set; } = new();
    public MemorySettings Memory { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public SecuritySettings Security { get; set; } = new();
    public RuntimeSettings Runtime { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public SoundSettings Sounds { get; set; } = new();
    public ShortcutSettings Shortcuts { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
    public WebSettings Web { get; set; } = new();
    public InboxSettings Inbox { get; set; } = new();
    public CalendarSettings Calendar { get; set; } = new();
    public MeetingSettings Meetings { get; set; } = new();
    public CompanionSettings Companion { get; set; } = new();
}

public sealed class CompanionSettings
{
    /// <summary>Let paired phones on your network talk to JARVIS. Off by default: the API is otherwise this-PC-only.</summary>
    public bool Enabled { get; set; }
    public int Port { get; set; } = 47322;
    /// <summary>Paired phones may approve or refuse pending actions.</summary>
    public bool AllowApprovals { get; set; } = true;
}

public sealed class MeetingSettings
{
    /// <summary>Also record what you hear (the other people on a call), not just your microphone.</summary>
    public bool CaptureSystemAudio { get; set; } = true;
    /// <summary>Recording stops by itself after this long, so it can't be forgotten.</summary>
    public int MaxMinutes { get; set; } = 180;
}

public sealed class CalendarSettings
{
    /// <summary>Minutes before a meeting to remind you (0 = no reminders).</summary>
    public int ReminderMinutes { get; set; } = 10;
    /// <summary>How often subscribed calendars are refreshed.</summary>
    public int SyncMinutes { get; set; } = 15;
}

public sealed class InboxSettings
{
    /// <summary>How often connected mail accounts are checked.</summary>
    public int SyncMinutes { get; set; } = 5;
    /// <summary>How far back the first sync goes.</summary>
    public int InitialDays { get; set; } = 14;
    /// <summary>Notify (through the notification centre) when an urgent message arrives.</summary>
    public bool NotifyUrgent { get; set; } = true;
    /// <summary>Addresses or domains whose mail is always at least important.</summary>
    public List<string> VipSenders { get; set; } = [];
}

public sealed class WebSettings
{
    /// <summary>Let JARVIS drive a browser (its own window and profile, separate from yours).</summary>
    public bool BrowserEnabled { get; set; } = true;
    /// <summary>Browser to drive; empty = Microsoft Edge, then Chrome/Chromium.</summary>
    public string BrowserPath { get; set; } = "";
    /// <summary>Run without a visible window. Off by default so you can always see what JARVIS does.</summary>
    public bool Headless { get; set; }
    /// <summary>Allow pages on this computer or the local network (e.g. a dev server on localhost).</summary>
    public bool AllowLocalPages { get; set; }
}

public sealed class AppearanceSettings
{
    /// <summary>"dark" (default), "light" or "auto" (follow Windows).</summary>
    public string Theme { get; set; } = "dark";
    /// <summary>Energy colour: "cyan" (default), "amber", "violet", "green".</summary>
    public string Accent { get; set; } = "cyan";
    /// <summary>"full", "reduced" (no ambient motion) or "off" (no animation at all).</summary>
    public string Motion { get; set; } = "full";
    /// <summary>Holographic grid and scan-line overlays.</summary>
    public bool HudEffects { get; set; } = true;
    /// <summary>"comfortable" or "compact".</summary>
    public string Density { get; set; } = "comfortable";
    /// <summary>Interface text size multiplier (0.85–1.4).</summary>
    public double TextScale { get; set; } = 1.0;
    /// <summary>Diameter of the floating desktop orb in pixels (48–128).</summary>
    public int OrbSize { get; set; } = 72;
    /// <summary>Show the right-hand context panel on wide screens.</summary>
    public bool ContextPanel { get; set; } = true;
    /// <summary>Interface language: "en" or "ar" (right-to-left).</summary>
    public string Language { get; set; } = "en";
}

public sealed class SoundSettings
{
    /// <summary>Master switch for interface sounds.</summary>
    public bool Enabled { get; set; } = true;
    public double Volume { get; set; } = 0.35;
    public bool Wake { get; set; } = true;
    public bool Accepted { get; set; } = true;
    /// <summary>A soft tick while working. Off by default; it gets old fast.</summary>
    public bool Processing { get; set; }
    public bool Completed { get; set; } = true;
    public bool Warning { get; set; } = true;
    public bool Error { get; set; } = true;
    public bool Notification { get; set; } = true;
}

public sealed class ShortcutSettings
{
    /// <summary>Opens the command console. Format: modifiers + key, e.g. "Ctrl+Alt+J".</summary>
    public string CommandConsole { get; set; } = "Ctrl+Alt+J";
    public string PushToTalk { get; set; } = "Ctrl+Alt+Space";
    /// <summary>Opens the dashboard. Empty = no shortcut.</summary>
    public string Dashboard { get; set; } = "";
}

public sealed class PrivacySettings
{
    /// <summary>Allow screenshots (by command or by the AI). Off blocks every screen capture tool.</summary>
    public bool AllowScreenCapture { get; set; } = true;
    /// <summary>Let JARVIS take a single photo with the camera when you ask (it still asks every time). Off by default.</summary>
    public bool AllowCamera { get; set; }
}

public sealed class GeneralSettings
{
    public string UserName { get; set; } = "";
    /// <summary>How JARVIS addresses the user in English ("Sir", a name, or empty).</summary>
    public string Honorific { get; set; } = "Sir";
    /// <summary>How JARVIS addresses the user in Arabic.</summary>
    public string HonorificAr { get; set; } = "يا فندم";
    /// <summary>"auto" mirrors the user's language; "en" or "ar" forces one.</summary>
    public string Language { get; set; } = "auto";
    /// <summary>Minutes of silence after which a conversation's short-term context resets.</summary>
    public int ConversationTimeoutMinutes { get; set; } = 15;
    public bool StartWithWindows { get; set; } = true;
    public bool LaunchDesktopOnStart { get; set; } = true;
    public bool ShowOrb { get; set; } = true;
}

public sealed class AiSettings
{
    /// <summary>Allow requests to leave this machine. Off by default (local-first).</summary>
    public bool AllowCloud { get; set; }
    public List<ProviderConfig> Providers { get; set; } =
    [
        new()
        {
            Id = "ollama", Name = "Ollama (local)", Kind = ProviderKinds.Ollama,
            BaseUrl = "http://127.0.0.1:11434", IsLocal = true, Enabled = true,
        },
        new()
        {
            Id = "lmstudio", Name = "LM Studio (local)", Kind = ProviderKinds.OpenAiCompatible,
            BaseUrl = "http://127.0.0.1:1234/v1", IsLocal = true, Enabled = false,
        },
        new()
        {
            Id = "anthropic", Name = "Anthropic Claude (cloud, your API key)", Kind = ProviderKinds.Anthropic,
            BaseUrl = "", IsLocal = false, Enabled = false, ApiKeySecret = "anthropic_api_key",
        },
        new()
        {
            Id = "openai-compatible-cloud", Name = "OpenAI-compatible cloud (your API key)", Kind = ProviderKinds.OpenAiCompatible,
            BaseUrl = "https://api.openai.com/v1", IsLocal = false, Enabled = false, ApiKeySecret = "openai_api_key",
        },
    ];

    /// <summary>
    /// Which provider/model handles each kind of work. An empty model means
    /// "the first model the provider reports". Roles fall back to "general".
    /// </summary>
    public Dictionary<string, List<RoleBinding>> Roles { get; set; } = new()
    {
        [ModelRoles.General] = [new() { Provider = "ollama" }, new() { Provider = "lmstudio" }, new() { Provider = "anthropic", Model = "claude-opus-5-5" }],
        [ModelRoles.Reasoning] = [new() { Provider = "anthropic", Model = "claude-opus-5-5" }],
        [ModelRoles.Coding] = [new() { Provider = "anthropic", Model = "claude-opus-5-5" }],
        [ModelRoles.Vision] = [],
    };

    /// <summary>Maximum tool-calling rounds per request before JARVIS stops and reports.</summary>
    public int MaxAgentSteps { get; set; } = 8;
    public int RequestTimeoutSeconds { get; set; } = 120;
    /// <summary>Context window requested from local models. Larger is slower and uses more memory.</summary>
    public int LocalContextTokens { get; set; } = 8192;
    /// <summary>Show replies word by word as the model writes them.</summary>
    public bool StreamResponses { get; set; } = true;
    /// <summary>Model used for semantic memory/file search (Ollama). Empty disables embeddings.</summary>
    public string EmbeddingModel { get; set; } = "bge-m3";
}

public static class ProviderKinds
{
    public const string OpenAiCompatible = "openai-compatible";
    public const string Anthropic = "anthropic";
    /// <summary>Ollama's native API: model capabilities, context sizing, downloads, embeddings.</summary>
    public const string Ollama = "ollama";
}

public static class ModelRoles
{
    public const string General = "general";
    public const string Reasoning = "reasoning";
    public const string Coding = "coding";
    public const string Vision = "vision";
}

public sealed class ProviderConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = ProviderKinds.OpenAiCompatible;
    public string BaseUrl { get; set; } = "";
    public bool IsLocal { get; set; }
    public bool Enabled { get; set; }
    /// <summary>Name of the entry in the encrypted secret store holding the API key.</summary>
    public string? ApiKeySecret { get; set; }
}

public sealed class RoleBinding
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
}

public sealed class VoiceSettings
{
    public bool TtsEnabled { get; set; } = true;
    /// <summary>Speak replies only when the request came in by voice.</summary>
    public bool SpeakOnlyForVoiceInput { get; set; } = true;
    public string VoiceEn { get; set; } = "";
    public string VoiceAr { get; set; } = "";
    public double Rate { get; set; } = 1.0;
    /// <summary>Whisper model: tiny, base, small, medium. "small" is noticeably better for Arabic.</summary>
    public string SttModel { get; set; } = "base";
    /// <summary>Always-on wake word listening. Off by default for privacy.</summary>
    public bool WakeWordEnabled { get; set; }
    public List<string> WakeWords { get; set; } = ["jarvis", "جارفيس"];
    /// <summary>After JARVIS answers a voice request, keep listening this long for a follow-up without the wake word.</summary>
    public int FollowUpSeconds { get; set; } = 6;
    public int MaxUtteranceSeconds { get; set; } = 15;
    /// <summary>Energy threshold multiplier over the measured noise floor for speech detection.</summary>
    public double VadSensitivity { get; set; } = 3.0;
    public int InputDeviceIndex { get; set; } = -1;
}

public sealed class PermissionSettings
{
    /// <summary>Run sensitive actions without asking. Critical actions always ask.</summary>
    public bool AutoApproveSensitive { get; set; }
    public Dictionary<string, ToolPolicy> ToolOverrides { get; set; } = new();
    public int ApprovalTimeoutSeconds { get; set; } = 180;
}

[JsonConverter(typeof(JsonStringEnumConverter<ToolPolicy>))]
public enum ToolPolicy { Default, Allow, Ask, Block }

public sealed class FileSettings
{
    /// <summary>Folders JARVIS may read/search without extra confirmation. Empty = your user profile.</summary>
    public List<string> AllowedRoots { get; set; } = [];
    public int MaxReadBytes { get; set; } = 200_000;
    /// <summary>Build a local, searchable index of document contents. Off until the user turns it on.</summary>
    public bool IndexEnabled { get; set; }
    /// <summary>Folders to index. Empty = Documents, Desktop and Downloads.</summary>
    public List<string> IndexRoots { get; set; } = [];
    /// <summary>Files larger than this are indexed by name only.</summary>
    public int IndexMaxFileMb { get; set; } = 25;
}

public sealed class MemorySettings
{
    public bool Enabled { get; set; } = true;
    public bool StoreConversations { get; set; } = true;
    public int ConversationRetentionDays { get; set; } = 90;
    /// <summary>Kinds of memory JARVIS may store. Remove a kind to stop JARVIS remembering it.</summary>
    public List<string> AllowedKinds { get; set; } = ["fact", "preference", "person", "project", "context", "pattern"];
    /// <summary>Let JARVIS infer patterns from behaviour (stored as unconfirmed, low confidence).</summary>
    public bool LearnPatterns { get; set; }
    /// <summary>Learn how the user writes from email they sent (opt-in). Samples are deleted when turned off.</summary>
    public bool LearnWritingStyle { get; set; }
}

public sealed class NotificationSettings
{
    public bool ToastsEnabled { get; set; } = true;
    public bool SpeakImportant { get; set; } = true;
    public bool HoldDuringMeetings { get; set; } = true;
    public bool HoldDuringFullscreen { get; set; } = true;
    /// <summary>Quiet hours, 24h "HH:mm". Empty disables.</summary>
    public string QuietHoursStart { get; set; } = "";
    public string QuietHoursEnd { get; set; } = "";
}

public sealed class SecuritySettings
{
    /// <summary>PBKDF2 hash of the dashboard PIN. Empty means no PIN.</summary>
    public string PinHash { get; set; } = "";
    public int UnlockMinutes { get; set; } = 30;
}

public sealed class RuntimeSettings
{
    public int Port { get; set; } = 47321;
    /// <summary>URL used to check internet connectivity.</summary>
    public string ConnectivityProbeUrl { get; set; } = "http://www.msftconnecttest.com/connecttest.txt";
    public int ConnectivityProbeSeconds { get; set; } = 30;
}
