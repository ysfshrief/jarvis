using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Core.Notifications;
using Jarvis.Core.Persistence;
using Jarvis.Core.Settings;
using Jarvis.Core.Tools.Builtin;
using Jint;

namespace Jarvis.Core.Plugins;

public sealed record SandboxResult(bool Success, string Message, JsonNode? Data, IReadOnlyList<string> Log);

/// <summary>
/// Runs plugin JavaScript in Jint — an interpreter with no access to .NET, files, processes or the network
/// on its own. The only way out is the <c>jarvis</c> object, and each of its abilities is checked against the
/// permissions the user approved: HTTP only to listed hosts (never local addresses), storage only if granted,
/// notifications only if granted. Time, statements, memory and recursion are all limited.
/// </summary>
public sealed class PluginSandbox(HttpClient http, JarvisDatabase db, NotificationCenter notifications, ISettingsStore settings)
{
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(20);
    private const int MaxRequests = 20, MaxResponseBytes = 1_000_000, MaxStorageValue = 65_536, MaxStorageKeys = 500;

    private const string Prelude = """
        const jarvis = Object.freeze({
          http: Object.freeze({
            get: (url) => __get(String(url)),
            getJson: (url) => JSON.parse(__get(String(url))),
            post: (url, body) => __post(String(url), typeof body === 'string' ? body : JSON.stringify(body)),
            postJson: (url, body) => JSON.parse(__post(String(url), typeof body === 'string' ? body : JSON.stringify(body))),
          }),
          storage: Object.freeze({
            get: (key) => __sget(String(key)),
            set: (key, value) => __sset(String(key), value === undefined || value === null ? null : String(value)),
          }),
          notify: (text) => __notify(String(text)),
          log: (text) => __log(String(text)),
        });
        """;

    /// <summary>Loads the code and checks it defines a function for every declared tool.</summary>
    public IReadOnlyList<string> Check(PluginManifest m, string code)
    {
        var problems = new List<string>();
        try
        {
            var engine = Create(m, code, sendAllowed: false, [], CancellationToken.None, networkAllowed: false);
            foreach (var t in m.Tools)
                if (engine.Evaluate($"typeof {t.Name}").AsString() != "function") problems.Add($"the code doesn't define function {t.Name}(args).");
        }
        catch (Exception ex) { problems.Add("the code doesn't load: " + Describe(ex)); }
        return problems;
    }

    /// <summary>Runs one tool. <paramref name="sendAllowed"/> is false for pre-approval tests: nothing can be sent anywhere.</summary>
    public SandboxResult Run(PluginManifest m, string code, string tool, JsonObject args, bool sendAllowed, CancellationToken ct, bool networkAllowed = true)
    {
        var log = new List<string>();
        try
        {
            var engine = Create(m, code, sendAllowed, log, ct, networkAllowed);
            engine.SetValue("__args", args.ToJsonString());
            var json = engine.Evaluate($"JSON.stringify((function () {{ const r = {tool}(JSON.parse(__args)); return r === undefined ? null : r; }})())").AsString();
            var node = JsonNode.Parse(json);
            return node switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => new(true, s, null, log),
                JsonObject o => new(o["ok"]?.GetValue<bool>() != false, o["message"]?.ToString() ?? o.ToJsonString(), o["data"], log),
                null => new(true, "Done.", null, log),
                _ => new(true, node.ToJsonString(), node, log),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new(false, Describe(ex), null, log);
        }
    }

    public const string NetworkOff = "the network is off during automatic checks";

