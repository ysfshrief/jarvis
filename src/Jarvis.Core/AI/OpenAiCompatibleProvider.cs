using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Core.Settings;

namespace Jarvis.Core.AI;

/// <summary>
/// Talks the OpenAI-style /chat/completions protocol. This one client covers the free local
/// servers (Ollama, LM Studio, llama.cpp server, vLLM) and many hosted APIs that offer the
/// same protocol with the user's own key.
/// </summary>
public sealed class OpenAiCompatibleProvider(ProviderConfig config, Func<string?> apiKey, HttpClient http) : IChatProvider
{
    public string Id => config.Id;
    public string Name => config.Name;
    public bool IsLocal => config.IsLocal;

    private string BaseUrl => config.BaseUrl.TrimEnd('/');
    private bool IsGemini => BaseUrl.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase);

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = BuildMessages(request.Messages, IsGemini),
            ["max_tokens"] = request.MaxTokens,
            ["stream"] = request.OnTextDelta is not null,
        };
        if (request.Temperature is { } temp) body["temperature"] = temp;
        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(t =>
            {
                var function = new JsonObject { ["name"] = t.Name, ["description"] = t.Description };
                // A tool without parameters leaves them out: Gemini rejects an object schema with no properties,
                // and for OpenAI-style servers leaving them out means the same thing.
                if (t.Parameters.Count > 0) function["parameters"] = t.ParametersSchema();
                return (JsonNode)new JsonObject { ["type"] = "function", ["function"] = function };
            }).ToArray());
            body["tool_choice"] = "auto";
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        Authorize(req);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, request.OnTextDelta is null ? HttpCompletionOption.ResponseContentRead : HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException($"{Name} is not reachable at {BaseUrl}.", retryable: true, ex);
        }

        using (resp)
        {
            var isStream = resp.IsSuccessStatusCode && request.OnTextDelta is not null &&
                           resp.Content.Headers.ContentType?.MediaType == "text/event-stream";
            if (isStream) return await ReadStreamAsync(resp, request, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Some local models can't call tools (Ollama answers 400 "does not support tools"):
            // fall back to a plain conversation rather than failing the request.
            if (resp.StatusCode == HttpStatusCode.BadRequest && request.Tools.Count > 0 &&
                text.Contains("does not support tools", StringComparison.OrdinalIgnoreCase))
                return await CompleteAsync(request with { Tools = [] }, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw Error(resp.StatusCode, text);
            return Parse(text);
        }
    }

    /// <summary>Server-sent events: "data: {chunk}" lines with content and tool-call fragments.</summary>
    private static async Task<ChatResponse> ReadStreamAsync(HttpResponseMessage resp, ChatRequest request, CancellationToken ct)
    {
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Args, JsonNode? Extra)>();
        string? finish = null, model = null;
        ChatUsage? usage = null;
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(s, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            JsonNode? chunk;
            try { chunk = JsonNode.Parse(data); } catch (JsonException) { continue; }
            model ??= chunk?["model"]?.GetValue<string>();
            if (chunk?["usage"] is JsonObject u)
                usage = new ChatUsage(u["prompt_tokens"]?.GetValue<int>() ?? 0, u["completion_tokens"]?.GetValue<int>() ?? 0);
            var choice = chunk?["choices"]?[0];
            if (choice is null) continue;
            finish = choice["finish_reason"]?.GetValue<string>() ?? finish;
            var delta = choice["delta"];
            if (delta?["content"]?.GetValue<string>() is { Length: > 0 } piece)
            {
                text.Append(piece);
                request.OnTextDelta!(piece);
            }
            if (delta?["tool_calls"] is JsonArray tcs)
            {
                foreach (var tc in tcs)
                {
                    var index = tc?["index"]?.GetValue<int>() ?? calls.Count;
                    if (!calls.TryGetValue(index, out var acc)) acc = (null, null, new StringBuilder(), null);
                    acc.Id ??= tc?["id"]?.GetValue<string>();
                    acc.Extra ??= tc?["extra_content"]?.DeepClone();
                    acc.Name ??= tc?["function"]?["name"]?.GetValue<string>();
                    var argsNode = tc?["function"]?["arguments"];
                    if (argsNode is JsonValue v && v.TryGetValue<string>(out var frag)) acc.Args.Append(frag);
                    else if (argsNode is JsonObject obj) acc.Args.Append(obj.ToJsonString());
                    calls[index] = acc;
                }
            }
        }
        return new ChatResponse
        {
            Content = text.Length > 0 ? text.ToString() : null,
            ToolCalls = calls.Where(c => !string.IsNullOrEmpty(c.Value.Name))
                .Select(c => new ToolCall(c.Value.Id ?? $"call_{c.Key}", c.Value.Name!, c.Value.Args.Length > 0 ? c.Value.Args.ToString() : "{}") { Extra = c.Value.Extra })
                .ToList(),
            FinishReason = finish,
            Usage = usage,
            Model = model ?? request.Model,
        };
    }

    public async Task<ProviderStatus> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/models");
            Authorize(req);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new(Id, false, Error(resp.StatusCode, text).Message, [], DateTimeOffset.Now);

            var models = new List<string>();
            if (JsonNode.Parse(text)?["data"] is JsonArray data)
                // Gemini lists "models/gemini-…" but expects the bare name in requests.
                models.AddRange(data.Select(m => m?["id"]?.GetValue<string>()).OfType<string>()
                    .Select(id => id.StartsWith("models/", StringComparison.Ordinal) ? id["models/".Length..] : id));
            models.Sort(StringComparer.OrdinalIgnoreCase);
            var msg = models.Count > 0 ? $"{models.Count} model(s) available." : "Connected, but no models are installed.";
            return new(Id, models.Count > 0, msg, models, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            var hint = IsLocal ? " Is it installed and running?" : "";
            return new(Id, false, $"Not reachable at {BaseUrl}.{hint}", [], DateTimeOffset.Now);
        }
    }

    private void Authorize(HttpRequestMessage req)
    {
        var key = apiKey();
        if (!string.IsNullOrEmpty(key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private AiProviderException Error(HttpStatusCode status, string body)
    {
        var detail = TryExtractError(body);
        // Gemini answers a bad key with 400 "API key not valid" rather than 401.
        if (status == HttpStatusCode.BadRequest && detail.Contains("API key", StringComparison.OrdinalIgnoreCase))
            status = HttpStatusCode.Unauthorized;
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new AiProviderException($"{Name} rejected the API key ({(int)status}). Check it in Settings → AI."),
            HttpStatusCode.TooManyRequests =>
                new AiProviderException($"{Name} is rate-limiting requests. Try again shortly.", retryable: true),
            HttpStatusCode.NotFound =>
                new AiProviderException($"{Name}: model or endpoint not found. {detail}".Trim()),
            >= HttpStatusCode.InternalServerError =>
                new AiProviderException($"{Name} had a server error ({(int)status}). {detail}".Trim(), retryable: true),
            _ => new AiProviderException($"{Name} returned {(int)status}. {detail}".Trim()),
        };
    }

    private static string TryExtractError(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonArray { Count: > 0 } list) node = list[0]; // Gemini wraps its error in a list
            return node?["error"]?["message"]?.GetValue<string>() ?? node?["error"]?.ToString() ?? "";
        }
        catch { return body.Length > 200 ? body[..200] : body; }
    }

    internal static JsonArray BuildMessages(IEnumerable<ChatMessage> messages, bool gemini = false)
    {
        var arr = new JsonArray();
        foreach (var m in messages)
        {
            var o = new JsonObject
            {
                ["role"] = m.Role switch
                {
                    ChatRole.System => "system",
                    ChatRole.User => "user",
                    ChatRole.Assistant => "assistant",
                    _ => "tool",
                },
            };
            if (m.Role == ChatRole.Tool)
            {
                o["tool_call_id"] = m.ToolCallId;
                o["content"] = m.Content ?? "";
            }
            else
            {
                o["content"] = m.Content ?? "";
            }
            if (m.ToolCalls is { Count: > 0 })
            {
                // Gemini only checks signatures it handed out. Calls it didn't make (an earlier turn on another model)
                // carry the placeholder Google documents for that case; calls it made carry its own signature back.
                var signed = m.ToolCalls.Any(c => c.Extra is not null);
                o["tool_calls"] = new JsonArray(m.ToolCalls.Select(c =>
                {
                    var call = new JsonObject
                    {
                        ["id"] = c.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson },
                    };
                    if (c.Extra is not null) call["extra_content"] = c.Extra.DeepClone();
                    else if (gemini && !signed)
                        call["extra_content"] = new JsonObject { ["google"] = new JsonObject { ["thought_signature"] = "skip_thought_signature_validator" } };
                    return (JsonNode)call;
                }).ToArray());
            }
            arr.Add(o);
        }
        return arr;
    }

    internal static ChatResponse Parse(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new AiProviderException("Empty response from model.");
        var choice = root["choices"]?[0] ?? throw new AiProviderException("Model response had no choices.");
        var message = choice["message"];
        var calls = new List<ToolCall>();
        if (message?["tool_calls"] is JsonArray tc)
        {
            var i = 0;
            foreach (var c in tc)
            {
                var fn = c?["function"];
                var name = fn?["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name)) continue;
                var argsNode = fn!["arguments"];
                // Some servers return arguments as an object instead of a JSON string.
                var args = argsNode is JsonValue v && v.TryGetValue<string>(out var s) ? s : argsNode?.ToJsonString() ?? "{}";
                var id = c?["id"]?.GetValue<string>() ?? $"call_{i}";
                calls.Add(new ToolCall(id, name, string.IsNullOrWhiteSpace(args) ? "{}" : args) { Extra = c?["extra_content"]?.DeepClone() });
                i++;
            }
        }
        var usage = root["usage"] is JsonObject u
            ? new ChatUsage(u["prompt_tokens"]?.GetValue<int>() ?? 0, u["completion_tokens"]?.GetValue<int>() ?? 0)
            : null;
        return new ChatResponse
        {
            Content = message?["content"]?.GetValue<string>(),
            ToolCalls = calls,
            FinishReason = choice["finish_reason"]?.GetValue<string>(),
            Usage = usage,
            Model = root["model"]?.GetValue<string>() ?? "",
        };
    }
}
