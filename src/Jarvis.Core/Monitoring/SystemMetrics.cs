using System.Diagnostics;
using System.Net.NetworkInformation;

namespace Jarvis.Core.Monitoring;

/// <summary>One reading of the machine's health. Null means "this machine/OS doesn't report it".</summary>
public sealed record MetricsSample
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public double? CpuPercent { get; init; }
    public double? MemoryPercent { get; init; }
    public double? MemoryUsedGb { get; init; }
    public double? MemoryTotalGb { get; init; }
    public double? GpuPercent { get; init; }
    public double NetDownBytesPerSec { get; init; }
    public double NetUpBytesPerSec { get; init; }
    public int? BatteryPercent { get; init; }
    public bool? Charging { get; init; }
    public double? TemperatureC { get; init; }
    /// <summary>JARVIS's own footprint, so its cost is always visible.</summary>
    public double RuntimeMemoryMb { get; init; }
    public double RuntimeCpuPercent { get; init; }
}

public sealed record DiskInfo(string Name, string Label, string Format, double TotalGb, double FreeGb, double UsedPercent);

public sealed record ProcessInfo(int Pid, string Name, double MemoryMb, double? CpuPercent);

/// <summary>OS-specific readings. Everything is optional; the generic source fills in the rest.</summary>
public interface IMetricsSource
{
    string Name { get; }
    double? CpuPercent();
    (double UsedGb, double TotalGb, double Percent)? Memory();
    double? GpuPercent();
    (int Percent, bool Charging)? Battery();
    double? TemperatureC();
}

/// <summary>Linux /proc and /sys readings (dev and CI); returns nulls elsewhere.</summary>
public sealed class GenericMetricsSource : IMetricsSource
{
    private (long Idle, long Total)? _lastCpu;

    public string Name => OperatingSystem.IsLinux() ? "linux" : "generic";

    public double? CpuPercent()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/stat")) return null;
        var parts = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray();
        var idle = parts[3] + (parts.Length > 4 ? parts[4] : 0);
        var total = parts.Sum();
        var last = _lastCpu;
        _lastCpu = (idle, total);
        if (last is null || total == last.Value.Total) return null;
        return Math.Round(100.0 * (1 - (double)(idle - last.Value.Idle) / (total - last.Value.Total)), 1);
    }

    public (double UsedGb, double TotalGb, double Percent)? Memory()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/meminfo"))
        {
            // Any OS: the GC knows the machine's memory load.
            var gc = GC.GetGCMemoryInfo();
            if (gc.TotalAvailableMemoryBytes <= 0 || gc.MemoryLoadBytes <= 0) return null;
            var totalGb = gc.TotalAvailableMemoryBytes / 1073741824.0;
            var usedGb = gc.MemoryLoadBytes / 1073741824.0;
            return (usedGb, totalGb, Math.Round(100.0 * usedGb / totalGb, 1));
        }
        var lines = File.ReadAllLines("/proc/meminfo");
        long Kb(string key) => long.TryParse(lines.FirstOrDefault(l => l.StartsWith(key))?.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], out var v) ? v : 0;
        var total = Kb("MemTotal:");
        var avail = Kb("MemAvailable:");
        if (total <= 0) return null;
        return ((total - avail) / 1048576.0, total / 1048576.0, Math.Round(100.0 * (total - avail) / total, 1));
    }

    public double? GpuPercent() => null;

    public (int Percent, bool Charging)? Battery()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/sys/class/power_supply")) return null;
        var bat = Directory.GetDirectories("/sys/class/power_supply", "BAT*").FirstOrDefault();
        if (bat is null || !int.TryParse(Read(Path.Combine(bat, "capacity")), out var pct)) return null;
        return (pct, Read(Path.Combine(bat, "status")) is "Charging" or "Full");
    }

    public double? TemperatureC()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/sys/class/thermal")) return null;
        var temps = Directory.GetDirectories("/sys/class/thermal", "thermal_zone*")
            .Select(z => long.TryParse(Read(Path.Combine(z, "temp")), out var milli) ? milli / 1000.0 : (double?)null)
            .Where(t => t is > 0 and < 130).ToList();
        return temps.Count == 0 ? null : Math.Round(temps.Max()!.Value, 1);
    }

    private static string? Read(string path)
    {
        try { return File.ReadAllText(path).Trim(); } catch { return null; }
    }
}

/// <summary>
/// Samples machine health on demand and keeps a short history for the dashboard graphs.
/// It never runs a timer of its own; the runtime decides when to sample (only while a UI is watching).
/// </summary>
public sealed class SystemMetrics(IEnumerable<IMetricsSource> sources)
{
    public const int HistoryLength = 90;

    private readonly IMetricsSource[] _sources = sources.Reverse().Append(new GenericMetricsSource()).ToArray();
    private readonly Queue<MetricsSample> _history = new();
    private readonly object _gate = new();
    private (long Rx, long Tx, DateTimeOffset At)? _lastNet;
    private (TimeSpan Cpu, DateTimeOffset At)? _lastSelf;
    private Dictionary<int, TimeSpan> _lastProcCpu = [];
    private DateTimeOffset _lastProcAt;

