using Jarvis.Core.Settings;

namespace Jarvis.Core.Tools;

public interface IToolRegistry
{
    IReadOnlyList<ITool> All { get; }
    ITool? Find(string name);
    void Register(ITool tool);
    /// <summary>Adds a plugin's tool. Refused if the name belongs to anything that isn't a plugin tool.</summary>
    bool TryRegisterPlugin(ITool tool);
    bool Unregister(string name);
    /// <summary>Tools the AI may call right now (blocked tools are hidden from the model entirely).</summary>
    IReadOnlyList<ToolDefinition> AvailableFor(JarvisSettings settings);
}

/// <summary>
/// The catalogue of everything JARVIS can do. Platform layers and (later) plugins register
/// tools here; the agent never calls platform code except through this registry.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        foreach (var t in tools) Register(t);
    }

    public IReadOnlyList<ITool> All
    {
        get { lock (_gate) return _tools.Values.OrderBy(t => t.Definition.Category).ThenBy(t => t.Definition.Name).ToList(); }
    }

    public ITool? Find(string name)
    {
        lock (_gate) return _tools.GetValueOrDefault(name);
    }

    public void Register(ITool tool)
    {
        var name = tool.Definition.Name;
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-zA-Z0-9_-]{1,64}$"))
            throw new ArgumentException($"Invalid tool name '{name}'. Use letters, digits, _ or -.");
        lock (_gate)
        {
            // Last registration wins so a platform-specific tool can replace a generic one.
            _tools[name] = tool;
        }
    }

    public bool TryRegisterPlugin(ITool tool)
    {
        var name = tool.Definition.Name;
        if (tool.Definition.Category != PluginCategory || !System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-zA-Z0-9_-]{1,64}$")) return false;
        lock (_gate)
        {
            // A plugin can never shadow a built-in capability.
            if (_tools.TryGetValue(name, out var existing) && existing.Definition.Category != PluginCategory) return false;
            _tools[name] = tool;
            return true;
        }
    }

    public bool Unregister(string name)
    {
        lock (_gate)
            return _tools.TryGetValue(name, out var t) && t.Definition.Category == PluginCategory && _tools.Remove(name);
    }

    public const string PluginCategory = "plugin";

    public IReadOnlyList<ToolDefinition> AvailableFor(JarvisSettings settings) =>
        All.Select(t => t.Definition)
            .Where(d => settings.Permissions.ToolOverrides.GetValueOrDefault(d.Name) != ToolPolicy.Block)
            .ToList();
}
