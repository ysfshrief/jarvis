using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Jarvis.Core.Tools.Builtin;

/// <summary>Cross-platform system overview. The Windows layer replaces it with a richer version (battery, CPU load).</summary>
public sealed class SystemInfoTool : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "system_info",
        Category = "system",
        Description = "Get the computer's status: OS, uptime, memory, disk space, CPU.",
    };

    protected override string Describe(ToolArgs args) => "Read system status";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var info = Collect();
        var msg = ctx.T(
            $"{info.Os}. Up {Fmt(info.Uptime)}. Memory: {info.MemoryUsedPercent}% used of {info.TotalMemoryGb:0.#} GB. Free disk on {info.SystemDrive}: {info.SystemDriveFreeGb:0.#} GB.",
            $"{info.Os}. شغال من {Fmt(info.Uptime)}. الرامات: {info.MemoryUsedPercent}% مستخدم من {info.TotalMemoryGb:0.#} جيجا. المساحة الفاضية على {info.SystemDrive}: {info.SystemDriveFreeGb:0.#} جيجا.");
        return Task.FromResult(ToolResult.Ok(msg, info));
    }

    public sealed record SystemSnapshot(
        string Os, string Machine, string User, int Cpus, string Architecture, TimeSpan Uptime,
        double TotalMemoryGb, int MemoryUsedPercent, string SystemDrive, double SystemDriveFreeGb, double SystemDriveTotalGb,
        IReadOnlyList<object> Drives);

    public static SystemSnapshot Collect()
    {
        var gc = GC.GetGCMemoryInfo();
        var total = gc.TotalAvailableMemoryBytes;
        var usedPercent = 0;
        double totalGb = total / 1024d / 1024 / 1024;
        if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
        {
            var lines = File.ReadAllLines("/proc/meminfo");
            long Kb(string key) => long.TryParse(lines.FirstOrDefault(l => l.StartsWith(key))?.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], out var v) ? v : 0;
            var t = Kb("MemTotal:");
            var a = Kb("MemAvailable:");
            if (t > 0) { totalGb = t / 1024d / 1024; usedPercent = (int)Math.Round(100 - 100d * a / t); }
        }
        else if (gc.MemoryLoadBytes > 0 && gc.HighMemoryLoadThresholdBytes > 0)
        {
            usedPercent = (int)Math.Round(100d * gc.MemoryLoadBytes / total);
        }

        var sysRoot = Path.GetPathRoot(Environment.SystemDirectory is { Length: > 0 } sd ? sd : "/") ?? "/";
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d => (object)new { name = d.Name, label = d.VolumeLabel, freeGb = Math.Round(d.AvailableFreeSpace / 1e9, 1), totalGb = Math.Round(d.TotalSize / 1e9, 1) })
            .Take(12).ToList();
        double free = 0, size = 0;
        try
        {
            var sys = new DriveInfo(sysRoot);
            free = sys.AvailableFreeSpace / 1e9;
            size = sys.TotalSize / 1e9;
        }
        catch { /* not ready */ }

        return new SystemSnapshot(
            RuntimeInformation.OSDescription.Trim(), Environment.MachineName, Environment.UserName, Environment.ProcessorCount,
            RuntimeInformation.OSArchitecture.ToString(), TimeSpan.FromMilliseconds(Environment.TickCount64),
            Math.Round(totalGb, 1), usedPercent, sysRoot, Math.Round(free, 1), Math.Round(size, 1), drives);
    }

    public static string Fmt(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m";
}

public sealed class OpenUrlTool : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "open_url",
        Category = "web",
        Description = "Open a web page in the user's default browser.",
        Parameters = [new("url", "string", "http(s) URL to open.", true)],
    };

    protected override string Describe(ToolArgs args) => $"Open {args.GetString("url")}";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var raw = args.RequireString("url");
        if (!raw.Contains("://")) raw = "https://" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Task.FromResult(ToolResult.Fail(ctx.T("That isn't a valid web address.", "ده مش لينك صحيح.")));
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("xdg-open", uri.AbsoluteUri) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail(ctx.T($"I couldn't open the browser: {ex.Message}", $"مقدرتش أفتح المتصفح: {ex.Message}")));
        }
        return Task.FromResult(ToolResult.Ok(ctx.T($"Opened {uri.Host}.", $"فتحت {uri.Host}."), new { url = uri.AbsoluteUri }));
    }
}
