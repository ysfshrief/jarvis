using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Jarvis.Core.Language;
using Jarvis.Core.Settings;

namespace Jarvis.Core.Tools;

/// <summary>
/// How much damage an action could do if it were wrong.
/// Safe runs immediately, Sensitive asks unless the user opted out, Critical always asks.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RiskLevel>))]
public enum RiskLevel { Safe = 0, Sensitive = 1, Critical = 2 }

public sealed record ToolParameter(
    string Name,
    string Type,
    string Description,
    bool Required = false,
    IReadOnlyList<string>? Enum = null);

/// <summary>Static description of a capability; exposed to the AI model as a function schema.</summary>
public sealed class ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }
    public RiskLevel Risk { get; init; } = RiskLevel.Safe;
    public bool RequiresInternet { get; init; }
    public IReadOnlyList<ToolParameter> Parameters { get; init; } = [];

    /// <summary>JSON Schema (draft-07 subset) of the parameters, as AI providers expect.</summary>
    public JsonObject ParametersSchema()
    {
        var props = new JsonObject();
        foreach (var p in Parameters)
        {
            var prop = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
            if (p.Enum is { Count: > 0 })
                prop["enum"] = new JsonArray(p.Enum.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
            props[p.Name] = prop;
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray(Parameters.Where(p => p.Required).Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray()),
        };
    }
}

/// <summary>What a specific invocation will do and how risky it is (may exceed the tool's base risk).</summary>
public sealed record RiskAssessment(RiskLevel Level, string Summary, string? Reason = null);

public interface ITool
{
    ToolDefinition Definition { get; }

    /// <summary>Describe this particular call and grade its risk. Must not have side effects.</summary>
    RiskAssessment Assess(ToolArgs args, ToolContext context);

    /// <summary>Perform the action and verify it worked. Never report success that was not observed.</summary>
    Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext context);
}

public abstract class ToolBase : ITool
{
    public abstract ToolDefinition Definition { get; }

    public virtual RiskAssessment Assess(ToolArgs args, ToolContext context) =>
        new(Definition.Risk, Describe(args));

    /// <summary>Short human description of what this call does, shown in approvals and the activity log.</summary>
    protected virtual string Describe(ToolArgs args) =>
        args.IsEmpty ? Definition.Name : $"{Definition.Name} {args.ToCompactString()}";

    public abstract Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext context);
}

public enum ToolStatus { Ok, Failed, Denied, Queued, TimedOut, NotFound }

/// <summary>Outcome of a tool invocation. <see cref="Message"/> is user-facing, in the user's language.</summary>
public sealed record ToolResult
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public object? Data { get; init; }
    public string? Error { get; init; }
    public ToolStatus Status { get; init; } = ToolStatus.Ok;

    public static ToolResult Ok(string message, object? data = null) =>
        new() { Success = true, Message = message, Data = data };

    public static ToolResult Fail(string message, string? error = null, ToolStatus status = ToolStatus.Failed) =>
        new() { Success = false, Message = message, Error = error ?? message, Status = status };
}

/// <summary>Ambient information for a tool call.</summary>
public sealed class ToolContext
{
    public required Lang Lang { get; init; }
    public required JarvisSettings Settings { get; init; }
    public string ConversationId { get; init; } = "";
    /// <summary>The agent turn this call belongs to, so the UI can group steps under one request.</summary>
    public string? TurnId { get; init; }
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Pick the English or Egyptian Arabic phrasing.</summary>
    public string T(string en, string ar) => Lang == Lang.Ar ? ar : en;

    /// <summary>The honorific in the current language, e.g. "Sir" or "يا فندم".</summary>
    public string Sir => Lang == Lang.Ar ? Settings.General.HonorificAr : Settings.General.Honorific;

    /// <summary>", Sir" suffix (or empty when the user turned honorifics off).</summary>
    public string CommaSir => string.IsNullOrWhiteSpace(Sir) ? "" : (Lang == Lang.Ar ? " " + Sir : ", " + Sir);
}

/// <summary>Arguments of a tool call, backed by a JSON object (what AI models produce).</summary>
public sealed class ToolArgs
{
    private readonly JsonObject _obj;

    public ToolArgs(JsonObject? obj = null) => _obj = obj ?? new JsonObject();

    public static ToolArgs Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ToolArgs();
        try
        {
            return new ToolArgs(JsonNode.Parse(json) as JsonObject);
        }
        catch (JsonException)
        {
            throw new ToolArgumentException("Arguments are not valid JSON.");
        }
    }

    public static ToolArgs From(object values) =>
        new(JsonSerializer.SerializeToNode(values) as JsonObject);

    public bool IsEmpty => _obj.Count == 0;
    public JsonObject Json => _obj;

    public string? GetString(string name)
    {
        var node = _obj[name];
        if (node is null) return null;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            return v.ToJsonString();
        }
        return node.ToJsonString();
    }

    public string RequireString(string name) =>
        GetString(name) ?? throw new ToolArgumentException($"Missing required argument '{name}'.");

    public int? GetInt(string name)
    {
        var node = _obj[name];
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) return p;
        return null;
    }

    public double? GetDouble(string name)
    {
        var node = _obj[name];
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    public bool? GetBool(string name)
    {
        var node = _obj[name];
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out var p)) return p;
        return null;
    }

    public string ToCompactString()
    {
        var parts = _obj.Select(kv =>
        {
            var val = kv.Value is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : kv.Value?.ToJsonString() ?? "null";
            if (val.Length > 80) val = val[..77] + "...";
            return $"{kv.Key}={val}";
        });
        return string.Join(", ", parts);
    }

    public override string ToString() => _obj.ToJsonString();
}

public sealed class ToolArgumentException(string message) : Exception(message);
