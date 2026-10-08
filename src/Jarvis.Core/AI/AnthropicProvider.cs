using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Jarvis.Core.Settings;
using AnthropicMessage = Anthropic.Models.Messages.Message;

namespace Jarvis.Core.AI;

/// <summary>
/// Optional cloud provider using the official Anthropic SDK with the user's own API key.
/// Disabled by default; only used when the user enables cloud AI and stores a key.
/// </summary>
public sealed class AnthropicProvider(ProviderConfig config, Func<string?> apiKey) : IChatProvider
{
    // Models offered in Settings. The default is the most capable general model;
    // users who prefer lower cost can pick Sonnet or Haiku.
    public static readonly string[] KnownModels = ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-fable-5-1"];

    public string Id => config.Id;
    public string Name => config.Name;
    public bool IsLocal => false;

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new AiProviderException($"{Name} has no API key. Add one in Settings → AI.");

        var client = CreateClient(key);
        var system = string.Join("\n\n", request.Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Content));
        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = Math.Max(request.MaxTokens, 1024),
            Messages = BuildMessages(request.Messages),
            Tools = request.Tools.Select(t => (ToolUnion)new Tool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = new()
                {
                    Properties = t.Parameters.ToDictionary(
                        p => p.Name,
                        p => JsonSerializer.SerializeToElement(t.ParametersSchema()["properties"]![p.Name])),
                    Required = t.Parameters.Where(p => p.Required).Select(p => p.Name).ToList(),
                },
            }).ToList(),
        };
        if (!string.IsNullOrWhiteSpace(system)) parameters = parameters with { System = system };

        AnthropicMessage response;
        try
        {
            response = await client.Messages.Create(parameters, ct).ConfigureAwait(false);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new AiProviderException($"{Name} is rate-limiting requests. Try again shortly.", retryable: true, ex);
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw new AiProviderException($"{Name} rejected the API key. Check it in Settings → AI.", inner: ex);
        }
        catch (Anthropic5xxException ex)
        {
            throw new AiProviderException($"{Name} had a server error. Try again shortly.", retryable: true, ex);
        }
        catch (AnthropicApiException ex)
        {
            throw new AiProviderException($"{Name} request failed: {ex.Message}", inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException($"{Name} is not reachable. Check your internet connection.", retryable: true, ex);
        }

        if (response.StopReason == "refusal")
        {
            var why = response.StopDetails?.Explanation;
            throw new AiProviderException($"{Name} declined this request{(string.IsNullOrEmpty(why) ? "." : ": " + why)}");
        }

        var text = new List<string>();
        var calls = new List<ToolCall>();
        var echo = new List<ContentBlockParam>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var t))
            {
                text.Add(t.Text);
                echo.Add(new TextBlockParam { Text = t.Text });
            }
            else if (block.TryPickThinking(out var th))
            {
                echo.Add(new ThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
            }
            else if (block.TryPickRedactedThinking(out var rt))
            {
                echo.Add(new RedactedThinkingBlockParam { Data = rt.Data });
            }
            else if (block.TryPickToolUse(out var tu))
            {
                calls.Add(new ToolCall(tu.ID, tu.Name, JsonSerializer.Serialize(tu.Input)));
                echo.Add(new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
            }
        }

        return new ChatResponse
        {
            Content = text.Count > 0 ? string.Join("\n", text) : null,
            ToolCalls = calls,
            FinishReason = response.StopReason?.ToString(),
            Usage = new ChatUsage((int)response.Usage.InputTokens, (int)response.Usage.OutputTokens),
            Model = request.Model,
            ProviderData = echo,
        };
    }

    public async Task<ProviderStatus> CheckAsync(CancellationToken ct)
    {
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key))
            return new(Id, false, "No API key stored.", KnownModels, DateTimeOffset.Now);
        try
        {
            var client = CreateClient(key);
            var page = await client.Models.List(cancellationToken: ct).ConfigureAwait(false);
            var models = page.Items.Select(m => m.ID).ToList();
            return new(Id, true, $"{models.Count} model(s) available.", models.Count > 0 ? models : KnownModels, DateTimeOffset.Now);
        }
        catch (AnthropicUnauthorizedException)
        {
            return new(Id, false, "API key rejected.", KnownModels, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is AnthropicApiException or HttpRequestException or TaskCanceledException)
        {
            return new(Id, false, "Not reachable.", KnownModels, DateTimeOffset.Now);
        }
    }

    private AnthropicClient CreateClient(string key)
    {
        return string.IsNullOrWhiteSpace(config.BaseUrl)
            ? new AnthropicClient { ApiKey = key }
            : new AnthropicClient { ApiKey = key, BaseUrl = config.BaseUrl };
    }

    /// <summary>
    /// Maps provider-neutral history to Anthropic messages: tool results become user turns,
    /// consecutive results are merged into one user message, and an assistant turn produced by
    /// this provider is echoed back verbatim (including thinking blocks).
    /// </summary>
    internal static List<MessageParam> BuildMessages(IEnumerable<ChatMessage> history)
    {
        var result = new List<MessageParam>();
        List<ContentBlockParam>? pendingToolResults = null;

        void FlushToolResults()
        {
            if (pendingToolResults is null) return;
            result.Add(new MessageParam { Role = Role.User, Content = pendingToolResults });
            pendingToolResults = null;
        }

        foreach (var m in history)
        {
            switch (m.Role)
            {
                case ChatRole.System:
                    continue;
                case ChatRole.Tool:
                    pendingToolResults ??= [];
                    pendingToolResults.Add(new ToolResultBlockParam
                    {
                        ToolUseID = m.ToolCallId ?? "",
                        Content = m.Content ?? "",
                        IsError = m.IsError,
                    });
                    continue;
                case ChatRole.User:
                    FlushToolResults();
                    result.Add(new MessageParam { Role = Role.User, Content = m.Content ?? "" });
                    continue;
                case ChatRole.Assistant:
                    FlushToolResults();
                    if (m.ProviderData is List<ContentBlockParam> echo && echo.Count > 0)
                    {
                        result.Add(new MessageParam { Role = Role.Assistant, Content = echo });
                        continue;
                    }
                    var blocks = new List<ContentBlockParam>();
                    if (!string.IsNullOrEmpty(m.Content)) blocks.Add(new TextBlockParam { Text = m.Content });
                    foreach (var c in m.ToolCalls ?? [])
                    {
                        var input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                            string.IsNullOrWhiteSpace(c.ArgumentsJson) ? "{}" : c.ArgumentsJson) ?? new();
                        blocks.Add(new ToolUseBlockParam { ID = c.Id, Name = c.Name, Input = input });
                    }
                    if (blocks.Count > 0) result.Add(new MessageParam { Role = Role.Assistant, Content = blocks });
                    continue;
            }
        }
        FlushToolResults();
        return result;
    }
}