    public string SourceName => _sources[0].Name;

    public MetricsSample? Latest
    {
        get { lock (_gate) return _history.LastOrDefault(); }
    }

    public IReadOnlyList<MetricsSample> History
    {
        get { lock (_gate) return _history.ToList(); }
    }

    public MetricsSample Sample()
    {
        var now = DateTimeOffset.Now;
        var mem = First(s => s.Memory());
        var battery = First(s => s.Battery());
        var (down, up) = NetworkRates(now);
        var self = Process.GetCurrentProcess();
        double selfCpu = 0;
        var cpuTime = self.TotalProcessorTime;
        if (_lastSelf is { } ls && now > ls.At)
            selfCpu = Math.Round(100.0 * (cpuTime - ls.Cpu).TotalMilliseconds / (now - ls.At).TotalMilliseconds / Environment.ProcessorCount, 1);
        _lastSelf = (cpuTime, now);

        var sample = new MetricsSample
        {
            Timestamp = now,
            CpuPercent = FirstValue(s => s.CpuPercent()),
            MemoryPercent = mem?.Percent,
            MemoryUsedGb = mem is { } m1 ? Math.Round(m1.UsedGb, 2) : null,
            MemoryTotalGb = mem is { } m2 ? Math.Round(m2.TotalGb, 1) : null,
            GpuPercent = FirstValue(s => s.GpuPercent()),
            NetDownBytesPerSec = down,
            NetUpBytesPerSec = up,
            BatteryPercent = battery?.Percent,
            Charging = battery?.Charging,
            TemperatureC = FirstValue(s => s.TemperatureC()),
            RuntimeMemoryMb = Math.Round(self.WorkingSet64 / 1048576.0, 1),
            RuntimeCpuPercent = Math.Max(0, selfCpu),
        };
        lock (_gate)
        {
            _history.Enqueue(sample);
            while (_history.Count > HistoryLength) _history.Dequeue();
        }
        return sample;
    }

    public static IReadOnlyList<DiskInfo> Disks() =>
        DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable && d.TotalSize > 1_000_000_000)
            .Select(d => new DiskInfo(d.Name, SafeLabel(d), d.DriveFormat, Math.Round(d.TotalSize / 1073741824.0, 1),
                Math.Round(d.AvailableFreeSpace / 1073741824.0, 1), Math.Round(100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize, 1)))
            .Where(d => !OperatingSystem.IsLinux() || d.Name == "/" || d.Name.StartsWith("/home") || d.Name.StartsWith("/mnt") || d.Name.StartsWith("/media"))
            .ToList();

    /// <summary>Top processes by memory, with CPU measured since the previous call (null on the first call).</summary>
    public IReadOnlyList<ProcessInfo> Processes(int limit, string sort)
    {
        var now = DateTimeOffset.Now;
        var elapsed = (now - _lastProcAt).TotalMilliseconds;
        var current = new Dictionary<int, TimeSpan>();
        var list = new List<ProcessInfo>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var cpu = p.TotalProcessorTime;
                    current[p.Id] = cpu;
                    double? pct = _lastProcCpu.TryGetValue(p.Id, out var before) && elapsed is > 200 and < 120_000
                        ? Math.Round(100.0 * (cpu - before).TotalMilliseconds / elapsed / Environment.ProcessorCount, 1)
                        : null;
                    list.Add(new ProcessInfo(p.Id, p.ProcessName, Math.Round(p.WorkingSet64 / 1048576.0, 1), pct));
                }
                catch { /* access denied or exited */ }
            }
        }
        _lastProcCpu = current;
        _lastProcAt = now;
        var sorted = sort == "cpu" ? list.OrderByDescending(p => p.CpuPercent ?? -1).ThenByDescending(p => p.MemoryMb) : list.OrderByDescending(p => p.MemoryMb);
        return sorted.Take(Math.Clamp(limit, 1, 100)).ToList();
    }

    private (double Down, double Up) NetworkRates(DateTimeOffset now)
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var st = n.GetIPStatistics();
                rx += st.BytesReceived;
                tx += st.BytesSent;
            }
        }
        catch { return (0, 0); }
        var last = _lastNet;
        _lastNet = (rx, tx, now);
        if (last is null) return (0, 0);
        var secs = Math.Max(0.001, (now - last.Value.At).TotalSeconds);
        return (Math.Max(0, (rx - last.Value.Rx) / secs), Math.Max(0, (tx - last.Value.Tx) / secs));
    }

    private T? First<T>(Func<IMetricsSource, T?> read) where T : struct
    {
        foreach (var s in _sources)
        {
            try { if (read(s) is { } v) return v; } catch { }
        }
        return null;
    }

    private double? FirstValue(Func<IMetricsSource, double?> read) => First(read);

    private static string SafeLabel(DriveInfo d)
    {
        try { return d.VolumeLabel; } catch { return ""; }
    }
}
