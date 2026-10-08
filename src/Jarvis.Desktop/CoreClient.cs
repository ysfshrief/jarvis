using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jarvis.Core;

namespace Jarvis.Desktop;

public sealed record RuntimeInfoDto(int Port, string Token, int Pid, string Version);

/// <summary>
/// Connection to the jarvis-core runtime: finds it via runtime.json, starts it if needed,
/// calls its API, listens to its event stream, and restarts it if it dies unexpectedly.
/// </summary>
public sealed class CoreClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly JarvisPaths _paths;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly CancellationTokenSource _cts = new();
    private RuntimeInfoDto? _info;
    private int _restarts;

    public CoreClient(JarvisPaths paths) => _paths = paths;

    public event Action<string, JsonNode?>? EventReceived;
    public event Action<bool>? ConnectionChanged;

    public bool Connected { get; private set; }
    public bool ShutdownRequested { get; set; }
    public RuntimeInfoDto? Info => _info;

    public string DashboardUrl(string page = "overview") =>
        _info is null ? "about:blank" : $"http://127.0.0.1:{_info.Port}/#token={Uri.EscapeDataString(_info.Token)}&page={page}";

    public static string CoreExePath => Path.Combine(AppContext.BaseDirectory, "jarvis-core.exe");

    /// <summary>Connect to a running runtime, starting one if necessary.</summary>
    public async Task<bool> EnsureRuntimeAsync(TimeSpan timeout)
    {
        if (await TryConnectAsync().ConfigureAwait(false)) return true;
        if (!File.Exists(CoreExePath)) return false;
        Process.Start(new ProcessStartInfo(CoreExePath, "--background") { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory })?.Dispose();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(400).ConfigureAwait(false);
            if (await TryConnectAsync().ConfigureAwait(false)) return true;
        }
        return false;
    }

    private async Task<bool> TryConnectAsync()
    {
        try
        {
            if (!File.Exists(_paths.RuntimeInfoPath)) return false;
            var info = JsonSerializer.Deserialize<RuntimeInfoDto>(await File.ReadAllTextAsync(_paths.RuntimeInfoPath).ConfigureAwait(false), Json);
            if (info is null) return false;
            using var resp = await _http.GetAsync($"http://127.0.0.1:{info.Port}/api/health").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            _info = info;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        if (_info is null) throw new InvalidOperationException("JARVIS runtime is not connected.");
        using var req = new HttpRequestMessage(method, $"http://127.0.0.1:{_info.Port}/api{path}");
        req.Headers.Add("X-Jarvis-Token", _info.Token);
        if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var node = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(node?["error"]?.GetValue<string>() ?? $"{(int)resp.StatusCode} {resp.ReasonPhrase}");
        return node;
    }

    public Task<JsonNode?> GetAsync(string path) => SendAsync(HttpMethod.Get, path);
    public Task<JsonNode?> PostAsync(string path, object? body = null, CancellationToken ct = default) => SendAsync(HttpMethod.Post, path, body ?? new { }, ct);

    /// <summary>Keeps an event-stream connection open for the life of the app; restarts the runtime if it dies.</summary>
    public async Task RunEventLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            if (_info is null && !await EnsureRuntimeAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false))
            {
                SetConnected(false);
                await Task.Delay(3000, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_info!.Port}/ws?access_token={Uri.EscapeDataString(_info.Token)}"), ct).ConfigureAwait(false);
                SetConnected(true);
                _restarts = 0;
                var buffer = new byte[64 * 1024];
                using var ms = new MemoryStream();
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buffer, 0, r.Count);
                    if (!r.EndOfMessage) continue;
                    var node = JsonNode.Parse(ms.ToArray());
                    ms.SetLength(0);
                    var type = node?["type"]?.GetValue<string>();
                    if (type is not null) EventReceived?.Invoke(type, node?["data"]);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // fall through to reconnect
            }

            SetConnected(false);
            if (ShutdownRequested) return;
            _info = null;

            // The runtime went away without being asked to: bring it back (with a limit).
            if (!await TryConnectAsync().ConfigureAwait(false) && _restarts++ < 5)
                await EnsureRuntimeAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
    }

    private void SetConnected(bool value)
    {
        if (Connected == value) return;
        Connected = value;
        ConnectionChanged?.Invoke(value);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}

/// <summary>Small per-user preferences of the desktop shell itself (orb position).</summary>
public sealed class DesktopPrefs
{
    public double? OrbLeft { get; set; }
    public double? OrbTop { get; set; }

    private static string PathFor(JarvisPaths p) => Path.Combine(p.DataDir, "desktop.json");

    public static DesktopPrefs Load(JarvisPaths p)
    {
        try { return JsonSerializer.Deserialize<DesktopPrefs>(File.ReadAllText(PathFor(p))) ?? new(); }
        catch { return new(); }
    }

    public void Save(JarvisPaths p)
    {
        try { File.WriteAllText(PathFor(p), JsonSerializer.Serialize(this)); }
        catch { /* not critical */ }
    }
}

internal static class JsonExt
{
    public static string? Str(this JsonNode? n, string key) =>
        n?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?[key]?.ToJsonString();

    public static bool Bool(this JsonNode? n, string key) =>
        n?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static int Int(this JsonNode? n, string key) =>
        n?[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    public static JsonArray Arr(this JsonNode? n) => n as JsonArray ?? new JsonArray();

    public static string[] Strings(this JsonArray a) => a.Select(x => x?.ToString() ?? "").ToArray();
}
