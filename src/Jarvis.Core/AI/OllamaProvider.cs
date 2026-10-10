using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Core.Settings;

namespace Jarvis.Core.AI;

/// <summary>
/// Native Ollama client (free, local). Compared with Ollama's OpenAI-compatible endpoint, the native
/// API lets JARVIS size the context window (num_ctx), read each model's real capabilities (tools,
/// vision, embeddings), stream tokens, keep the model warm, and download models for the user.
/// </summary>
public sealed class OllamaProvider(ProviderConfig config, HttpClient http, int defaultContextTokens = 8192)
    : IChatProvider, IModelCatalog, IModelPuller, IEmbeddingProvider, IModelLoader
{
    private readonly ConcurrentDictionary<string, ModelInfo> _details = new(StringComparer.OrdinalIgnoreCase); // by digest
    private readonly ConcurrentDictionary<string, ModelInfo> _byName = new(StringComparer.OrdinalIgnoreCase);

    public string Id => config.Id;
    public string Name => config.Name;
    public bool IsLocal => config.IsLocal;

    /// <summary>Accepts "http://host:11434", "http://host:11434/" or the OpenAI-style ".../v1".</summary>
    public string BaseUrl
    {
        get
        {
            var url = (string.IsNullOrWhiteSpace(config.BaseUrl) ? "http://127.0.0.1:11434" : config.BaseUrl).TrimEnd('/');
            return url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? url[..^3] : url;
        }
    }

    public async Task<ProviderStatus> CheckAsync(CancellationToken ct)
    {
        try
        {
            var models = await ListModelsAsync(ct).ConfigureAwait(false);
            var chat = models.Where(m => m.IsChatModel).Select(m => m.Name).ToList();
            var msg = models.Count == 0
                ? "Ollama is running, but no models are installed. Pull one in Settings → AI."
                : $"{chat.Count} chat model(s), {models.Count - chat.Count} other.";
            return new ProviderStatus(Id, chat.Count > 0, msg, models.Select(m => m.Name).ToList(), DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or AiProviderException)
        {
            return new ProviderStatus(Id, false, $"Ollama is not reachable at {BaseUrl}. Is it installed and running?", [], DateTimeOffset.Now);
        }
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        using var resp = await http.GetAsync($"{BaseUrl}/api/tags", ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new AiProviderException($"Ollama returned {(int)resp.StatusCode}.", retryable: true);

        var list = new List<ModelInfo>();
        foreach (var m in JsonNode.Parse(text)?["models"]?.AsArray() ?? [])
        {
            var name = m?["name"]?.GetValue<string>() ?? m?["model"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name)) continue;
            var info = await DescribeAsync(name, m!, ct).ConfigureAwait(false);
            list.Add(info);
        }
        return list.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Reads /api/show once per model (capabilities and context length) and caches it.</summary>
    private async Task<ModelInfo> DescribeAsync(string name, JsonNode tag, CancellationToken ct)
    {
        var details = tag["details"];
        var modified = DateTimeOffset.TryParse(tag["modified_at"]?.GetValue<string>(), out var mod) ? mod : (DateTimeOffset?)null;
        var digest = tag["digest"]?.GetValue<string>() ?? name;
        // Two tags of one model ("qwen2.5vl" and "qwen2.5vl:7b") share a digest: reuse what /api/show said, but under this tag's name.
        if (_details.TryGetValue(digest, out var cached)) return _byName[name] = cached.Name == name ? cached : cached with { Name = name };

        IReadOnlyList<string> caps = GuessCapabilities(name, details?["family"]?.GetValue<string>());
        var reported = false;
        int? ctxLen = null;
        try
        {
            using var content = new StringContent(new JsonObject { ["model"] = name }.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync($"{BaseUrl}/api/show", content, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var show = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (show?["capabilities"] is JsonArray capArr && capArr.Count > 0)
                {
                    caps = capArr.Select(c => c?.GetValue<string>() ?? "").Where(c => c.Length > 0).ToList();
                    reported = true;
                }
                if (show?["model_info"] is JsonObject info)
                {
                    foreach (var (key, value) in info)
                    {
                        if (key.EndsWith(".context_length", StringComparison.Ordinal) && value is JsonValue v && v.TryGetValue<long>(out var n))
                            ctxLen = (int)Math.Min(n, int.MaxValue);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException) { /* keep the guess */ }

        var result = new ModelInfo
        {
            Name = name,
            Family = details?["family"]?.GetValue<string>(),
            ParameterSize = details?["parameter_size"]?.GetValue<string>(),
            Quantization = details?["quantization_level"]?.GetValue<string>(),
            SizeBytes = tag["size"]?.GetValue<long>(),
            ModifiedAt = modified,
            ContextLength = ctxLen,
            Capabilities = caps,
            CapabilitiesReported = reported,
        };
        _details[digest] = result;
        _byName[name] = result;
        return result;
    }

    /// <summary>Older Ollama versions don't report capabilities; infer from well-known model names.</summary>
    internal static IReadOnlyList<string> GuessCapabilities(string name, string? family)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("embed") || n.StartsWith("bge") || n.Contains("minilm") || family is "nomic-bert" or "bert")
            return [ModelCapabilities.Embedding];
        var caps = new List<string> { ModelCapabilities.Completion };
        string[] tools = ["qwen2.5", "qwen3", "llama3.1", "llama3.2", "llama3.3", "mistral", "mixtral", "command-r", "hermes", "firefunction", "granite3", "smollm2", "phi4-mini", "gpt-oss"];
        if (tools.Any(n.StartsWith)) caps.Add(ModelCapabilities.Tools);
        string[] vision = ["llava", "bakllava", "moondream", "qwen2.5vl", "qwen2-vl", "llama3.2-vision", "minicpm-v", "gemma3"];
        if (vision.Any(n.StartsWith)) caps.Add(ModelCapabilities.Vision);
        if (n.StartsWith("qwen3") || n.StartsWith("deepseek-r1")) caps.Add(ModelCapabilities.Thinking);
        return caps;
    }

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        var info = _byName.GetValueOrDefault(request.Model);
        var stream = request.OnTextDelta is not null;
        var options = new JsonObject
        {
            ["num_ctx"] = request.ContextTokens ?? Math.Min(info?.ContextLength ?? defaultContextTokens, defaultContextTokens),
            ["num_predict"] = request.MaxTokens,
        };
        if (request.Temperature is { } t) options["temperature"] = t;

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = BuildMessages(request.Messages),
            ["stream"] = stream,
            ["options"] = options,
            ["keep_alive"] = "15m",
        };
        // Thinking models answer faster and more directly for an assistant without visible reasoning.
        if (info?.Has(ModelCapabilities.Thinking) == true) body["think"] = false;
        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(tool => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.ParametersSchema(),
                },
            }).ToArray());
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/chat")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException($"Ollama is not reachable at {BaseUrl}.", retryable: true, ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.BadRequest && request.Tools.Count > 0 && err.Contains("does not support tools", StringComparison.OrdinalIgnoreCase))
                    return await CompleteAsync(request with { Tools = [] }, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.BadRequest && body["think"] is not null && err.Contains("think", StringComparison.OrdinalIgnoreCase))
                {
                    _byName.TryRemove(request.Model, out _);
                    return await CompleteAsync(request, ct).ConfigureAwait(false);
                }
                var detail = TryError(err);
                throw resp.StatusCode == HttpStatusCode.NotFound
                    ? new AiProviderException($"The model \"{request.Model}\" isn't installed in Ollama. Pull it in Settings → AI. {detail}".Trim())
                    : new AiProviderException($"Ollama returned {(int)resp.StatusCode}. {detail}".Trim(), retryable: (int)resp.StatusCode >= 500);
            }

            if (!stream) return Parse(JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)), request.Model);

            // NDJSON stream: content deltas, tool calls in any chunk, usage in the final one.
            var text = new StringBuilder();
            var calls = new List<ToolCall>();
            JsonNode? last = null;
            await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(s, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0) continue;
                request.OnProgress?.Invoke();
                var chunk = JsonNode.Parse(line);
                if (chunk?["error"] is { } e) throw new AiProviderException($"Ollama: {e}");
                var delta = chunk?["message"]?["content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(delta))
                {
                    text.Append(delta);
                    request.OnTextDelta!(delta);
                }
                calls.AddRange(ParseToolCalls(chunk?["message"]?["tool_calls"], calls.Count));
                if (chunk?["done"]?.GetValue<bool>() == true) { last = chunk; break; }
            }
            return new ChatResponse
            {
                Content = text.Length > 0 ? text.ToString() : null,
                ToolCalls = calls,
                FinishReason = last?["done_reason"]?.GetValue<string>(),
                Usage = Usage(last),
                Model = request.Model,
            };
        }
    }

    public async Task<bool> IsLoadedAsync(string model, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync($"{BaseUrl}/api/ps", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return true; // an Ollama without /api/ps: let the chat request load it
            var running = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))?["models"]?.AsArray() ?? [];
            return running.Any(m => (m?["name"]?.GetValue<string>() ?? m?["model"]?.GetValue<string>())?.Equals(model, StringComparison.OrdinalIgnoreCase) == true);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException) { return true; }
    }

    public async Task LoadAsync(string model, int contextTokens, CancellationToken ct)
    {
        // A generate request without a prompt only loads the model; the same num_ctx as the chat keeps it from reloading.
        var body = new JsonObject { ["model"] = model, ["keep_alive"] = "15m", ["options"] = new JsonObject { ["num_ctx"] = contextTokens } };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage resp;
        try { resp = await http.PostAsync($"{BaseUrl}/api/generate", content, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new AiProviderException($"Ollama is not reachable at {BaseUrl}.", retryable: true, ex); }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new AiProviderException($"Ollama couldn't load {model}: {TryError(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))}".Trim(), retryable: (int)resp.StatusCode >= 500);
        }
    }

    public async Task PullAsync(string model, IProgress<PullProgress> progress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/pull")
        {
            Content = new StringContent(new JsonObject { ["model"] = model, ["stream"] = true }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new AiProviderException($"Ollama couldn't pull {model}: {TryError(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))}");
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(s);
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (line.Length == 0) continue;
            var n = JsonNode.Parse(line);
            if (n?["error"] is { } e) throw new AiProviderException($"Ollama couldn't pull {model}: {e}");
            progress.Report(new PullProgress(n?["status"]?.GetValue<string>() ?? "", n?["completed"]?.GetValue<long>(), n?["total"]?.GetValue<long>()));
        }
        _details.Clear();
        _byName.Clear();
    }

    public async Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var body = new JsonObject { ["model"] = model, ["input"] = new JsonArray(inputs.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()) };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync($"{BaseUrl}/api/embed", content, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new AiProviderException($"Ollama embedding failed: {TryError(text)}");
        var arr = JsonNode.Parse(text)?["embeddings"]?.AsArray() ?? [];
        return arr.Select(v => v!.AsArray().Select(x => x!.GetValue<float>()).ToArray()).ToArray();
    }

    internal static JsonArray BuildMessages(IEnumerable<ChatMessage> messages)
    {
        var arr = new JsonArray();
        foreach (var m in messages)
        {
            var o = new JsonObject
            {
                ["role"] = m.Role switch { ChatRole.System => "system", ChatRole.User => "user", ChatRole.Assistant => "assistant", _ => "tool" },
                ["content"] = m.Content ?? "",
            };
            if (m.Role == ChatRole.Tool && m.ToolName is not null) o["tool_name"] = m.ToolName;
            if (m.ToolCalls is { Count: > 0 })
            {
                o["tool_calls"] = new JsonArray(m.ToolCalls.Select(c =>
                {
                    JsonNode args;
                    try { args = JsonNode.Parse(string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson) ?? new JsonObject(); }
                    catch (JsonException) { args = new JsonObject(); }
                    return (JsonNode)new JsonObject { ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = args } };
                }).ToArray());
            }
            if (m.Images is { Count: > 0 })
                o["images"] = new JsonArray(m.Images.Select(i => (JsonNode)JsonValue.Create(Convert.ToBase64String(i))!).ToArray());
            arr.Add(o);
        }
        return arr;
    }

    internal static ChatResponse Parse(JsonNode? root, string model)
    {
        if (root is null) throw new AiProviderException("Empty response from Ollama.");
        var msg = root["message"];
        return new ChatResponse
        {
            Content = msg?["content"]?.GetValue<string>() is { Length: > 0 } c ? c : null,
            ToolCalls = ParseToolCalls(msg?["tool_calls"], 0).ToList(),
            FinishReason = root["done_reason"]?.GetValue<string>(),
            Usage = Usage(root),
            Model = model,
        };
    }

    private static IEnumerable<ToolCall> ParseToolCalls(JsonNode? node, int offset)
    {
        if (node is not JsonArray arr) yield break;
        var i = offset;
        foreach (var c in arr)
        {
            var fn = c?["function"];
            var name = fn?["name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name)) continue;
            var args = fn!["arguments"];
            var json = args is JsonValue v && v.TryGetValue<string>(out var s) ? s : args?.ToJsonString() ?? "{}";
            yield return new ToolCall(c?["id"]?.GetValue<string>() ?? $"call_{i++}", name, json);
        }
    }

    private static ChatUsage? Usage(JsonNode? n) =>
        n?["prompt_eval_count"] is null && n?["eval_count"] is null
            ? null
            : new ChatUsage(n["prompt_eval_count"]?.GetValue<int>() ?? 0, n["eval_count"]?.GetValue<int>() ?? 0);

    private static string TryError(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?.GetValue<string>() ?? body; }
        catch { return body.Length > 200 ? body[..200] : body; }
    }
}
