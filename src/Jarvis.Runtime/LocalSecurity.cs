using System.Security.Cryptography;
using System.Text;

namespace Jarvis.Runtime;

/// <summary>
/// Protects the local API. The server only listens on 127.0.0.1, and additionally:
/// - the Host header must be a loopback name (defeats DNS-rebinding from web pages),
/// - every /api and /ws request must carry the runtime token (defeats other local web pages),
/// - when a PIN is set and the session is locked, only status/unlock endpoints answer.
/// </summary>
public sealed class LocalSecurityMiddleware(RequestDelegate next, RuntimeState state)
{
    private static readonly string[] Public = ["/api/health"];
    private static readonly string[] AllowedWhileLocked = ["/api/health", "/api/status", "/api/auth/state", "/api/auth/unlock"];

    public async Task InvokeAsync(HttpContext ctx)
    {
        var host = ctx.Request.Host.Host;
        if (host is not ("127.0.0.1" or "localhost" or "[::1]" or "::1"))
        {
            ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            return;
        }

        var path = ctx.Request.Path.Value ?? "/";
        var isApi = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || path.Equals("/ws", StringComparison.OrdinalIgnoreCase);
        if (!isApi || Public.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            await next(ctx);
            return;
        }

        if (!TokenMatches(ctx, state.Token))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "Missing or invalid JARVIS token." });
            return;
        }

        if (state.Locked && !AllowedWhileLocked.Contains(path, StringComparer.OrdinalIgnoreCase) && path != "/ws")
        {
            ctx.Response.StatusCode = StatusCodes.Status423Locked;
            await ctx.Response.WriteAsJsonAsync(new { error = "JARVIS is locked. Enter your PIN.", locked = true });
            return;
        }

        if (!state.Locked) state.Touch();
        await next(ctx);
    }

    internal static bool TokenMatches(HttpContext ctx, string expected)
    {
        string? provided = null;
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) provided = auth[7..].Trim();
        provided ??= ctx.Request.Headers["X-Jarvis-Token"].FirstOrDefault();
        provided ??= ctx.Request.Query["access_token"].FirstOrDefault();
        if (string.IsNullOrEmpty(provided)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
    }
}
