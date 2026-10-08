using System.Diagnostics;
using System.Runtime.InteropServices;
using Jarvis.Core.Monitoring;

namespace Jarvis.Platform.Windows;

/// <summary>
/// Windows readings: CPU from GetSystemTimes, memory from GlobalMemoryStatusEx, battery from
/// GetSystemPowerStatus and GPU load from the "GPU Engine" performance counters (3D engines, the
/// same number Task Manager shows). CPU temperature needs admin-only WMI on most PCs, so it is
/// reported as unavailable rather than guessed.
/// </summary>
public sealed class WindowsMetricsSource : IMetricsSource, IDisposable
{
    private (long Idle, long Kernel, long User)? _lastCpu;
    private List<PerformanceCounter> _gpu = [];
    private DateTimeOffset _gpuRefreshed = DateTimeOffset.MinValue;
    private bool _gpuUnavailable;

    public string Name => "windows";

    public double? CpuPercent()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var last = _lastCpu;
        _lastCpu = (idle, kernel, user);
        if (last is null) return null;
        var total = (kernel - last.Value.Kernel) + (user - last.Value.User);
        if (total <= 0) return null;
        return Math.Round(100.0 * (total - (idle - last.Value.Idle)) / total, 1);
    }

    public (double UsedGb, double TotalGb, double Percent)? Memory()
    {
        var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref mem) || mem.ullTotalPhys == 0) return null;
        var total = mem.ullTotalPhys / 1073741824.0;
        var used = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
        return (used, total, Math.Round(100.0 * used / total, 1));
    }

    public double? GpuPercent()
    {
        if (_gpuUnavailable) return null;
        try
        {
            // Engine instances come and go with processes; refresh the list every half minute.
            if (DateTimeOffset.Now - _gpuRefreshed > TimeSpan.FromSeconds(30))
            {
                if (!PerformanceCounterCategory.Exists("GPU Engine")) { _gpuUnavailable = true; return null; }
                foreach (var c in _gpu) c.Dispose();
                _gpu = new PerformanceCounterCategory("GPU Engine").GetInstanceNames()
                    .Where(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    .Select(n => new PerformanceCounter("GPU Engine", "Utilization Percentage", n, readOnly: true))
                    .ToList();
                foreach (var c in _gpu) c.NextValue(); // the first read of a rate counter is always 0
                _gpuRefreshed = DateTimeOffset.Now;
                return null;
            }
            double sum = 0;
            foreach (var c in _gpu)
            {
                try { sum += c.NextValue(); } catch (InvalidOperationException) { /* process exited */ }
            }
            return Math.Round(Math.Min(100, sum), 1);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _gpuUnavailable = true;
            return null;
        }
    }

    public (int Percent, bool Charging)? Battery()
    {
        if (!Native.GetSystemPowerStatus(out var power)) return null;
        if (power.BatteryFlag is 128 or 255 || power.BatteryLifePercent > 100) return null;
        return (power.BatteryLifePercent, power.ACLineStatus == 1);
    }

    public double? TemperatureC() => null;

    public void Dispose()
    {
        foreach (var c in _gpu) c.Dispose();
    }
}