    private Engine Create(PluginManifest m, string code, bool sendAllowed, List<string> log, CancellationToken ct, bool networkAllowed)
    {
        var requests = 0;
        var engine = new Engine(o => o
            .Strict()
            .TimeoutInterval(TimeLimit)
            .MaxStatements(5_000_000)
            .LimitMemory(64_000_000)
            .LimitRecursion(256)
            .CancellationToken(ct));

        string Fetch(string url, string? body)
        {
            if (!networkAllowed) throw new PluginException(NetworkOff + ".");
            if (++requests > MaxRequests) throw new PluginException("too many web requests in one call.");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new PluginException($"'{url}' isn't a web address.");
            var host = uri.Host.ToLowerInvariant();
            var local = host == "localhost" && settings.Current.Web.AllowLocalPages; // developers testing against a local server
            var allowed = body is null ? m.Permissions.Http.Concat(m.Permissions.HttpSend) : m.Permissions.HttpSend;
            if (!allowed.Any(h => h.Equals(host, StringComparison.OrdinalIgnoreCase)) && !(local && allowed.Contains("localhost")))
                throw new PluginException($"this plugin isn't allowed to {(body is null ? "read from" : "send to")} {host}.");
            if (body is not null && !sendAllowed) throw new PluginException($"sending to {host} isn't allowed until you approve the plugin.");
            if (uri.Scheme != "https" && !local) throw new PluginException("only https addresses are allowed.");
            if (!local && WebReadTool.IsPrivateAsync(uri, ct).GetAwaiter().GetResult()) throw new PluginException($"{host} points to a local address.");
            using var req = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, uri);
            if (body is not null) req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            req.Headers.UserAgent.ParseAdd($"JARVIS-plugin/{m.Id}");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).GetAwaiter().GetResult();
            var bytes = resp.Content.ReadAsByteArrayAsync(timeout.Token).GetAwaiter().GetResult();
            if (bytes.Length > MaxResponseBytes) throw new PluginException("the response was too large.");
            if (!resp.IsSuccessStatusCode) throw new PluginException($"{host} answered {(int)resp.StatusCode}.");
            log.Add($"{(body is null ? "GET" : "POST")} {uri.Host}{uri.AbsolutePath}");
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        engine.SetValue("__get", new Func<string, string>(u => Fetch(u, null)));
        engine.SetValue("__post", new Func<string, string, string>((u, b) => Fetch(u, b)));
        engine.SetValue("__sget", new Func<string, string?>(k => m.Permissions.Storage ? StorageGet(m.Id, k) : throw new PluginException("this plugin has no storage permission.")));
        engine.SetValue("__sset", new Action<string, string?>((k, v) => { if (!m.Permissions.Storage) throw new PluginException("this plugin has no storage permission."); StorageSet(m.Id, k, v); }));
        engine.SetValue("__notify", new Action<string>(t =>
        {
            if (!m.Permissions.Notify) throw new PluginException("this plugin has no notification permission.");
            if (!sendAllowed) { log.Add("notify (not shown during tests): " + t); return; }
            notifications.PostAsync(new Notification { Title = m.Name, Body = t.Length > 300 ? t[..300] : t, Source = $"plugin:{m.Id}" }, ct).GetAwaiter().GetResult();
        }));
        engine.SetValue("__log", new Action<string>(t => { if (log.Count < 100) log.Add(t.Length > 300 ? t[..300] : t); }));
        engine.Execute(Prelude);
        engine.Execute(code);
        return engine;
    }

    private string? StorageGet(string pluginId, string key)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM plugin_storage WHERE plugin_id = $p AND key = $k;";
        cmd.Parameters.AddWithValue("$p", pluginId);
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private void StorageSet(string pluginId, string key, string? value)
    {
        if (key.Length > 200) throw new PluginException("storage key is too long.");
        if (value is { Length: > MaxStorageValue }) throw new PluginException("storage value is too large.");
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("$p", pluginId);
        cmd.Parameters.AddWithValue("$k", key);
        if (value is null)
        {
            cmd.CommandText = "DELETE FROM plugin_storage WHERE plugin_id = $p AND key = $k;";
        }
        else
        {
            cmd.CommandText = "SELECT COUNT(*) FROM plugin_storage WHERE plugin_id = $p;";
            if ((long)cmd.ExecuteScalar()! >= MaxStorageKeys && StorageGet(pluginId, key) is null) throw new PluginException("storage is full.");
            cmd.CommandText = "INSERT INTO plugin_storage(plugin_id, key, value) VALUES ($p, $k, $v) ON CONFLICT(plugin_id, key) DO UPDATE SET value = $v;";
            cmd.Parameters.AddWithValue("$v", value);
        }
        cmd.ExecuteNonQuery();
    }

    public void ClearStorage(string pluginId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM plugin_storage WHERE plugin_id = $p;";
        cmd.Parameters.AddWithValue("$p", pluginId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Plain-language reasons for failures, including the sandbox's limits.</summary>
    internal static string Describe(Exception ex)
    {
        var e = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
        return e switch
        {
            PluginException p => p.Message,
            Jint.Runtime.JavaScriptException js when js.InnerException is PluginException pe => pe.Message,
            Jint.Runtime.JavaScriptException js => "script error: " + js.Message,
            TimeoutException => $"it ran longer than {TimeLimit.TotalSeconds:0} seconds and was stopped.",
            Jint.Runtime.StatementsCountOverflowException => "it ran too many steps and was stopped.",
            Jint.Runtime.MemoryLimitExceededException => "it used too much memory and was stopped.",
            Jint.Runtime.RecursionDepthOverflowException => "it recursed too deeply and was stopped.",
            _ when e.GetType().Name.Contains("Parse", StringComparison.Ordinal) => "syntax error: " + e.Message,
            _ => e.Message,
        };
    }
}
