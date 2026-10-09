using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jarvis.Core.Tools;

namespace Jarvis.Core.Plugins;

public sealed record PluginParam
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "string";
    public string Description { get; init; } = "";
    public bool Required { get; init; }
    public List<string>? Enum { get; init; }
}

public sealed record PluginToolSpec
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public List<PluginParam> Parameters { get; init; } = [];
    /// <summary>"safe", "sensitive" or "critical" — what the author claims; JARVIS may raise it, never lower it.</summary>
    public string Risk { get; init; } = "sensitive";
}

/// <summary>Everything a plugin may do. Anything not listed here is impossible inside the sandbox.</summary>
public sealed record PluginPermissions
{
    /// <summary>Hosts the plugin may read from (HTTP GET), e.g. "api.frankfurter.app".</summary>
    public List<string> Http { get; init; } = [];
    /// <summary>Hosts the plugin may send data to (HTTP POST). Makes its tools at least sensitive.</summary>
    public List<string> HttpSend { get; init; } = [];
    /// <summary>May show notifications.</summary>
    public bool Notify { get; init; }
    /// <summary>May keep its own small key/value storage.</summary>
    public bool Storage { get; init; }
}

public sealed record PluginTest
{
    public string Tool { get; init; } = "";
    public JsonObject Args { get; init; } = [];
    /// <summary>Text the result must contain.</summary>
    public string Expect { get; init; } = "";
}

public sealed partial record PluginManifest
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "1.0.0";
    public string Description { get; init; } = "";
    public string? Author { get; init; }
    public PluginPermissions Permissions { get; init; } = new();
    public List<PluginToolSpec> Tools { get; init; } = [];
    public List<PluginTest> Tests { get; init; } = [];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static PluginManifest Parse(string json)
    {
        try { return JsonSerializer.Deserialize<PluginManifest>(json, Json) ?? throw new PluginException("The manifest is empty."); }
        catch (JsonException ex) { throw new PluginException($"The manifest isn't valid JSON: {ex.Message}"); }
    }

    /// <summary>The risk JARVIS uses: never below what the author declared, and at least sensitive when it can send data out.</summary>
    public RiskLevel EffectiveRisk(PluginToolSpec t)
    {
        var declared = t.Risk.ToLowerInvariant() switch { "safe" => RiskLevel.Safe, "critical" => RiskLevel.Critical, _ => RiskLevel.Sensitive };
        return Permissions.HttpSend.Count > 0 && declared < RiskLevel.Sensitive ? RiskLevel.Sensitive : declared;
    }

    /// <summary>The name a tool gets inside JARVIS (prefixed so it can never collide with a built-in one).</summary>
    public string ToolName(PluginToolSpec t)
    {
        var name = $"plugin_{Id.Replace('-', '_')}_{t.Name}";
        return name.Length > 64 ? name[..64] : name;
    }

    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        if (!IdRegex().IsMatch(Id)) p.Add("id must be 2–40 lowercase letters, digits or dashes.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 60) p.Add("name is required (max 60 characters).");
        if (!Regex.IsMatch(Version, @"^\d+\.\d+(\.\d+)?$")) p.Add("version must look like 1.0.0.");
        if (Description.Length > 500) p.Add("description is too long.");
        if (Tools.Count is 0 or > 20) p.Add("a plugin needs 1–20 tools.");
        foreach (var t in Tools)
        {
            if (!ToolRegex().IsMatch(t.Name)) p.Add($"tool name '{t.Name}' must be lowercase letters, digits or _.");
            if (string.IsNullOrWhiteSpace(t.Description)) p.Add($"tool '{t.Name}' needs a description.");
            if (t.Risk.ToLowerInvariant() is not ("safe" or "sensitive" or "critical")) p.Add($"tool '{t.Name}' risk must be safe, sensitive or critical.");
            foreach (var a in t.Parameters)
            {
                if (!ToolRegex().IsMatch(a.Name)) p.Add($"parameter '{a.Name}' of '{t.Name}' has an invalid name.");
                if (a.Type is not ("string" or "number" or "integer" or "boolean")) p.Add($"parameter '{a.Name}' of '{t.Name}' must be string, number, integer or boolean.");
            }
        }
        if (Tools.Select(t => t.Name).Distinct().Count() != Tools.Count) p.Add("tool names must be unique.");
        foreach (var h in Permissions.Http.Concat(Permissions.HttpSend))
            if (!HostRegex().IsMatch(h) || Regex.IsMatch(h, @"^\d+(\.\d+){3}$")) p.Add($"'{h}' isn't an allowed host (use a plain domain name, no wildcards or IP addresses).");
        foreach (var test in Tests)
            if (Tools.All(t => t.Name != test.Tool)) p.Add($"test refers to unknown tool '{test.Tool}'.");
        return p;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{1,39}$")]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9_]{1,40}$")]
    private static partial Regex ToolRegex();

    [GeneratedRegex(@"^(?=.{1,253}$)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)*$", RegexOptions.IgnoreCase)]
    private static partial Regex HostRegex();
}

public sealed class PluginException(string message) : Exception(message);
