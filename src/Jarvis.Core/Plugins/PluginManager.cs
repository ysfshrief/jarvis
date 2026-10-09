using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Core.Activity;
using Jarvis.Core.Events;
using Jarvis.Core.Persistence;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Plugins;

public static class PluginStatus
{
    public const string Draft = "draft";
    public const string Failed = "failed";
    /// <summary>Validated and its own tests passed in the sandbox; waiting for your approval.</summary>
    public const string Ready = "ready";
    public const string Installed = "installed";
    public const string Disabled = "disabled";
    /// <summary>Its files changed after you approved it, so it isn't loaded.</summary>
    public const string Tampered = "changed";
}

public sealed record TestOutcome(string Tool, bool Passed, string Detail, IReadOnlyList<string> Log, bool Skipped = false);
public sealed record PluginReport(IReadOnlyList<string> Problems, IReadOnlyList<TestOutcome> Tests, DateTimeOffset At, bool NetworkTested = false)
{
    public bool Ok => Problems.Count == 0 && Tests.All(t => t.Passed || t.Skipped);
}

public sealed record PluginInfo(
    PluginManifest Manifest, string Status, string Source, string Code, PluginReport? Report, string? Error,
    DateTimeOffset CreatedAt, DateTimeOffset? InstalledAt)
{
    public string Id => Manifest.Id;
    /// <summary>A newer version waiting for checks and approval; the installed version keeps running meanwhile.</summary>
    public PluginUpdate? Update { get; init; }
}

/// <summary>A pending update and what it would change, in plain words.</summary>
public sealed record PluginUpdate(PluginManifest Manifest, string Code, string Status, string Source, PluginReport? Report, IReadOnlyList<string> Changes, bool MorePermissions);

/// <summary>
/// The plugin lifecycle: a draft (written by JARVIS from your description, or imported) is validated, its
/// own tests run in the sandbox with nothing able to leave the PC except reads from its declared hosts, you
/// see exactly what it may do, and only your explicit approval installs it. Installed code is fingerprinted;
/// if the files change afterwards, the plugin stops loading until you approve it again.
/// </summary>
public sealed class PluginManager(JarvisPaths paths, JarvisDatabase db, PluginSandbox sandbox, IServiceProvider services, IEventBus events, ActivityLog activity, ILogger<PluginManager> logger)
{
    // Resolved on use: the registry contains the plugin tools, which themselves need this manager.
    private IToolRegistry Registry => (IToolRegistry)services.GetService(typeof(IToolRegistry))!;
    private const int MaxCodeBytes = 100_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Root => Path.Combine(paths.DataDir, "plugins");

    /// <summary>Saves a draft (replacing an earlier draft with the same id) and checks it.</summary>
    public PluginInfo SaveDraft(string manifestJson, string code, string source)
    {
        var m = PluginManifest.Parse(manifestJson);
        if (!System.Text.RegularExpressions.Regex.IsMatch(m.Id, "^[a-z0-9][a-z0-9-]{1,39}$")) throw new PluginException("The plugin id is invalid (lowercase letters, digits and dashes).");
        if (Encoding.UTF8.GetByteCount(code) > MaxCodeBytes) throw new PluginException("The plugin code is too large (100 KB max).");
        if (Get(m.Id) is { Status: PluginStatus.Installed or PluginStatus.Disabled } installed) return SaveUpdate(installed, m, code, source);
        var dir = Path.Combine(Root, m.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(m, PluginManifest.Json));
        File.WriteAllText(Path.Combine(dir, "main.js"), code);
        var now = JarvisDatabase.Now();
        Exec("""
            INSERT INTO plugins(id, name, version, status, source, code_hash, created_at, updated_at) VALUES ($id, $n, $v, 'draft', $s, $h, $t, $t)
            ON CONFLICT(id) DO UPDATE SET name = $n, version = $v, status = 'draft', source = $s, code_hash = $h, report = NULL, error = NULL, updated_at = $t;
            """, c =>
        {
            c.Parameters.AddWithValue("$id", m.Id);
            c.Parameters.AddWithValue("$n", m.Name);
            c.Parameters.AddWithValue("$v", m.Version);
            c.Parameters.AddWithValue("$s", source);
            c.Parameters.AddWithValue("$h", Hash(m, code));
            c.Parameters.AddWithValue("$t", now);
        });
        activity.Record(ActivityKinds.System, $"Plugin draft saved: {m.Name}", status: "ok", details: source);
        Changed();
        return Get(m.Id)!;
    }

