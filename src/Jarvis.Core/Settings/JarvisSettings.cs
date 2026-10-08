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
            Id = "ollama", Name = "Ollama (local)", Kind = ProviderKinds.OpenAiCompatible,
            BaseUrl = "http://127.0.0.1:11434/v1", IsLocal = true, Enabled = true,
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
}

public static class ProviderKinds
{
    public const string OpenAiCompatible = "openai-compatible";
    public const string Anthropic = "anthropic";
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
