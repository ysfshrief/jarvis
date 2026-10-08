using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Core;
using Jarvis.Core.Security;
using Jarvis.Core.Settings;

namespace Jarvis.Runtime;

/// <summary>Written to the data folder so the desktop shell (and other local clients) can find and authenticate to the runtime.</summary>
public sealed record RuntimeInfo(int Port, string Token, int Pid, string Version, DateTimeOffset StartedAt);

/// <summary>Process-wide runtime facts: access token, port, pause and PIN-lock state.</summary>
public sealed class RuntimeState(JarvisPaths paths, ISettingsStore settings)
{
    private DateTimeOffset _unlockedUntil = DateTimeOffset.MinValue;

    /// <summary>Fresh random token each start; only processes that can read the user's data folder learn it.</summary>
    public string Token { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public int Port { get; set; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public bool Paused { get; set; }

    public static string Version { get; } =
        typeof(RuntimeState).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public bool PinSet => !string.IsNullOrEmpty(settings.Current.Security.PinHash);

    public bool Locked => PinSet && DateTimeOffset.Now > _unlockedUntil;

    public bool TryUnlock(string pin)
    {
        if (!PinSet) return true;
        if (!PinHasher.Verify(pin, settings.Current.Security.PinHash)) return false;
        Touch();
        return true;
    }

    /// <summary>Extend the unlocked session on activity.</summary>
    public void Touch()
    {
        if (PinSet) _unlockedUntil = DateTimeOffset.Now.AddMinutes(Math.Max(1, settings.Current.Security.UnlockMinutes));
    }

    public void Lock() => _unlockedUntil = DateTimeOffset.MinValue;

    public void WriteInfo()
    {
        var info = new RuntimeInfo(Port, Token, Environment.ProcessId, Version, StartedAt);
        var tmp = paths.RuntimeInfoPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(info, SettingsStore.JsonOptions));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, paths.RuntimeInfoPath, overwrite: true);
    }

    public void DeleteInfo()
    {
        try
        {
            if (File.Exists(paths.RuntimeInfoPath))
            {
                var current = JsonSerializer.Deserialize<RuntimeInfo>(File.ReadAllText(paths.RuntimeInfoPath), SettingsStore.JsonOptions);
                if (current?.Pid == Environment.ProcessId) File.Delete(paths.RuntimeInfoPath);
            }
        }
        catch { /* best effort */ }
    }

    public static RuntimeInfo? ReadInfo(JarvisPaths paths)
    {
        try
        {
            return File.Exists(paths.RuntimeInfoPath)
                ? JsonSerializer.Deserialize<RuntimeInfo>(File.ReadAllText(paths.RuntimeInfoPath), SettingsStore.JsonOptions)
                : null;
        }
        catch { return null; }
    }
}
