using System.Collections.Concurrent;
using Jarvis.Core.Activity;
using Jarvis.Core.Events;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.AI;

/// <summary>A free local model JARVIS knows works well for a job, with honest size/quality notes.</summary>
public sealed record RecommendedModel(string Name, string Purpose, string Size, string Notes);

public sealed record ModelPullState(string ProviderId, string Model, string Status, long? Completed, long? Total, bool Done, string? Error);

/// <summary>Lists installed local models with their capabilities and downloads new ones on request.</summary>
public sealed class ModelManager(ProviderRegistry providers, IEventBus events, ActivityLog activity, ILogger<ModelManager> logger)
{
    public static readonly IReadOnlyList<RecommendedModel> Recommended =
    [
        new("qwen2.5:7b", "Conversation + tools (recommended)", "4.7 GB", "Best free all-rounder for English and Egyptian Arabic; reliable tool calling. 8 GB RAM or a 6 GB GPU."),
        new("qwen2.5:3b", "Conversation + tools (light)", "1.9 GB", "For laptops with 8 GB RAM. Weaker Arabic and planning."),
        new("llama3.2:3b", "Conversation + tools (light)", "2.0 GB", "Fast English assistant; Arabic is limited."),
        new("qwen2.5vl:7b", "Vision", "6.0 GB", "Reads screenshots, documents and UI. Used for screen understanding."),
        new("bge-m3", "Embeddings (semantic search)", "1.2 GB", "Multilingual (good Arabic) — powers meaning-based memory and file search."),
    ];

    private readonly ConcurrentDictionary<string, ModelPullState> _pulls = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ModelPullState> Pulls => _pulls.Values.ToList();

    public async Task<IReadOnlyList<object>> ListAsync(CancellationToken ct)
    {
        var result = new List<object>();
        foreach (var (id, p) in providers.Providers)
        {
            if (p is IModelCatalog catalog)
            {
                try
                {
                    var models = await catalog.ListModelsAsync(ct).ConfigureAwait(false);
                    result.Add(new { provider = id, name = p.Name, reachable = true, canPull = p is IModelPuller, models });
                }
                catch (Exception ex) when (ex is HttpRequestException or AiProviderException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    result.Add(new { provider = id, name = p.Name, reachable = false, canPull = p is IModelPuller, models = Array.Empty<ModelInfo>(), error = ex.Message });
                }
            }
            else
            {
                var st = providers.CachedStatus(id) ?? await providers.CheckAsync(id, ct).ConfigureAwait(false);
                var models = st.Models.Select(n => new ModelInfo { Name = n, Capabilities = OllamaProvider.GuessCapabilities(n, null) }).ToList();
                result.Add(new { provider = id, name = p.Name, reachable = st.Available || st.Models.Count > 0, canPull = false, models });
            }
        }
        return result;
    }

    /// <summary>Starts a download in the background; progress arrives as <see cref="EventTypes.ModelPull"/> events.</summary>
    public bool StartPull(string providerId, string model, out string? error)
    {
        error = null;
        if (providers.Get(providerId) is not IModelPuller puller) { error = "This provider can't download models."; return false; }
        if (string.IsNullOrWhiteSpace(model) || model.Length > 200 || model.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '\\'))
        {
            error = "That isn't a valid model name.";
            return false;
        }
        var key = $"{providerId}/{model}";
        if (_pulls.TryGetValue(key, out var existing) && !existing.Done) { error = "Already downloading."; return false; }

        Update(new ModelPullState(providerId, model, "starting", null, null, false, null));
        activity.Record(ActivityKinds.Ai, $"Downloading model {model}", status: "started");
        _ = Task.Run(async () =>
        {
            var lastPublish = DateTimeOffset.MinValue;
            var progress = new Progress<PullProgress>(p =>
            {
                // A few updates per second is plenty for a progress bar.
                if (DateTimeOffset.Now - lastPublish < TimeSpan.FromMilliseconds(400) && p.Status != "success") return;
                lastPublish = DateTimeOffset.Now;
                Update(new ModelPullState(providerId, model, p.Status, p.Completed, p.Total, false, null));
            });
            try
            {
                await puller.PullAsync(model, progress, CancellationToken.None).ConfigureAwait(false);
                Update(new ModelPullState(providerId, model, "success", null, null, true, null));
                activity.Record(ActivityKinds.Ai, $"Model {model} downloaded", status: "ok");
                await providers.CheckAsync(providerId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Model pull failed for {Model}", model);
                Update(new ModelPullState(providerId, model, "failed", null, null, true, ex.Message));
                activity.Record(ActivityKinds.Ai, $"Model {model} download failed", status: "failed", details: ex.Message);
            }
        });
        return true;
    }

    private void Update(ModelPullState state)
    {
        _pulls[$"{state.ProviderId}/{state.Model}"] = state;
        events.Publish(EventTypes.ModelPull, state);
    }
}