    /// <summary>
    /// Validate → run its tests in the sandbox → ready or failed. Nothing is ever sent and no notifications are
    /// shown; reads from the declared hosts happen only when <paramref name="allowNetwork"/> is set, which only
    /// your own click does (an AI-written plugin can't reach the network before you've seen where it goes).
    /// </summary>
    public PluginInfo Check(string id, CancellationToken ct, bool allowNetwork = false)
    {
        var p = Get(id) ?? throw new PluginException("No such plugin.");
        if (p.Status is PluginStatus.Installed or PluginStatus.Disabled)
        {
            if (p.Update is null) throw new PluginException("It's already installed.");
            var updateReport = RunChecks(p.Update.Manifest, p.Update.Code, allowNetwork, ct);
            Exec("UPDATE plugins SET update_status = $s, update_report = $r, updated_at = $t WHERE id = $id;", c =>
            {
                c.Parameters.AddWithValue("$id", id);
                c.Parameters.AddWithValue("$s", updateReport.Ok ? PluginStatus.Ready : PluginStatus.Failed);
                c.Parameters.AddWithValue("$r", JsonSerializer.Serialize(updateReport, Json));
                c.Parameters.AddWithValue("$t", JarvisDatabase.Now());
            });
            Changed();
            return Get(id)!;
        }
        var report = RunChecks(p.Manifest, p.Code, allowNetwork, ct);
        Exec("UPDATE plugins SET status = $s, report = $r, error = NULL, updated_at = $t WHERE id = $id;", c =>
        {
            c.Parameters.AddWithValue("$id", id);
            c.Parameters.AddWithValue("$s", report.Ok ? PluginStatus.Ready : PluginStatus.Failed);
            c.Parameters.AddWithValue("$r", JsonSerializer.Serialize(report, Json));
            c.Parameters.AddWithValue("$t", JarvisDatabase.Now());
        });
        Changed();
        return Get(id)!;
    }

    private PluginReport RunChecks(PluginManifest manifest, string code, bool allowNetwork, CancellationToken ct)
    {
        var p = (Manifest: manifest, Code: code);
        var problems = p.Manifest.Problems().ToList();
        var tests = new List<TestOutcome>();
        sandbox.ClearStorage(PluginSandbox.CheckStorage(manifest.Id));
        if (problems.Count == 0) problems.AddRange(sandbox.Check(p.Manifest, p.Code));
        if (problems.Count == 0)
        {
            foreach (var t in p.Manifest.Tests)
            {
                var r = sandbox.Run(p.Manifest, p.Code, t.Tool, t.Args, sendAllowed: false, ct, networkAllowed: allowNetwork, storageId: PluginSandbox.CheckStorage(p.Manifest.Id));
                if (!r.Success && r.Message.StartsWith(PluginSandbox.NetworkOff, StringComparison.Ordinal))
                {
                    tests.Add(new TestOutcome(t.Tool, false, "Needs the network — run the tests after reviewing the hosts.", r.Log, Skipped: true));
                    continue;
                }
                var text = r.Message + (r.Data is null ? "" : " " + r.Data.ToJsonString());
                var passed = r.Success && (t.Expect.Length == 0 || text.Contains(t.Expect, StringComparison.OrdinalIgnoreCase));
                tests.Add(new TestOutcome(t.Tool, passed, passed ? r.Message : r.Success ? $"expected “{t.Expect}”, got: {Short(text)}" : r.Message, r.Log));
            }
        }
        sandbox.ClearStorage(PluginSandbox.CheckStorage(manifest.Id)); // each check starts clean and leaves nothing behind
        return new PluginReport(problems, tests, DateTimeOffset.Now, allowNetwork);
    }

