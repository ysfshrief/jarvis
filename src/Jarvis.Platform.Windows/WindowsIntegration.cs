using System.Diagnostics;
using System.Security.Cryptography;
using Jarvis.Core;
using Jarvis.Core.Agent;
using Jarvis.Core.Events;
using Jarvis.Core.Notifications;
using Jarvis.Core.Presence;
using Jarvis.Core.Security;
using Jarvis.Core.Settings;
using Jarvis.Core.Tools.Builtin;
using Jarvis.Core.Voice;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;

namespace Jarvis.Platform.Windows;

/// <summary>Encrypts secrets with Windows DPAPI, bound to the current Windows user account.</summary>
public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "JARVIS.secrets.v1"u8.ToArray();
    public string Name => "Windows DPAPI (current user)";
    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>Shows notifications as native Windows toasts.</summary>
public sealed class ToastNotificationSink(IEventBus events, ILogger<ToastNotificationSink> logger) : INotificationSink
{
    private bool _activationHooked;

    public string Name => "windows-toast";

    public Task DeliverAsync(Notification n, DeliveryMode mode, CancellationToken ct)
    {
        if (!mode.HasFlag(DeliveryMode.Visual)) return Task.CompletedTask;
        try
        {
            if (!_activationHooked)
            {
                // Clicking a toast opens the dashboard.
                ToastNotificationManagerCompat.OnActivated += _ => events.Publish(EventTypes.UiShow, new { reason = "toast" });
                _activationHooked = true;
            }
            var builder = new ToastContentBuilder()
                .AddArgument("id", n.Id)
                .AddText(n.Title);
            if (!string.IsNullOrWhiteSpace(n.Body)) builder.AddText(n.Body);
            builder.AddAttributionText("JARVIS");
            if (n.Priority >= NotificationPriority.High) builder.SetToastScenario(ToastScenario.Reminder);
            if (n.Priority >= NotificationPriority.High) builder.AddButton(new ToastButtonDismiss());
            builder.Show();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Toast notification failed");
        }
        return Task.CompletedTask;
    }
}

/// <summary>Keeps the "start with Windows" registry entry in sync with the setting.</summary>
public sealed class StartupRegistration(ISettingsStore settings, ILogger<StartupRegistration> logger) : IHostedService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "JARVIS";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Apply(settings.Current);
        settings.Changed += Apply;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Only an installed/portable build registers itself, never a developer build.</summary>
    public static bool IsPackagedLayout() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "JARVIS.exe")) &&
        Environment.GetEnvironmentVariable("JARVIS_DEV") is null;

    private void Apply(JarvisSettings s)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "jarvis-core.exe");
            var command = $"\"{exe}\" --background";
            if (s.General.StartWithWindows && IsPackagedLayout())
            {
                if (key.GetValue(ValueName) as string != command)
                {
                    key.SetValue(ValueName, command);
                    logger.LogInformation("Registered JARVIS to start with Windows");
                }
            }
            else if (key.GetValue(ValueName) is not null && !s.General.StartWithWindows)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                logger.LogInformation("Removed JARVIS from Windows startup");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't update the startup registration");
        }
    }
}

/// <summary>
/// Starts the desktop interface (orb, tray, dashboard) next to the runtime and restarts it if it
/// crashes. The runtime keeps working if the interface is closed; a clean exit (code 0) means the
/// user chose to close the interface, so it is not restarted.
/// </summary>
public sealed class DesktopShellLauncher(ISettingsStore settings, ILogger<DesktopShellLauncher> logger) : BackgroundService
{
    private readonly Queue<DateTimeOffset> _restarts = new();

