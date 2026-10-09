using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Settings;

public interface ISettingsStore
{
    /// <summary>The current settings. Treat as read-only; change through <see cref="Update"/>.</summary>
    JarvisSettings Current { get; }
    void Update(Action<JarvisSettings> change);
    void Replace(JarvisSettings settings);
    event Action<JarvisSettings>? Changed;
}

public sealed class SettingsStore : ISettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly ILogger<SettingsStore> _logger;
    private readonly object _gate = new();
    private JarvisSettings _current;

    public SettingsStore(JarvisPaths paths, ILogger<SettingsStore> logger)
    {
        _path = paths.SettingsPath;
        _logger = logger;
        _current = Load();
    }

    public JarvisSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public event Action<JarvisSettings>? Changed;

    public void Update(Action<JarvisSettings> change)
    {
        JarvisSettings next;
        lock (_gate)
        {
            next = Clone(_current);
            change(next);
            Normalize(next);
            Save(next);
            _current = next;
        }
        Changed?.Invoke(next);
    }

    public void Replace(JarvisSettings settings)
    {
        var next = Clone(settings);
        Normalize(next);
        lock (_gate)
        {
            Save(next);
            _current = next;
        }
        Changed?.Invoke(next);
    }

    public static JarvisSettings Clone(JarvisSettings s) =>
        JsonSerializer.Deserialize<JarvisSettings>(JsonSerializer.Serialize(s, JsonOptions), JsonOptions)!;

    private JarvisSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<JarvisSettings>(File.ReadAllText(_path), JsonOptions);
                if (loaded is not null)
                {
                    Normalize(loaded);
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            // A corrupt settings file must not stop JARVIS from starting. Keep a copy for inspection.
            _logger.LogError(ex, "Settings file is unreadable; starting with defaults");
            TryBackupCorrupt();
        }

        var defaults = new JarvisSettings();
        Save(defaults);
        return defaults;
    }

    private void Save(JarvisSettings settings)
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }

    private void TryBackupCorrupt()
    {
        try { File.Copy(_path, _path + ".corrupt", overwrite: true); }
        catch { /* best effort */ }
    }

    private static void Normalize(JarvisSettings s)
    {
        s.General ??= new();
        s.Ai ??= new();
        s.Ai.Providers ??= [];
        s.Ai.Roles ??= new();
        s.Voice ??= new();
        s.Voice.WakeWords ??= [];
        s.Permissions ??= new();
        s.Permissions.ToolOverrides ??= new();
        s.Files ??= new();
        s.Files.AllowedRoots ??= [];
        s.Files.IndexRoots ??= [];
        s.Files.IndexMaxFileMb = Math.Clamp(s.Files.IndexMaxFileMb, 1, 500);
        s.Memory ??= new();
        s.Memory.AllowedKinds ??= [];
        s.Notifications ??= new();
        s.Security ??= new();
        s.Runtime ??= new();
        s.Appearance ??= new();
        s.Sounds ??= new();
        s.Shortcuts ??= new();
        s.Privacy ??= new();
        s.Web ??= new();
        s.Web.BrowserPath ??= "";
        if (s.Appearance.Theme is not ("dark" or "light" or "auto")) s.Appearance.Theme = "dark";
        if (s.Appearance.Accent is not ("cyan" or "amber" or "violet" or "green")) s.Appearance.Accent = "cyan";
        if (s.Appearance.Motion is not ("full" or "reduced" or "off")) s.Appearance.Motion = "full";
        if (s.Appearance.Density is not ("comfortable" or "compact")) s.Appearance.Density = "comfortable";
        if (s.Appearance.Language is not ("en" or "ar")) s.Appearance.Language = "en";
        s.Appearance.TextScale = Math.Clamp(s.Appearance.TextScale, 0.85, 1.4);
        s.Appearance.OrbSize = Math.Clamp(s.Appearance.OrbSize, 48, 128);
        s.Sounds.Volume = Math.Clamp(s.Sounds.Volume, 0, 1);
        s.Shortcuts.CommandConsole ??= "";
        s.Shortcuts.PushToTalk ??= "";
        s.Shortcuts.Dashboard ??= "";

        s.General.ConversationTimeoutMinutes = Math.Clamp(s.General.ConversationTimeoutMinutes, 1, 24 * 60);
        s.Ai.MaxAgentSteps = Math.Clamp(s.Ai.MaxAgentSteps, 1, 30);
        s.Ai.RequestTimeoutSeconds = Math.Clamp(s.Ai.RequestTimeoutSeconds, 10, 900);
        s.Ai.LocalContextTokens = Math.Clamp(s.Ai.LocalContextTokens, 2048, 131072);
        s.Ai.EmbeddingModel ??= "";
        // v0.1 talked to Ollama through its OpenAI-compatible endpoint; the native API knows more.
        foreach (var p in s.Ai.Providers)
        {
            if (p.Id == "ollama" && p.Kind == ProviderKinds.OpenAiCompatible && (p.BaseUrl ?? "").Contains(":11434"))
            {
                p.Kind = ProviderKinds.Ollama;
                p.BaseUrl = p.BaseUrl!.TrimEnd('/');
                if (p.BaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) p.BaseUrl = p.BaseUrl[..^3];
            }
        }
        s.Voice.FollowUpSeconds = Math.Clamp(s.Voice.FollowUpSeconds, 0, 60);
        s.Voice.MaxUtteranceSeconds = Math.Clamp(s.Voice.MaxUtteranceSeconds, 3, 60);
        s.Voice.VadSensitivity = Math.Clamp(s.Voice.VadSensitivity, 1.2, 20);
        s.Voice.Rate = Math.Clamp(s.Voice.Rate, 0.5, 2.0);
        s.Permissions.ApprovalTimeoutSeconds = Math.Clamp(s.Permissions.ApprovalTimeoutSeconds, 15, 3600);
        s.Runtime.Port = Math.Clamp(s.Runtime.Port, 1024, 65535);
        s.Runtime.ConnectivityProbeSeconds = Math.Clamp(s.Runtime.ConnectivityProbeSeconds, 5, 3600);
        if (s.General.Language is not ("auto" or "en" or "ar")) s.General.Language = "auto";
    }
}
