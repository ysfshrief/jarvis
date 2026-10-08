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

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = BuildMessages(request.Messages),
            ["max_tokens"] = request.MaxTokens,
            ["stream"] = false,
        };
        if (request.Temperature is { } temp) body["temperature"] = temp;
        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.ParametersSchema(),
                },
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
            resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException($"{Name} is not reachable at {BaseUrl}.", retryable: true, ex);
        }

        using (resp)
        {
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
                models.AddRange(data.Select(m => m?["id"]?.GetValue<string>()).OfType<string>());
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
            return node?["error"]?["message"]?.GetValue<string>() ?? node?["error"]?.ToString() ?? "";
        }
        catch { return body.Length > 200 ? body[..200] : body; }
    }

    internal static JsonArray BuildMessages(IEnumerable<ChatMessage> messages)
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
                o["tool_calls"] = new JsonArray(m.ToolCalls.Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson },
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
                calls.Add(new ToolCall(id, name, string.IsNullOrWhiteSpace(args) ? "{}" : args));
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
