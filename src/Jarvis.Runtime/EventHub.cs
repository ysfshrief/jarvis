using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Jarvis.Core.Events;

namespace Jarvis.Runtime;

/// <summary>
/// Streams every JARVIS event to connected clients (dashboard, desktop shell, later the phone)
/// over a WebSocket as JSON: {"type": "...", "data": {...}, "timestamp": "..."}.
/// </summary>
public sealed class EventHub(IEventBus events, ILogger<EventHub> logger)
{
    private int _clients;

    public int ClientCount => _clients;

    public async Task HandleAsync(HttpContext ctx, JsonSerializerOptions json, object hello)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var channel = Channel.CreateBounded<JarvisEvent>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });
        using var sub = events.Subscribe(e => channel.Writer.TryWrite(e));
        Interlocked.Increment(ref _clients);
        var ct = ctx.RequestAborted;

        try
        {
            await SendAsync(socket, new JarvisEvent("hello", hello, DateTimeOffset.Now), json, ct);
            var receive = DrainIncomingAsync(socket, ct);
            var reader = channel.Reader;
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var readTask = reader.WaitToReadAsync(ct).AsTask();
                var finished = await Task.WhenAny(readTask, receive);
                if (finished == receive) break;
                if (!await readTask) break;
                while (reader.TryRead(out var evt))
                    await SendAsync(socket, evt, json, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // client went away
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Event stream failed");
        }
        finally
        {
            Interlocked.Decrement(ref _clients);
            if (socket.State == WebSocketState.Open)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            }
        }
    }

    private static Task SendAsync(WebSocket socket, JarvisEvent evt, JsonSerializerOptions json, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { type = evt.Type, data = evt.Data, timestamp = evt.Timestamp }, json);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    /// <summary>Reads (and ignores) client messages so pings/close frames are processed.</summary>
    private static async Task DrainIncomingAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[1024];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var r = await socket.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return;
        }
    }
}
