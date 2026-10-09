using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Jarvis.Core;
using Jarvis.Core.Activity;
using Jarvis.Core.Agent;
using Jarvis.Core.Companion;
using Jarvis.Core.Notifications;
using Jarvis.Core.Permissions;
using Jarvis.Core.Security;
using Jarvis.Core.Settings;
using Jarvis.Core.Tools;
using Jarvis.Core.Tools.Builtin;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Jarvis.Runtime;

/// <summary>
/// JARVIS's own TLS certificate for the companion connection: generated on this PC, never from a CA. Phones
/// pin its SHA-256 fingerprint at pairing time, so nobody else on the network can impersonate JARVIS.
/// </summary>
public sealed class CompanionCertificate(JarvisPaths paths, ISecretStore secrets)
{
    private const string Secret = "companion.certificate.password";
    private X509Certificate2? _cert;

    public string FilePath => Path.Combine(paths.DataDir, "companion.pfx");

    public X509Certificate2 Get()
    {
        if (_cert is not null) return _cert;
        var password = secrets.Get(Secret);
        if (password is not null && File.Exists(FilePath))
        {
            try { return _cert = X509CertificateLoader.LoadPkcs12FromFile(FilePath, password); }
            catch (CryptographicException) { /* unreadable: make a new one */ }
        }
        password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=JARVIS companion", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        foreach (var ip in CompanionServer.LanAddresses()) san.AddIpAddress(ip);
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var created = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
        File.WriteAllBytes(FilePath, created.Export(X509ContentType.Pfx, password));
        secrets.Set(Secret, password);
        return _cert = X509CertificateLoader.LoadPkcs12FromFile(FilePath, password);
    }

    /// <summary>SHA-256 of the certificate, as phones pin it ("AB:CD:…").</summary>
    public string Fingerprint() => string.Join(':', SHA256.HashData(Get().RawData).Select(b => b.ToString("X2")));
}

