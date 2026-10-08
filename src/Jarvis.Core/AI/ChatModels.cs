using System.Text.Json.Serialization;
using Jarvis.Core.Tools;

namespace Jarvis.Core.AI;

[JsonConverter(typeof(JsonStringEnumConverter<ChatRole>))]
public enum ChatRole { System, User, Assistant, Tool }

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>Provider-neutral chat message. Each provider maps this to its own wire format.</summary>
public sealed record ChatMessage
{
    public required ChatRole Role { get; init; }
    public string? Content { get; init; }
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }
    /// <summary>For <see cref="ChatRole.Tool"/> messages: which call this answers.</summary>
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public bool IsError { get; init; }
    /// <summary>Opaque provider-specific payload to echo back verbatim (e.g. Anthropic thinking blocks).</summary>
    [JsonIgnore] public object? ProviderData { get; init; }

    public static ChatMessage System(string text) => new() { Role = ChatRole.System, Content = text };
    public static ChatMessage User(string text) => new() { Role = ChatRole.User, Content = text };
    public static ChatMessage Assistant(string? text, IReadOnlyList<ToolCall>? calls = null) =>
        new() { Role = ChatRole.Assistant, Content = text, ToolCalls = calls };
    public static ChatMessage ToolResult(string callId, string toolName, string content, bool isError = false) =>
        new() { Role = ChatRole.Tool, ToolCallId = callId, ToolName = toolName, Content = content, IsError = isError };
}

public sealed record ChatRequest
{
    public required string Model { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];
    public int MaxTokens { get; init; } = 4096;
    public double? Temperature { get; init; }
}

public sealed record ChatUsage(int InputTokens, int OutputTokens);

public sealed record ChatResponse
{
    public string? Content { get; init; }
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];
    public string? FinishReason { get; init; }
    public ChatUsage? Usage { get; init; }
    public string Model { get; init; } = "";
    [JsonIgnore] public object? ProviderData { get; init; }
}

public sealed record ProviderStatus(string ProviderId, bool Available, string Message, IReadOnlyList<string> Models, DateTimeOffset CheckedAt);

/// <summary>A source of language-model completions (local server, cloud API...).</summary>
public interface IChatProvider
{
    string Id { get; }
    string Name { get; }
    bool IsLocal { get; }
    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct);
    Task<ProviderStatus> CheckAsync(CancellationToken ct);
}

/// <summary>Raised for provider failures with a message suitable for the user.</summary>
public sealed class AiProviderException(string message, bool retryable = false, Exception? inner = null) : Exception(message, inner)
{
    public bool Retryable { get; } = retryable;
}
