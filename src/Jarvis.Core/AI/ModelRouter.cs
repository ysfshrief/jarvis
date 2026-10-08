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
    private readonly Events.IEventBus? _events;
    private readonly ConcurrentDictionary<string, ProviderStatus> _status = new();
    private IReadOnlyDictionary<string, IChatProvider> _providers = new Dictionary<string, IChatProvider>();

    public ProviderRegistry(ISettingsStore settings, ISecretStore secrets, HttpClient http, ILogger<ProviderRegistry> logger, Events.IEventBus? events = null)
    {
        _events = events;
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
        var previous = _status.GetValueOrDefault(id);
        _status[id] = status;
        // Let the UI refresh its "AI ready" indicators when a provider comes or goes.
        if (previous is null || previous.Available != status.Available || previous.Models.Count != status.Models.Count)
            _events?.Publish(Events.EventTypes.AiStatusChanged, new { provider = id, status.Available, models = status.Models.Count, status.Message });
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
            ProviderKinds.Ollama => new OllamaProvider(cfg, _http, _settings.Current.Ai.LocalContextTokens),
            ProviderKinds.Anthropic => new AnthropicProvider(cfg, key),
            _ => null,
        };
    }
}

public sealed record RouteDecision(string Role, IChatProvider? Provider, string? Model, string Reason)
{
    /// <summary>What the chosen model can do, when the provider can tell (Ollama).</summary>
    public ModelInfo? Info { get; init; }
    public bool HasModel => Provider is not null && !string.IsNullOrEmpty(Model);
    public string Label => HasModel ? $"{Provider!.Id}/{Model}" : "none";
    /// <summary>False only when the server says the model can't call tools; unknown counts as yes.</summary>
    public bool SupportsTools => Info is null || Info.Has(ModelCapabilities.Tools);
    public string Key => $"{Provider?.Id}/{Model}";
}

/// <summary>
/// Chooses which model (if any) handles a request. Deterministic commands never reach here.
/// Local providers are preferred; cloud providers are only used when the user allowed cloud AI
/// and the machine is online. When a model fails mid-request the caller asks again with that
/// model excluded, which gives a natural fallback chain (local → other local → cloud if allowed).
/// </summary>
public sealed partial class ModelRouter(ProviderRegistry providers, ISettingsStore settings, IConnectivity connectivity)
{
    /// <summary>Models that are good, free, local assistants — preferred in this order when the user didn't pick one.</summary>
    public static readonly string[] PreferredLocalFamilies = ["qwen3", "qwen2.5", "llama3.1", "llama3.2", "gemma3", "mistral", "phi4", "llama3"];

    public static string Classify(string text)
    {
        if (CodingRegex().IsMatch(text)) return ModelRoles.Coding;
        if (text.Length > 600 || ReasoningRegex().IsMatch(text)) return ModelRoles.Reasoning;
        return ModelRoles.General;
    }

    public Task<RouteDecision> RouteAsync(string text, CancellationToken ct) => RouteAsync(text, ct, null, null);

    public async Task<RouteDecision> RouteAsync(string text, CancellationToken ct, string? role, IReadOnlySet<string>? exclude)
    {
        var s = settings.Current;
        role ??= Classify(text);
        var needVision = role == ModelRoles.Vision;
        var bindings = new List<RoleBinding>();
        if (role != ModelRoles.General && s.Ai.Roles.TryGetValue(role, out var specific)) bindings.AddRange(specific);
        // Vision needs a vision model: general bindings are still tried, but only their vision-capable models.
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

            IReadOnlyList<ModelInfo> catalog = [];
            if (provider is IModelCatalog cat)
            {
                try { catalog = await cat.ListModelsAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HttpRequestException or AiProviderException or System.Text.Json.JsonException) { }
            }

            var model = b.Model;
            if (string.IsNullOrEmpty(model))
            {
                model = catalog.Count > 0
                    ? PickBest(catalog, needVision, exclude, provider.Id)
                    : PickDefaultModel(status.Models.Where(m => exclude?.Contains($"{provider.Id}/{m}") != true).ToList());
            }
            else if (provider.IsLocal && status.Models.Count > 0 && !status.Models.Contains(model, StringComparer.OrdinalIgnoreCase))
            {
                reasons.Add($"{b.Provider}: model '{model}' not installed");
                continue;
            }
            if (string.IsNullOrEmpty(model)) { reasons.Add($"{b.Provider}: {(needVision ? "no vision model" : "no models")}"); continue; }
            if (exclude?.Contains($"{provider.Id}/{model}") == true) { reasons.Add($"{b.Provider}/{model}: failed just now"); continue; }

            var info = catalog.FirstOrDefault(m => m.Name.Equals(model, StringComparison.OrdinalIgnoreCase));
            if (needVision && info is not null && !info.Has(ModelCapabilities.Vision)) { reasons.Add($"{b.Provider}/{model}: can't see images"); continue; }
            if (needVision && info is null && provider.IsLocal) { reasons.Add($"{b.Provider}/{model}: vision support unknown"); continue; }

            return new RouteDecision(role, provider, model, $"{role} → {provider.Name} ({model})") { Info = info };
        }

        return new RouteDecision(role, null, null, reasons.Count == 0 ? "No AI provider is configured." : string.Join("; ", reasons.Distinct()));
    }

    /// <summary>A model call failed: refresh that provider's health so the next route sees it.</summary>
    public async Task ReportFailureAsync(RouteDecision decision, CancellationToken ct)
    {
        if (decision.Provider is null) return;
        try { await providers.CheckAsync(decision.Provider.Id, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// The best installed model for the job: chat models that can call tools first, then the
    /// families known to be good free assistants (and good at Arabic), then by name.
    /// </summary>
    internal static string? PickBest(IReadOnlyList<ModelInfo> models, bool vision, IReadOnlySet<string>? exclude, string providerId) =>
        models
            .Where(m => m.IsChatModel && (!vision || m.Has(ModelCapabilities.Vision)))
            .Where(m => exclude?.Contains($"{providerId}/{m.Name}") != true)
            .OrderByDescending(m => m.Has(ModelCapabilities.Tools))
            .ThenBy(m => FamilyRank(m.Name))
            .ThenBy(m => SizeDistance(m))
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => m.Name)
            .FirstOrDefault();

    private static int FamilyRank(string name)
    {
        var i = Array.FindIndex(PreferredLocalFamilies, f => name.StartsWith(f, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? PreferredLocalFamilies.Length : i;
    }

    /// <summary>
    /// ~7-8B parameters is the sweet spot on a typical PC: good tool use and Arabic, still fast.
    /// Tiny models misuse tools; very large ones are slow without a big GPU.
    /// </summary>
    internal static double SizeDistance(ModelInfo m)
    {
        var billions = ParseBillions(m.ParameterSize) ?? ParseBillions(m.Name.Split(':').ElementAtOrDefault(1));
        return billions is { } b && b > 0 ? Math.Abs(Math.Log(b / 8.0)) : 1.0;
    }

    internal static double? ParseBillions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = SizeRegex().Match(text);
        if (!m.Success) return null;
        var n = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return m.Groups[2].Value.Equals("m", StringComparison.OrdinalIgnoreCase) ? n / 1000 : n;
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*([bm])\b", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

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