/// <summary>
/// A second, separate web server for paired phones — started only when Settings → Devices → Companion is on.
/// It serves nothing but the companion page and a deliberately small API (status, briefing, chat, approvals,
/// notifications). Every request needs a paired device's token, is rate limited and is audited; the main
/// dashboard API never leaves 127.0.0.1.
/// </summary>
public sealed class CompanionServer(IServiceProvider sp, ISettingsStore settings, CompanionCertificate certificate, ILogger<CompanionServer> logger) : IHostedService, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;
    private int _port;

    public bool Running => _app is not null;
    public int Port => _port;
    public string? Error { get; private set; }

    public async Task StartAsync(CancellationToken ct)
    {
        settings.Changed += s => _ = ApplyAsync(s);
        await ApplyAsync(settings.Current);
    }

    public async Task StopAsync(CancellationToken ct) => await StopServerAsync();

    private async Task ApplyAsync(JarvisSettings s)
    {
        await _gate.WaitAsync();
        try
        {
            var want = s.Companion.Enabled;
            if (want && (_app is null || _port != s.Companion.Port))
            {
                await StopServerUnlockedAsync();
                await StartServerAsync(s.Companion.Port);
            }
            else if (!want && _app is not null) await StopServerUnlockedAsync();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            logger.LogWarning(ex, "Companion server failed to start");
        }
        finally { _gate.Release(); }
        sp.GetRequiredService<Jarvis.Core.Events.IEventBus>().Publish(Jarvis.Core.Events.EventTypes.DevicesChanged, new { running = Running, error = Error });
    }

    private async Task StartServerAsync(int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = 64 * 1024;
            k.Listen(IPAddress.Any, port, o => { o.Protocols = HttpProtocols.Http1AndHttp2; o.UseHttps(certificate.Get()); });
        });
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?", _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
        });
        var app = builder.Build();
        app.UseRateLimiter();
        Map(app);
        await app.StartAsync();
        _app = app;
        _port = port;
        Error = null;
        sp.GetRequiredService<ActivityLog>().Record(ActivityKinds.System, $"Phone companion listening on port {port}", status: "ok");
    }

    private async Task StopServerAsync()
    {
        await _gate.WaitAsync();
        try { await StopServerUnlockedAsync(); }
        finally { _gate.Release(); }
    }

    private async Task StopServerUnlockedAsync()
    {
        if (_app is null) return;
        try { await _app.StopAsync(); await _app.DisposeAsync(); } catch { }
        _app = null;
    }

    public async ValueTask DisposeAsync() => await StopServerAsync();

    /// <summary>LAN IPv4 addresses phones can reach (Wi-Fi/Ethernet, up, not virtual adapters where possible).</summary>
    public static IReadOnlyList<IPAddress> LanAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Where(n => !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254."))
            .Distinct().ToList();

    private void Map(WebApplication app)
    {
        var web = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        // The companion page and its assets only — the dashboard isn't served here.
        app.MapGet("/", () => Results.Redirect("/companion"));
        app.MapGet("/companion", () => File.Exists(Path.Combine(web, "companion.html")) ? Results.File(Path.Combine(web, "companion.html"), "text/html; charset=utf-8") : Results.NotFound());
        app.MapGet("/assets/{file}", (string file) =>
        {
            if (file.Contains("..") || file.Contains('/') || file.Contains('\\')) return Results.NotFound();
            var path = Path.Combine(web, "assets", file);
            var type = Path.GetExtension(file) switch { ".js" => "text/javascript", ".css" => "text/css", ".woff2" => "font/woff2", ".woff" => "font/woff", ".svg" => "image/svg+xml", ".png" => "image/png", _ => "application/octet-stream" };
            return File.Exists(path) ? Results.File(path, type) : Results.NotFound();
        });

        var api = app.MapGroup("/companion/api");
        api.MapPost("/pair", (PairDto dto, HttpContext ctx) =>
        {
            var store = sp.GetRequiredService<DeviceStore>();
            var r = store.Pair(dto.Code ?? "", dto.DeviceName ?? "Phone", ctx.Connection.RemoteIpAddress?.ToString());
            if (r is null) return Results.Json(new { error = "That code is wrong or expired. Show a new one on the PC." }, statusCode: 403);
            sp.GetRequiredService<ActivityLog>().Record(ActivityKinds.System, $"Phone paired: {r.Value.Device.Name}", status: "ok", details: ctx.Connection.RemoteIpAddress?.ToString());
            return Results.Ok(new { token = r.Value.Token, device = r.Value.Device.Name, name = "JARVIS" });
        });

        var authed = api.MapGroup("").AddEndpointFilter(async (ic, next) =>
        {
            var ctx = ic.HttpContext;
            var header = ctx.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : null;
            var device = sp.GetRequiredService<DeviceStore>().Authenticate(token, ctx.Connection.RemoteIpAddress?.ToString());
            if (device is null) return Results.Json(new { error = "This phone isn't paired (or was removed)." }, statusCode: 401);
            ctx.Items["device"] = device;
            return await next(ic);
        });

        authed.MapGet("/status", () =>
        {
            var s = settings.Current;
            var approvals = sp.GetRequiredService<ApprovalBroker>();
            var recorder = sp.GetRequiredService<Jarvis.Core.Meetings.MeetingRecorder>();
            return Results.Ok(new
            {
                name = "JARVIS", version = RuntimeState.Version, online = sp.GetRequiredService<Jarvis.Core.Connectivity.IConnectivity>().IsOnline,
                pendingApprovals = approvals.Pending.Count, recording = recorder.Current?.Title, allowApprovals = s.Companion.AllowApprovals,
                honorific = s.General.Honorific, language = s.General.Language,
            });
        });
        authed.MapGet("/briefing", () =>
        {
            var tool = sp.GetRequiredService<BriefingTool>();
            var ctx = Ctx(null);
            return Results.Ok(new { text = BriefingTool.Render(tool.Build(DateTimeOffset.Now, false), ctx) });
        });
        authed.MapPost("/chat", async (CompanionChatDto dto, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Text) || dto.Text.Length > 4000) return Results.BadRequest(new { error = "Say something (up to 4000 characters)." });
            var device = (PairedDevice)http.Items["device"]!;
            var result = await sp.GetRequiredService<AgentOrchestrator>().HandleAsync(new UserInput(dto.Text, $"phone-{device.Id}", InputSource.Remote), CancellationToken.None);
            return Results.Ok(new { result.Reply, result.Success, steps = result.Steps.Select(st => new { st.Tool, st.Summary, status = st.Status.ToString() }) });
        });
        authed.MapGet("/approvals", () => Results.Ok(sp.GetRequiredService<ApprovalBroker>().Pending.Select(a => new { a.Id, a.Tool, risk = a.Risk.ToString(), a.Summary, a.Reason, a.ExpiresAt })));
        authed.MapPost("/approvals/{id}", (string id, CompanionDecisionDto dto, HttpContext http) =>
        {
            if (!settings.Current.Companion.AllowApprovals) return Results.Json(new { error = "Approving from the phone is turned off on the PC." }, statusCode: 403);
            var device = (PairedDevice)http.Items["device"]!;
            var broker = sp.GetRequiredService<ApprovalBroker>();
            var pending = broker.Pending.FirstOrDefault(a => a.Id == id);
            if (pending is null || !broker.Resolve(id, dto.Approve)) return Results.NotFound();
            sp.GetRequiredService<ActivityLog>().Record(ActivityKinds.Approval, $"{(dto.Approve ? "Approved" : "Refused")} from {device.Name}: {pending.Summary}", pending.Tool, pending.Risk.ToString(), dto.Approve ? "approved" : "denied");
            return Results.Ok();
        });
        authed.MapGet("/notifications", (int? limit) =>
            Results.Ok(sp.GetRequiredService<NotificationCenter>().Recent(Math.Clamp(limit ?? 30, 1, 100)).Select(n => new { n.Id, n.Title, n.Body, priority = n.Priority.ToString(), n.Source, n.Timestamp, n.Status })));
    }

    private ToolContext Ctx(string? conversation) => new()
    {
        Lang = settings.Current.General.Language == "ar" ? Jarvis.Core.Language.Lang.Ar : Jarvis.Core.Language.Lang.En,
        Settings = settings.Current, ConversationId = conversation ?? "phone", Via = "phone",
    };
}

public sealed record PairDto(string? Code, string? DeviceName);
public sealed record CompanionChatDto(string? Text);
public sealed record CompanionDecisionDto(bool Approve);
