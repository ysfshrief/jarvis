using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Jarvis.Core.Web;

public sealed class CdpException(string message) : Exception(message);

/// <summary>
/// A minimal Chrome DevTools Protocol client over one WebSocket (browser-level, "flattened" sessions).
/// Works with Microsoft Edge, Chrome and Chromium; no driver or extra download needed.
/// </summary>
public sealed class CdpConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private int _nextId;
    private Task? _loop;

    /// <summary>Protocol events: method, params, session id.</summary>
    public event Action<string, JsonElement, string?>? Event;
    public bool IsOpen => _ws.State == WebSocketState.Open;

    public static async Task<CdpConnection> ConnectAsync(Uri endpoint, CancellationToken ct)
    {
        var c = new CdpConnection();
        c._ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await c._ws.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        c._loop = Task.Run(c.ReceiveLoopAsync);
        return c;
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters = null, string? sessionId = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = new Dictionary<string, object?> { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new { } };
        if (sessionId is not null) msg["sessionId"] = sessionId;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg);
        await _send.WaitAsync(ct).ConfigureAwait(false);
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { _pending.TryRemove(id, out _); throw new CdpException("The browser connection was lost."); }
        finally { _send.Release(); }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        using var reg = cts.Token.Register(() => tcs.TrySetCanceled());
        try { return await tcs.Task.ConfigureAwait(false); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new CdpException($"The browser didn't answer {method} in time."); }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open && !_stop.IsCancellationRequested)
            {
                var r = await _ws.ReceiveAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                Dispatch(ms.ToArray());
                ms.SetLength(0);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        foreach (var p in _pending.Values) p.TrySetException(new CdpException("The browser was closed."));
    }

    private void Dispatch(byte[] data)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(data).RootElement; }
        catch (JsonException) { return; }
        if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id))
        {
            if (!_pending.TryGetValue(id, out var tcs)) return;
            if (root.TryGetProperty("error", out var err))
                tcs.TrySetException(new CdpException(err.TryGetProperty("message", out var m) ? m.GetString() ?? "error" : "error"));
            else
                tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res.Clone() : default);
            return;
        }
        if (root.TryGetProperty("method", out var method))
        {
            var sid = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
            var prm = root.TryGetProperty("params", out var p) ? p.Clone() : default;
            try { Event?.Invoke(method.GetString() ?? "", prm, sid); } catch { /* a handler's bug must not kill the connection */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
        _ws.Dispose();
        if (_loop is not null) try { await _loop.ConfigureAwait(false); } catch { }
        _send.Dispose();
        _stop.Dispose();
    }
}