    public static string ShellPath => Path.Combine(AppContext.BaseDirectory, "JARVIS.exe");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Current.General.LaunchDesktopOnStart || !File.Exists(ShellPath)) return;
        await Task.Delay(1500, stoppingToken).ConfigureAwait(false); // let the API come up first

        while (!stoppingToken.IsCancellationRequested)
        {
            var proc = Process.GetProcessesByName("JARVIS").FirstOrDefault() ?? Launch();
            if (proc is null) return;
            await proc.WaitForExitAsync(stoppingToken).ConfigureAwait(false);
            int code;
            try { code = proc.ExitCode; } catch { code = 0; }
            proc.Dispose();
            if (code == 0) return;

            _restarts.Enqueue(DateTimeOffset.Now);
            while (_restarts.Count > 0 && DateTimeOffset.Now - _restarts.Peek() > TimeSpan.FromMinutes(10)) _restarts.Dequeue();
            if (_restarts.Count > 3)
            {
                logger.LogError("The desktop interface keeps crashing; not restarting it again");
                return;
            }
            logger.LogWarning("Desktop interface exited with code {Code}; restarting", code);
            await Task.Delay(2000, stoppingToken).ConfigureAwait(false);
        }
    }

    public static Process? Launch(string? args = null)
    {
        if (!File.Exists(ShellPath)) return null;
        return Process.Start(new ProcessStartInfo(ShellPath, args ?? "--tray") { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
    }
}

public static class WindowsPlatform
{
    /// <summary>Registers every Windows-specific capability. Call after AddJarvisCore.</summary>
    public static IServiceCollection AddWindowsPlatform(this IServiceCollection services)
    {
        // Per-monitor DPI awareness so window bounds and screenshots use real pixels.
        try { Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }

        services.AddSingleton(new PlatformInfo("windows", $"Windows ({Environment.OSVersion.Version})"));
        services.AddSingleton<ISecretProtector, DpapiProtector>();
        services.AddSingleton<IFileTrash, RecycleBinTrash>();
        services.AddSingleton<IPresenceProvider, WindowsPresenceProvider>();
        services.AddSingleton<WindowsTextToSpeech>();
        services.AddSingleton<ITextToSpeech>(sp => sp.GetRequiredService<WindowsTextToSpeech>());
        services.AddSingleton<WindowsAudioInput>();
        services.AddSingleton<IAudioInput>(sp => sp.GetRequiredService<WindowsAudioInput>());
        services.AddSingleton<Jarvis.Core.Meetings.IMeetingAudioSource, WindowsMeetingAudioSource>();
        services.AddSingleton<Jarvis.Core.Vision.IScreenCapture, WindowsScreenCapture>();
        services.AddSingleton<Jarvis.Core.Vision.ICamera, WindowsCamera>();
        services.AddSingleton<WindowsUiAutomation>();
        services.AddSingleton<INotificationSink, ToastNotificationSink>();
        services.AddSingleton<AppCatalog>();
        services.AddSingleton<Jarvis.Core.Monitoring.IMetricsSource, WindowsMetricsSource>();
        services.AddSingleton<Jarvis.Core.Files.IOcrEngine, WindowsOcrEngine>();
        services.AddHostedService<StartupRegistration>();
        services.AddHostedService<DesktopShellLauncher>();
        services.AddHostedService<AppCatalogWarmup>();

        services.AddTool<AppOpenTool>();
        services.AddTool<AppCloseTool>();
        services.AddTool<WindowListTool>();
        services.AddTool<WindowControlTool>();
        services.AddTool<ProcessListTool>();
        services.AddTool<ProcessKillTool>();
        services.AddTool<VolumeTool>();
        services.AddTool<MediaControlTool>();
        services.AddTool<ScreenshotTool>();
        services.AddTool<LockScreenTool>();
        services.AddTool<SystemPowerTool>();
        services.AddTool<ClipboardReadTool>();
        services.AddTool<ClipboardWriteTool>();
        services.AddTool<TypeTextTool>();
        services.AddTool<SendKeysTool>();
        services.AddTool<WindowsSystemInfoTool>();
        services.AddTool<UiReadTool>();
        services.AddTool<UiClickTool>();
        services.AddTool<UiTypeTool>();
        services.AddTool<MouseClickTool>();
        services.AddTool<MouseScrollTool>();
        return services;
    }
}

/// <summary>Indexes installed apps in the background at startup so the first "open X" is fast.</summary>
internal sealed class AppCatalogWarmup(AppCatalog catalog) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(3000, stoppingToken).ConfigureAwait(false);
        await catalog.RefreshAsync(force: true, stoppingToken).ConfigureAwait(false);
    }
}
