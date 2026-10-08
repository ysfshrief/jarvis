using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jarvis.Core;
using Jarvis.Core.Settings;
using Jarvis.Runtime;
using Jarvis.Voice;
using Serilog;
#if JARVIS_WINDOWS
using Jarvis.Platform.Windows;
#endif

// jarvis-core: the always-on JARVIS runtime.
//   --background        started by Windows at logon (no window, desktop shell launched per settings)
//   --data-dir <path>   use another data folder (also: JARVIS_DATA_DIR)
//   --port <n>          override the API port

var dataDirArg = ArgValue(args, "--data-dir");
var paths = new JarvisPaths(dataDirArg);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting", Serilog.Events.LogEventLevel.Information)
    .MinimumLevel.Override("System.Net.Http", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(paths.LogsDir, "jarvis-core-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

// One runtime per data folder.
using var mutex = new Mutex(true, "Local\\JARVIS.Core." + ShortHash(paths.DataDir), out var firstInstance);
if (!firstInstance)
{
    Log.Information("JARVIS runtime is already running; asking it to show the interface");
    await AskRunningInstanceToShowUi(paths);
    await Log.CloseAndFlushAsync();
    return 0;
}

try
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        // Started at logon the working directory is System32; resolve wwwroot next to the exe.
        ContentRootPath = AppContext.BaseDirectory,
        WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
    });
    builder.Host.UseSerilog();
    builder.Host.UseConsoleLifetime(o => o.SuppressStatusMessages = true);
    builder.Services.Configure<HostOptions>(o =>
    {
        // A crashing background service must not take the whole assistant down.
        o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
        o.ShutdownTimeout = TimeSpan.FromSeconds(10);
    });

    builder.Services.AddJarvisCore(paths);
#if JARVIS_WINDOWS
    builder.Services.AddWindowsPlatform();
#endif
    builder.Services.AddJarvisVoice();

    builder.Services.AddSingleton<RuntimeState>();
    builder.Services.AddSingleton<EventHub>();
    builder.Services.AddHostedService<SchedulerService>();
    builder.Services.AddHostedService<PresenceService>();
    builder.Services.AddHostedService<ConnectivityService>();
    builder.Services.AddHostedService<RetentionService>();
    builder.Services.AddHostedService<ProviderWarmupService>();

    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    json.Converters.Add(new JsonStringEnumConverter());
    builder.Services.ConfigureHttpJsonOptions(o =>
    {
        o.SerializerOptions.Encoder = json.Encoder;
        o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

    // Port: settings (or --port), falling forward if something else already uses it.
    var settingsPeek = new SettingsStore(paths, new Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsStore>());
    var requestedPort = int.TryParse(ArgValue(args, "--port"), out var p) ? p : settingsPeek.Current.Runtime.Port;
    var port = FindFreePort(requestedPort);
    builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));

    var app = builder.Build();
    var state = app.Services.GetRequiredService<RuntimeState>();
    state.Port = port;

    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
    app.UseMiddleware<LocalSecurityMiddleware>();
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapJarvisApi(json);
    app.MapFallback(async ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        var index = Path.Combine(app.Environment.WebRootPath ?? "", "index.html");
        ctx.Response.ContentType = "text/html; charset=utf-8";
        if (File.Exists(index)) await ctx.Response.SendFileAsync(index);
        else await ctx.Response.WriteAsync("<h1>JARVIS runtime is running</h1><p>The dashboard UI has not been built. Run <code>npm run build</code> in <code>ui/</code>.</p>");
    });

    app.Lifetime.ApplicationStarted.Register(() =>
    {
        state.WriteInfo();
        Log.Information("JARVIS runtime {Version} listening on http://127.0.0.1:{Port} (data: {DataDir})", RuntimeState.Version, port, paths.DataDir);
    });
    app.Lifetime.ApplicationStopping.Register(state.DeleteInfo);

    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "JARVIS runtime crashed");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static string? ArgValue(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string ShortHash(string s) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant())))[..12];

static int FindFreePort(int preferred)
{
    for (var candidate = preferred; candidate < preferred + 20; candidate++)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, candidate);
            l.Start();
            l.Stop();
            return candidate;
        }
        catch (SocketException) { }
    }
    var any = new TcpListener(IPAddress.Loopback, 0);
    any.Start();
    var port = ((IPEndPoint)any.LocalEndpoint).Port;
    any.Stop();
    return port;
}

static async Task AskRunningInstanceToShowUi(JarvisPaths paths)
{
    var info = RuntimeState.ReadInfo(paths);
    if (info is null) return;
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{info.Port}/api/ui/show");
        req.Headers.Add("X-Jarvis-Token", info.Token);
        await http.SendAsync(req);
    }
    catch { /* the running instance will be found by the shell anyway */ }
}

public partial class Program;