    /// <summary>Installs a ready plugin. Only the critical plugin_install tool calls this, after your approval.</summary>
    public PluginInfo Install(string id)
    {
        var p = Get(id) ?? throw new PluginException("No such plugin.");
        if (p.Status != PluginStatus.Ready) throw new PluginException(p.Status == PluginStatus.Installed ? "It's already installed." : "Only a plugin that passed its checks can be installed.");
        var hash = Hash(p.Manifest, p.Code);
        Exec("UPDATE plugins SET status = 'installed', approved_hash = $h, installed_at = $t, updated_at = $t WHERE id = $id;", c =>
        {
            c.Parameters.AddWithValue("$id", id);
            c.Parameters.AddWithValue("$h", hash);
            c.Parameters.AddWithValue("$t", JarvisDatabase.Now());
        });
        Load(Get(id)!);
        activity.Record(ActivityKinds.System, $"Plugin installed: {p.Manifest.Name} {p.Manifest.Version}", status: "ok", details: Describe(p.Manifest));
        Changed();
        return Get(id)!;
    }

    private PluginInfo SaveUpdate(PluginInfo installed, PluginManifest m, string code, string source)
    {
        if (!IsNewer(m.Version, installed.Manifest.Version))
            throw new PluginException($"“{installed.Manifest.Name}” {installed.Manifest.Version} is installed; an update needs a higher version than that (got {m.Version}).");
        var dir = Path.Combine(Root, m.Id, "update");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(m, PluginManifest.Json));
        File.WriteAllText(Path.Combine(dir, "main.js"), code);
        Exec("UPDATE plugins SET update_status = 'draft', update_source = $s, update_hash = $h, update_report = NULL, updated_at = $t WHERE id = $id;", c =>
        {
            c.Parameters.AddWithValue("$id", m.Id);
            c.Parameters.AddWithValue("$s", source);
            c.Parameters.AddWithValue("$h", Hash(m, code));
            c.Parameters.AddWithValue("$t", JarvisDatabase.Now());
        });
        activity.Record(ActivityKinds.System, $"Plugin update saved: {m.Name} {installed.Manifest.Version} → {m.Version}", status: "ok", details: source);
        Changed();
        return Get(m.Id)!;
    }

    /// <summary>
    /// Replaces an installed plugin with its checked update. Only the critical plugin_update tool calls this, after
    /// the user saw what changes. Its storage is kept; its tools are reloaded from the newly approved code.
    /// </summary>
    public PluginInfo ApplyUpdate(string id)
    {
        var p = Get(id) ?? throw new PluginException("No such plugin.");
        var u = p.Update ?? throw new PluginException("There's no update waiting for this plugin.");
        if (u.Status != PluginStatus.Ready) throw new PluginException("Only an update that passed its checks can be installed.");
        var hash = Hash(u.Manifest, u.Code);
        if (hash != UpdateHash(id)) throw new PluginException("The update's files changed after it was checked. Check it again.");
        var dir = Path.Combine(Root, id);
        UnloadTools(p.Manifest);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(u.Manifest, PluginManifest.Json));
        File.WriteAllText(Path.Combine(dir, "main.js"), u.Code);
        try { Directory.Delete(Path.Combine(dir, "update"), true); } catch (IOException) { }
        Exec("""
            UPDATE plugins SET name = $n, version = $v, code_hash = $h, approved_hash = $h, report = update_report, error = NULL,
                source = update_source, update_status = NULL, update_source = NULL, update_hash = NULL, update_report = NULL, updated_at = $t
            WHERE id = $id;
            """, c =>
        {
            c.Parameters.AddWithValue("$id", id);
            c.Parameters.AddWithValue("$n", u.Manifest.Name);
            c.Parameters.AddWithValue("$v", u.Manifest.Version);
            c.Parameters.AddWithValue("$h", hash);
            c.Parameters.AddWithValue("$t", JarvisDatabase.Now());
        });
        var updated = Get(id)!;
        if (updated.Status == PluginStatus.Installed) Load(updated);
        activity.Record(ActivityKinds.System, $"Plugin updated: {u.Manifest.Name} {p.Manifest.Version} → {u.Manifest.Version}", status: "ok", details: string.Join("; ", u.Changes));
        Changed();
        return updated;
    }

    public bool DiscardUpdate(string id)
    {
        var p = Get(id);
        if (p?.Update is null) return false;
        try { Directory.Delete(Path.Combine(Root, id, "update"), true); } catch (IOException) { }
        Exec("UPDATE plugins SET update_status = NULL, update_source = NULL, update_hash = NULL, update_report = NULL WHERE id = $id;", c => c.Parameters.AddWithValue("$id", id));
        Changed();
        return true;
    }

    /// <summary>What an update changes, permissions first. <c>More</c> is true when it may do anything new.</summary>
    public static (IReadOnlyList<string> Changes, bool More) Diff(PluginManifest from, PluginManifest to)
    {
        var list = new List<string>();
        var more = false;
        void Hosts(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b, string verb)
        {
            foreach (var h in b.Except(a, StringComparer.OrdinalIgnoreCase)) { list.Add($"NEW: may {verb} {h}"); more = true; }
            foreach (var h in a.Except(b, StringComparer.OrdinalIgnoreCase)) list.Add($"no longer {verb} {h}");
        }
        Hosts(from.Permissions.Http, to.Permissions.Http, "read from");
        Hosts(from.Permissions.HttpSend, to.Permissions.HttpSend, "SEND data to");
        if (!from.Permissions.Storage && to.Permissions.Storage) { list.Add("NEW: may keep its own storage"); more = true; }
        if (from.Permissions.Storage && !to.Permissions.Storage) list.Add("no longer uses storage");
        if (!from.Permissions.Notify && to.Permissions.Notify) { list.Add("NEW: may show notifications"); more = true; }
        if (from.Permissions.Notify && !to.Permissions.Notify) list.Add("no longer shows notifications");
        foreach (var t in to.Tools)
        {
            var old = from.Tools.FirstOrDefault(o => o.Name == t.Name);
            var risk = to.EffectiveRisk(t);
            if (old is null) { list.Add($"new tool {t.Name} ({risk.ToString().ToLowerInvariant()})"); more = true; }
            else if (from.EffectiveRisk(old) != risk)
            {
                list.Add($"{t.Name}: {from.EffectiveRisk(old).ToString().ToLowerInvariant()} → {risk.ToString().ToLowerInvariant()}");
                if (risk < from.EffectiveRisk(old)) more = true; // a lower grade means fewer confirmations
            }
        }
        foreach (var t in from.Tools.Where(o => to.Tools.All(n => n.Name != o.Name))) list.Add($"removed tool {t.Name}");
        if (list.Count == 0) list.Add("Same permissions and tools as the installed version.");
        list.Insert(0, $"version {from.Version} → {to.Version}");
        return (list, more);
    }

    internal static bool IsNewer(string candidate, string current)
    {
        static int[] Parts(string v) => v.Split('-', '+')[0].Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).Concat([0, 0, 0]).Take(3).ToArray();
        var a = Parts(candidate);
        var b = Parts(current);
        for (var i = 0; i < 3; i++) if (a[i] != b[i]) return a[i] > b[i];
        return false;
    }

    private string? UpdateHash(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT update_hash FROM plugins WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }

    public void SetEnabled(string id, bool enabled)
    {
        var p = Get(id) ?? throw new PluginException("No such plugin.");
        if (p.Status is not (PluginStatus.Installed or PluginStatus.Disabled)) throw new PluginException("It isn't installed.");
        SetStatus(id, enabled ? PluginStatus.Installed : PluginStatus.Disabled);
        if (enabled) Load(Get(id)!); else UnloadTools(p.Manifest);
        activity.Record(ActivityKinds.System, $"Plugin {(enabled ? "enabled" : "disabled")}: {p.Manifest.Name}", status: "ok");
        Changed();
    }

    public bool Remove(string id)
    {
        var p = Get(id);
        if (p is null) return false;
        UnloadTools(p.Manifest);
        sandbox.ClearStorage(id);
        Exec("DELETE FROM plugins WHERE id = $id;", c => c.Parameters.AddWithValue("$id", id));
        try { Directory.Delete(Path.Combine(Root, id), true); } catch (IOException) { }
        activity.Record(ActivityKinds.System, $"Plugin removed: {p.Manifest.Name}", status: "ok");
        Changed();
        return true;
    }

    /// <summary>At start-up: loads installed plugins whose files still match what you approved.</summary>
    public int LoadInstalled()
    {
        var n = 0;
        foreach (var p in List().Where(p => p.Status == PluginStatus.Installed))
        {
            if (Hash(p.Manifest, p.Code) != ApprovedHash(p.Id))
            {
                SetStatus(p.Id, PluginStatus.Tampered, "Its files changed after you approved it. Review it and approve again.");
                activity.Record(ActivityKinds.System, $"Plugin not loaded (files changed): {p.Manifest.Name}", status: "blocked");
                continue;
            }
            Load(p);
            n++;
        }
        return n;
    }

    public PluginInfo? Get(string id) => List().FirstOrDefault(p => p.Id == id);

    public IReadOnlyList<PluginInfo> List()
    {
        var list = new List<PluginInfo>();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, status, source, report, error, created_at, installed_at, update_status, update_source, update_report FROM plugins ORDER BY created_at;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetString(0);
            var dir = Path.Combine(Root, id);
            PluginManifest manifest;
            string code;
            try
            {
                manifest = PluginManifest.Parse(File.ReadAllText(Path.Combine(dir, "plugin.json")));
                code = File.ReadAllText(Path.Combine(dir, "main.js"));
            }
            catch (Exception ex) when (ex is IOException or PluginException or UnauthorizedAccessException)
            {
                logger.LogWarning("Plugin {Id} files are unreadable: {Error}", id, ex.Message);
                continue;
            }
            PluginUpdate? update = null;
            if (!r.IsDBNull(7))
            {
                try
                {
                    var um = PluginManifest.Parse(File.ReadAllText(Path.Combine(dir, "update", "plugin.json")));
                    var (changes, more) = Diff(manifest, um);
                    update = new PluginUpdate(um, File.ReadAllText(Path.Combine(dir, "update", "main.js")), r.GetString(7), r.IsDBNull(8) ? "" : r.GetString(8),
                        r.IsDBNull(9) ? null : JsonSerializer.Deserialize<PluginReport>(r.GetString(9), Json), changes, more);
                }
                catch (Exception ex) when (ex is IOException or PluginException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Plugin {Id} update files are unreadable: {Error}", id, ex.Message);
                }
            }
            list.Add(new PluginInfo(manifest, r.GetString(1), r.GetString(2), code,
                r.IsDBNull(3) ? null : JsonSerializer.Deserialize<PluginReport>(r.GetString(3), Json), r.IsDBNull(4) ? null : r.GetString(4),
                DateTimeOffset.Parse(r.GetString(5)), r.IsDBNull(6) ? null : DateTimeOffset.Parse(r.GetString(6))) { Update = update });
        }
        return list;
    }

    /// <summary>What the plugin may do, in plain words (shown before you approve).</summary>
    public static string Describe(PluginManifest m)
    {
        var parts = new List<string>();
        if (m.Permissions.Http.Count > 0) parts.Add($"read from {string.Join(", ", m.Permissions.Http)}");
        if (m.Permissions.HttpSend.Count > 0) parts.Add($"SEND data to {string.Join(", ", m.Permissions.HttpSend)}");
        if (m.Permissions.Storage) parts.Add("keep its own small storage");
        if (m.Permissions.Notify) parts.Add("show notifications");
        var tools = string.Join(", ", m.Tools.Select(t => $"{t.Name} ({m.EffectiveRisk(t).ToString().ToLowerInvariant()})"));
        return $"Tools: {tools}. May {(parts.Count == 0 ? "only compute with what it's given — no network, files or storage" : string.Join("; ", parts))}. No access to files, apps, other plugins or JARVIS's data.";
    }

    private void Load(PluginInfo p)
    {
        foreach (var t in p.Manifest.Tools)
        {
            // The approved code is held in memory: later edits to the files on disk can't change what runs.
            var tool = new PluginTool(p.Manifest, t, sandbox, p.Code);
            if (!Registry.TryRegisterPlugin(tool)) logger.LogWarning("Plugin tool {Tool} was refused (name clash)", tool.Definition.Name);
        }
    }

    private void UnloadTools(PluginManifest m)
    {
        foreach (var t in m.Tools) Registry.Unregister(m.ToolName(t));
    }

    private string? ApprovedHash(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT approved_hash FROM plugins WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }

    private void SetStatus(string id, string status, string? error = null) =>
        Exec("UPDATE plugins SET status = $s, error = $e, updated_at = $t WHERE id = $id;", c =>
        {
            c.Parameters.AddWithValue("$id", id);
            c.Parameters.AddWithValue("$s", status);
            c.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
            c.Parameters.AddWithValue("$t", JarvisDatabase.Now());
        });

    internal static string Hash(PluginManifest m, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(m, Json) + "\n" + code)));

    private int Exec(string sql, Action<Microsoft.Data.Sqlite.SqliteCommand> bind)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        return cmd.ExecuteNonQuery();
    }

    private void Changed() => events.Publish(EventTypes.PluginsChanged, new { });

    private static string Short(string s) => s.Length > 200 ? s[..197] + "…" : s;
}

