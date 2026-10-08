using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Jarvis.Core.Tools.Builtin;

/// <summary>
/// Runs a shell command (PowerShell or cmd on Windows, bash elsewhere) and returns the real
/// exit code and output. Read-only commands run freely; anything else needs approval, and
/// destructive commands are critical.
/// </summary>
public sealed partial class RunCommandTool(FilePolicy policy) : ToolBase
{
    private const int MaxOutputChars = 24_000;

    public override ToolDefinition Definition { get; } = new()
    {
        Name = "run_command",
        Category = "system",
        Risk = RiskLevel.Sensitive,
        Description = OperatingSystem.IsWindows()
            ? "Run a PowerShell (default) or cmd command on this Windows PC and get its exit code and output. Use for builds, tests, git, diagnostics. Non-read-only commands need approval."
            : "Run a bash command and get its exit code and output. Use for builds, tests, git, diagnostics. Non-read-only commands need approval.",
        Parameters =
        [
            new("command", "string", "The command line to run.", true),
            new("cwd", "string", "Working directory (default: user's home)."),
            new("shell", "string", "Shell to use.", false, OperatingSystem.IsWindows() ? ["powershell", "cmd"] : ["bash", "sh"]),
            new("timeout_seconds", "integer", "Kill the command after this many seconds (default 120, max 900)."),
        ],
    };

    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx)
    {
        var cmd = args.RequireString("command");
        var cwd = args.GetString("cwd");
        var summary = $"Run: {cmd}" + (cwd is null ? "" : $"  (in {cwd})");
        if (DestructiveRegex().IsMatch(cmd))
            return new(RiskLevel.Critical, summary, "This command can delete data or change the system.");
        if (IsReadOnly(cmd))
            return new(RiskLevel.Safe, summary);
        return new(RiskLevel.Sensitive, summary, "Runs a program on your computer.");
    }

    internal static bool IsReadOnly(string cmd)
    {
        var c = cmd.Trim();
        if (c.IndexOfAny(['|', '>', '<', ';', '&', '`', '$']) >= 0) return false;
        return ReadOnlyRegex().IsMatch(c);
    }

    public override async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var command = args.RequireString("command");
        var cwd = args.GetString("cwd") is { } c ? policy.Resolve(c) : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Directory.Exists(cwd))
            return ToolResult.Fail(ctx.T($"The folder {cwd} doesn't exist.", $"الفولدر {cwd} مش موجود."), status: ToolStatus.NotFound);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(args.GetInt("timeout_seconds") ?? 120, 5, 900));
        var shell = args.GetString("shell") ?? (OperatingSystem.IsWindows() ? "powershell" : "bash");

        var psi = BuildStartInfo(shell, command, cwd);
        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        var stdout = new BoundedBuffer(MaxOutputChars);
        var stderr = new BoundedBuffer(MaxOutputChars / 2);
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ctx.T($"I couldn't start {shell}: {ex.Message}", $"مقدرتش أشغل {shell}: {ex.Message}"));
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
        cts.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ctx.CancellationToken.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (!timedOut) throw;
        }
        process.WaitForExit(2000); // flush async readers
        sw.Stop();

        var data = new
        {
            command, cwd, shell,
            exitCode = timedOut ? (int?)null : process.ExitCode,
            timedOut,
            durationMs = sw.ElapsedMilliseconds,
            stdout = stdout.ToString(),
            stderr = stderr.ToString(),
        };

        if (timedOut)
            return new ToolResult
            {
                Success = false, Status = ToolStatus.TimedOut, Data = data, Error = "timeout",
                Message = ctx.T($"The command was still running after {timeout.TotalSeconds:0}s, so I stopped it.", $"الأمر فضل شغال أكتر من {timeout.TotalSeconds:0} ثانية فوقفته."),
            };

        var tail = LastLines(process.ExitCode == 0 ? data.stdout : (data.stderr.Length > 0 ? data.stderr : data.stdout), 6);
        if (process.ExitCode == 0)
            return ToolResult.Ok(ctx.T($"Command finished successfully.{Block(tail)}", $"الأمر خلص بنجاح.{Block(tail)}"), data);
        return new ToolResult
        {
            Success = false, Status = ToolStatus.Failed, Data = data, Error = $"exit code {process.ExitCode}",
            Message = ctx.T($"The command failed with exit code {process.ExitCode}.{Block(tail)}", $"الأمر فشل (exit code {process.ExitCode}).{Block(tail)}"),
        };
    }

    private static ProcessStartInfo BuildStartInfo(string shell, string command, string cwd)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        switch (shell)
        {
            case "cmd":
                psi.FileName = "cmd.exe";
                psi.Arguments = $"/d /s /c \"chcp 65001>nul & {command}\"";
                break;
            case "powershell":
                psi.FileName = "powershell.exe";
                psi.ArgumentList.Add("-NoLogo");
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add("[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; " + command);
                break;
            default:
                psi.FileName = shell == "sh" ? "/bin/sh" : "/bin/bash";
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(command);
                break;
        }
        return psi;
    }

    private static string LastLines(string text, int n)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("\n", lines.TakeLast(n));
    }

    private static string Block(string tail) => string.IsNullOrWhiteSpace(tail) ? "" : $"\n```\n{tail}\n```";

    [GeneratedRegex(@"\b(rm|rmdir|rd|del|erase|format|diskpart|mkfs|dd|shred|shutdown|reboot|Stop-Computer|Restart-Computer|Remove-Item|Clear-Content|Clear-RecycleBin|Remove-\w+|Uninstall-\w+|Set-ExecutionPolicy|bcdedit|takeown|icacls|cipher|vssadmin|wbadmin|net\s+user|reg\s+(delete|add|import)|sc\s+(delete|config)|git\s+(push|reset\s+--hard|clean|filter-branch)|npm\s+(publish|unpublish)|chmod\s+-R|chown\s+-R|Invoke-Expression|iex)\b|\|\s*(sh|bash|iex)\b|>\s*/dev/sd", RegexOptions.IgnoreCase)]
    private static partial Regex DestructiveRegex();

    [GeneratedRegex(@"^([\w.-]+\s+(--version|-v|-V|version)$|git\s+(status|log|diff|branch|remote\s+-v|show)|dir|ls|pwd|whoami|hostname|ipconfig|ifconfig|ping\s|tracert\s|nslookup\s|systeminfo|tasklist|ver|date\s*/t|time\s*/t|uptime|df|free|node\s+(-v|--version)|npm\s+(-v|--version|ls|list)|dotnet\s+(--info|--version|--list-sdks)|python3?\s+(-V|--version)|java\s+-version|Get-(ChildItem|Process|Date|Location|Service|ComputerInfo|NetIPAddress|Volume|PSDrive)|Test-Path|Test-Connection|where(\.exe)?\s|which\s|echo\s)", RegexOptions.IgnoreCase)]
    private static partial Regex ReadOnlyRegex();

    /// <summary>Keeps the head and tail of large output.</summary>
    private sealed class BoundedBuffer(int max)
    {
        private readonly StringBuilder _head = new();
        private readonly Queue<string> _tail = new();
        private int _tailChars;
        private bool _overflow;
        private readonly object _gate = new();

        public void AppendLine(string line)
        {
            lock (_gate)
            {
                if (!_overflow && _head.Length + line.Length < max / 2) { _head.AppendLine(line); return; }
                _overflow = true;
                _tail.Enqueue(line);
                _tailChars += line.Length + 1;
                while (_tailChars > max / 2 && _tail.Count > 1) _tailChars -= _tail.Dequeue().Length + 1;
            }
        }

        public override string ToString()
        {
            lock (_gate)
                return _overflow ? _head + "…(output truncated)…\n" + string.Join("\n", _tail) : _head.ToString().TrimEnd();
        }
    }
}
