using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Jarvis.Core.Connectivity;
using Jarvis.Core.Security;
using Jarvis.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.AI;

/// <summary>Builds provider clients from settings and caches their health.</summary>
public sealed class ProviderRegistry
{
    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly HttpClient _http;
    private readonly ILogger<ProviderRegistry> _logger;
    private readonly ConcurrentDictionary<string, ProviderStatus> _status = new();
    private IReadOnlyDictionary<string, IChatProvider> _providers = new Dictionary<string, IChatProvider>();

    public ProviderRegistry(ISettingsStore settings, ISecretStore secrets, HttpClient http, ILogger<ProviderRegistry> logger)
    {
        _settings = settings;
        _secrets = secrets;
        _http = http;
        _logger = logger;
        Rebuild(settings.Current);
        settings.Changed += s => { Rebuild(s); _status.Clear(); };
    }

    /// <summary>Lets tests and plugins substitute a provider implementation.</summary>
    public Func<ProviderConfig, IChatProvider?>? Factory { get; set; }

    public IReadOnlyDictionary<string, IChatProvider> Providers => _providers;

    public IChatProvider? Get(string id) => _providers.GetValueOrDefault(id);

    public ProviderStatus? CachedStatus(string id) => _status.GetValueOrDefault(id);

    public async Task<ProviderStatus> CheckAsync(string id, CancellationToken ct)
    {
        var p = Get(id);
        if (p is null) return new ProviderStatus(id, false, "Provider is disabled or unknown.", [], DateTimeOffset.Now);
        ProviderStatus status;
        try { status = await p.CheckAsync(ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider check failed for {Provider}", id);
            status = new ProviderStatus(id, false, ex.Message, [], DateTimeOffset.Now);
        }
        _status[id] = status;
        return status;
    }

    public async Task<IReadOnlyList<ProviderStatus>> CheckAllAsync(CancellationToken ct)
    {
        var tasks = _providers.Keys.Select(id => CheckAsync(id, ct));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private void Rebuild(JarvisSettings s)
    {
        var dict = new Dictionary<string, IChatProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var cfg in s.Ai.Providers.Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Id)))
        {
            var provider = Factory?.Invoke(cfg) ?? Create(cfg);
            if (provider is not null) dict[cfg.Id] = provider;
        }
        _providers = dict;
    }

    private IChatProvider? Create(ProviderConfig cfg)
    {
        Func<string?> key = () => string.IsNullOrEmpty(cfg.ApiKeySecret) ? null : _secrets.Get(cfg.ApiKeySecret);
        return cfg.Kind switch
        {
            ProviderKinds.OpenAiCompatible => new OpenAiCompatibleProvider(cfg, key, _http),
            ProviderKinds.Anthropic => new AnthropicProvider(cfg, key),
            _ => null,
        };
    }
}

public sealed record RouteDecision(string Role, IChatProvider? Provider, string? Model, string Reason)
{
    public bool HasModel => Provider is not null && !string.IsNullOrEmpty(Model);
    public string Label => HasModel ? $"{Provider!.Id}/{Model}" : "none";
}

/// <summary>
/// Chooses which model (if any) handles a request. Deterministic commands never reach here.
/// Local providers are preferred; cloud providers are only used when the user allowed cloud AI
/// and the machine is online.
/// </summary>
public sealed partial class ModelRouter(ProviderRegistry providers, ISettingsStore settings, IConnectivity connectivity)
{
    public static string Classify(string text)
    {
        if (CodingRegex().IsMatch(text)) return ModelRoles.Coding;
        if (text.Length > 600 || ReasoningRegex().IsMatch(text)) return ModelRoles.Reasoning;
        return ModelRoles.General;
    }

    public async Task<RouteDecision> RouteAsync(string text, CancellationToken ct)
    {
        var s = settings.Current;
        var role = Classify(text);
        var bindings = new List<RoleBinding>();
        if (role != ModelRoles.General && s.Ai.Roles.TryGetValue(role, out var specific)) bindings.AddRange(specific);
        if (s.Ai.Roles.TryGetValue(ModelRoles.General, out var general)) bindings.AddRange(general);

        var reasons = new List<string>();
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in bindings)
        {
            if (!tried.Add($"{b.Provider}/{b.Model}")) continue;
            var provider = providers.Get(b.Provider);
            if (provider is null) { reasons.Add($"{b.Provider}: disabled"); continue; }
            if (!provider.IsLocal && !s.Ai.AllowCloud) { reasons.Add($"{b.Provider}: cloud AI is off"); continue; }
            if (!provider.IsLocal && !connectivity.IsOnline) { reasons.Add($"{b.Provider}: offline"); continue; }

            var status = providers.CachedStatus(provider.Id);
            if (status is null || DateTimeOffset.Now - status.CheckedAt > TimeSpan.FromMinutes(2) || !status.Available)
                status = await providers.CheckAsync(provider.Id, ct).ConfigureAwait(false);
            if (!status.Available) { reasons.Add($"{b.Provider}: {status.Message}"); continue; }

            var model = b.Model;
            if (string.IsNullOrEmpty(model))
                model = PickDefaultModel(status.Models);
            else if (provider.IsLocal && status.Models.Count > 0 && !status.Models.Contains(model, StringComparer.OrdinalIgnoreCase))
            {
                reasons.Add($"{b.Provider}: model '{model}' not installed");
                continue;
            }
            if (string.IsNullOrEmpty(model)) { reasons.Add($"{b.Provider}: no models"); continue; }

            return new RouteDecision(role, provider, model, $"{role} → {provider.Name} ({model})");
        }

        return new RouteDecision(role, null, null, reasons.Count == 0 ? "No AI provider is configured." : string.Join("; ", reasons.Distinct()));
    }

    /// <summary>Prefer chat/instruct models over embedding models when the user didn't pick one.</summary>
    internal static string? PickDefaultModel(IReadOnlyList<string> models) =>
        models.FirstOrDefault(m => !m.Contains("embed", StringComparison.OrdinalIgnoreCase) &&
                                   !m.Contains("whisper", StringComparison.OrdinalIgnoreCase))
        ?? models.FirstOrDefault();

    [GeneratedRegex(@"\b(code|coding|build|compile|compiler|error|exception|stack ?trace|bug|debug|function|class|repo|git|npm|yarn|pnpm|dotnet|python|script|refactor|test(s)?|deploy(ment)?)\b|كود|برمج|ايرور|إيرور|بيلد|ديباج", RegexOptions.IgnoreCase)]
    private static partial Regex CodingRegex();

    [GeneratedRegex(@"\b(analy[sz]e|compare|comparison|plan|strategy|research|evaluate|pros and cons|trade-?offs?|why)\b|حلل|قارن|خطه|خطة|استراتيجي|ليه", RegexOptions.IgnoreCase)]
    private static partial Regex ReasoningRegex();
}