/// <summary>One tool of an installed plugin, run in the sandbox and governed like any other tool.</summary>
public sealed class PluginTool(PluginManifest manifest, PluginToolSpec spec, PluginSandbox sandbox, string code) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = manifest.ToolName(spec),
        Category = ToolRegistry.PluginCategory,
        Description = $"[{manifest.Name} plugin] {spec.Description}",
        Risk = manifest.EffectiveRisk(spec),
        RequiresInternet = manifest.Permissions.Http.Count + manifest.Permissions.HttpSend.Count > 0,
        // What comes back from the web is someone else's content.
        ReadsUntrustedContent = manifest.Permissions.Http.Count + manifest.Permissions.HttpSend.Count > 0,
        Parameters = spec.Parameters.Select(p => new ToolParameter(p.Name, p.Type, p.Description, p.Required, p.Enum)).ToList(),
    };

    public RiskAssessment Assess(ToolArgs args, ToolContext context) =>
        new(Definition.Risk, $"{manifest.Name}: {spec.Name}", manifest.Permissions.HttpSend.Count > 0 ? $"May send data to {string.Join(", ", manifest.Permissions.HttpSend)}." : null);

    public Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var json = JsonNode.Parse(args.ToString()) as JsonObject ?? [];
        var r = sandbox.Run(manifest, code, spec.Name, json, sendAllowed: true, ctx.CancellationToken);
        return Task.FromResult(r.Success
            ? ToolResult.Ok(r.Message, new { plugin = manifest.Id, data = r.Data?.ToJsonString(), untrustedContent = r.Message })
            : ToolResult.Fail(ctx.T($"The {manifest.Name} plugin failed: {r.Message}", $"إضافة {manifest.Name} فشلت: {r.Message}"), r.Message));
    }
}
