using System.Diagnostics;
using Jarvis.Core.Tools;

namespace Jarvis.Platform.Windows;

public sealed class AppOpenTool(AppCatalog catalog) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "app_open",
        Category = "apps",
        Description = "Open an application, Windows settings page, folder or website by name (English or Arabic), e.g. 'VS Code', 'Calculator', 'downloads', 'bluetooth settings', 'youtube'.",
        Parameters = [new("name", "string", "App, folder, settings page or website name.", true)],
    };

    protected override string Describe(ToolArgs args) => $"Open {args.GetString("name")}";

    public override async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var name = args.RequireString("name");
        await catalog.RefreshAsync(ct: ctx.CancellationToken).ConfigureAwait(false);
        var target = catalog.Resolve(name);
        if (target is null)
        {
            var suggestions = catalog.Suggestions(name);
            var hint = suggestions.Count == 0 ? "" : ctx.T($" Did you mean {string.Join(" or ", suggestions)}?", $" تقصد {string.Join(" ولا ", suggestions)}؟");
            return ToolResult.Fail(ctx.T($"I couldn't find an app called \"{name}\" on this PC.{hint}", $"ملقتش برنامج اسمه \"{name}\" على الجهاز.{hint}"), status: ToolStatus.NotFound);
        }

        // If it's already running, bring it forward instead of starting a second copy.
        if (target.ProcessHint is { } hint2 && target.Kind is LaunchKind.App or LaunchKind.Executable)
        {
            var existing = WindowManager.List().FirstOrDefault(w => w.ProcessName.Equals(hint2, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && hint2 is not ("explorer" or "cmd" or "powershell" or "WindowsTerminal" or "notepad"))
            {
                WindowManager.Focus(existing.Handle);
                return ToolResult.Ok(ctx.T($"{target.DisplayName} was already open; I've brought it to the front.", $"{target.DisplayName} كان مفتوح أصلاً، جبتهولك قدام."),
                    new { target.DisplayName, alreadyRunning = true });
            }
        }

        var before = WindowManager.List().Select(w => w.Handle).ToHashSet();
        try
        {
            Process.Start(AppCatalog.StartInfo(target))?.Dispose();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ctx.T($"Windows refused to open {target.DisplayName}: {ex.Message}", $"الويندوز رفض يفتح {target.DisplayName}: {ex.Message}"));
        }

        if (target.Kind is LaunchKind.Website or LaunchKind.Uri)
            return ToolResult.Ok(ctx.T($"Opening {target.DisplayName}.", $"بفتحلك {target.DisplayName}."), new { target.DisplayName, target.Target });

        // Verify: wait for a new window to appear.
        var appeared = await WaitForNewWindowAsync(before, target, TimeSpan.FromSeconds(8), ctx.CancellationToken).ConfigureAwait(false);
        if (appeared is not null)
        {
            WindowManager.Focus(appeared.Handle);
            return ToolResult.Ok(ctx.T($"{target.DisplayName} is open{ctx.CommaSir}.", $"{target.DisplayName} اتفتح{ctx.CommaSir}."),
                new { target.DisplayName, window = appeared.Title, process = appeared.ProcessName });
        }
        return ToolResult.Ok(ctx.T($"I've asked Windows to open {target.DisplayName}, but no window has appeared yet — it may still be loading.",
                                   $"طلبت من الويندوز يفتح {target.DisplayName}، بس لسه مفيش شباك ظهر — ممكن لسه بيحمل."),
            new { target.DisplayName, verified = false });
    }

    private static async Task<WindowInfo?> WaitForNewWindowAsync(HashSet<nint> before, LaunchTarget target, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var nameKey = WindowManager.Norm(target.DisplayName);
        while (sw.Elapsed < timeout)
        {
            await Task.Delay(300, ct).ConfigureAwait(false);
            var now = WindowManager.List();
            var fresh = now.Where(w => !before.Contains(w.Handle)).ToList();
            var match = fresh.FirstOrDefault(w => target.ProcessHint is not null && w.ProcessName.Equals(target.ProcessHint, StringComparison.OrdinalIgnoreCase))
                        ?? fresh.FirstOrDefault(w => WindowManager.Norm(w.Title).Contains(nameKey) || nameKey.Contains(WindowManager.Norm(w.ProcessName)))
                        ?? (fresh.Count == 1 ? fresh[0] : null);
            if (match is not null) return match;
            // Already-running single-instance apps just get activated.
            var fg = WindowManager.Foreground();
            if (fg is not null && target.ProcessHint is not null && fg.ProcessName.Equals(target.ProcessHint, StringComparison.OrdinalIgnoreCase) && sw.Elapsed > TimeSpan.FromSeconds(1))
                return fg;
        }
        return null;
    }
}

public sealed class AppCloseTool : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "app_close",
        Category = "apps",
        Description = "Close an application's windows gracefully (the app may ask to save). Set force=true to terminate it (loses unsaved work; needs approval).",
        Parameters =
        [
            new("name", "string", "App name, process name or part of the window title.", true),
            new("force", "boolean", "Terminate instead of asking the app to close."),
        ],
    };

    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx)
    {
        var name = args.RequireString("name");
        return args.GetBool("force") == true
            ? new(RiskLevel.Sensitive, $"Force-close {name}", "Unsaved work in that app will be lost.")
            : new(RiskLevel.Safe, $"Close {name}");
    }

    public override async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var name = args.RequireString("name");
        var windows = WindowManager.Find(ProcessAlias(name));
        if (windows.Count == 0)
            return ToolResult.Fail(ctx.T($"I don't see {name} running.", $"مش شايف {name} شغال."), status: ToolStatus.NotFound);

        var display = windows[0].ProcessName;
        var pids = windows.Select(w => w.ProcessId).Distinct().ToList();
        if (args.GetBool("force") == true)
        {
            foreach (var pid in pids)
            {
                try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
            }
        }
        else
        {
            foreach (var w in windows) WindowManager.Close(w.Handle);
        }

        // Verify the windows actually went away.
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(250, ctx.CancellationToken).ConfigureAwait(false);
            var remaining = WindowManager.List().Where(w => windows.Any(x => x.Handle == w.Handle)).ToList();
            if (remaining.Count == 0)
                return ToolResult.Ok(ctx.T($"Closed {display}.", $"قفلت {display}."), new { process = display, windows = windows.Count });
        }
        return ToolResult.Fail(ctx.T($"{display} is still open — it's probably asking whether to save your work.", $"{display} لسه مفتوح — غالباً بيسألك تحفظ الشغل ولا لأ."));
    }

    /// <summary>Common spoken names whose process name differs.</summary>
    private static string ProcessAlias(string name)
    {
        var key = AppCatalog.Key(name);
        return key switch
        {
            "vscode" or "visualstudiocode" or "code" or "فيجوالستوديوكود" or "فياسكود" => "Code",
            "calculator" or "calc" or "الهحاسبه" or "حاسبه" => "CalculatorApp",
            "word" or "وورد" => "WINWORD",
            "powerpoint" or "باوربوينت" => "POWERPNT",
            "edge" or "ايدج" => "msedge",
            "chrome" or "كروم" => "chrome",
            "teams" or "تيمز" => "ms-teams",
            "taskmanager" or "مديرمهام" => "Taskmgr",
            "terminal" or "ترمنال" => "WindowsTerminal",
            _ => name,
        };
    }
}

public sealed class WindowListTool : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "window_list",
        Category = "apps",
        Description = "List open application windows and which one is in the foreground.",
    };

    protected override string Describe(ToolArgs args) => "List open windows";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var fg = Native.GetForegroundWindow();
        var list = WindowManager.List().Select(w => new { w.Title, w.ProcessName, w.Minimized, foreground = w.Handle == fg }).ToList();
        var lines = string.Join("\n", list.Take(12).Select(w => $"• {w.ProcessName}: {w.Title}"));
        return Task.FromResult(ToolResult.Ok(ctx.T($"{list.Count} windows open:\n{lines}", $"فيه {list.Count} شباك مفتوح:\n{lines}"), list));
    }
}

public sealed class WindowControlTool : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "window_control",
        Category = "apps",
        Description = "Focus (switch to), minimize, maximize or restore a window found by app name or title.",
        Parameters =
        [
            new("target", "string", "App name or part of the window title.", true),
            new("action", "string", "What to do.", true, ["focus", "minimize", "maximize", "restore"]),
        ],
    };

    protected override string Describe(ToolArgs args) => $"{args.GetString("action")} window {args.GetString("target")}";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var target = args.RequireString("target");
        var action = args.RequireString("action");
        var w = WindowManager.Find(target).FirstOrDefault();
        if (w is null) return Task.FromResult(ToolResult.Fail(ctx.T($"I can't find a window for \"{target}\".", $"مش لاقي شباك لـ\"{target}\"."), status: ToolStatus.NotFound));
        switch (action)
        {
            case "focus": WindowManager.Focus(w.Handle); break;
            case "minimize": WindowManager.Show(w.Handle, Native.SW_MINIMIZE); break;
            case "maximize": WindowManager.Show(w.Handle, Native.SW_MAXIMIZE); break;
            case "restore": WindowManager.Show(w.Handle, Native.SW_RESTORE); break;
            default: throw new ToolArgumentException($"Unknown action '{action}'.");
        }
        return Task.FromResult(ToolResult.Ok(ctx.T($"Done — {w.ProcessName}.", $"تمام — {w.ProcessName}."), new { w.Title, w.ProcessName, action }));
    }
}

public sealed class ProcessListTool : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "process_list",
        Category = "system",
        Description = "List running processes, optionally filtered by name, with memory use. Sorted by memory.",
        Parameters = [new("filter", "string", "Part of the process name.")],
    };

    protected override string Describe(ToolArgs args) => "List processes" + (args.GetString("filter") is { } f ? $" matching {f}" : "");

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var filter = args.GetString("filter");
        var list = Process.GetProcesses()
            .Where(p => filter is null || p.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(p =>
            {
                long mem = 0;
                try { mem = p.WorkingSet64; } catch { }
                var r = new { name = p.ProcessName, pid = p.Id, memoryMb = Math.Round(mem / 1048576.0, 1) };
                p.Dispose();
                return r;
            })
            .OrderByDescending(p => p.memoryMb).Take(60).ToList();
        var top = string.Join("\n", list.Take(8).Select(p => $"• {p.name} ({p.memoryMb:0} MB)"));
        return Task.FromResult(ToolResult.Ok(ctx.T($"Top processes by memory:\n{top}", $"أكتر البرامج استهلاكاً للرامات:\n{top}"), list));
    }
}

public sealed class ProcessKillTool : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "process_kill",
        Category = "system",
        Risk = RiskLevel.Sensitive,
        Description = "Terminate a process by name or PID. Unsaved work in it is lost. Requires approval.",
        Parameters = [new("name", "string", "Process name."), new("pid", "integer", "Process id.")],
    };

    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx)
    {
        var target = args.GetInt("pid")?.ToString() ?? args.GetString("name") ?? throw new ToolArgumentException("Give a name or pid.");
        var critical = target.ToLowerInvariant() is "csrss" or "winlogon" or "lsass" or "svchost" or "explorer" or "dwm" or "wininit" or "services" or "smss";
        return critical
            ? new(RiskLevel.Critical, $"Terminate process {target}", "This is a core Windows process; killing it can crash or log out the session.")
            : new(RiskLevel.Sensitive, $"Terminate process {target}", "Unsaved work in that program will be lost.");
    }

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var targets = new List<Process>();
        if (args.GetInt("pid") is { } pid)
        {
            if (TryGet(pid) is { } one) targets.Add(one);
        }
        else
        {
            targets.AddRange(Process.GetProcessesByName(args.RequireString("name").Replace(".exe", "", StringComparison.OrdinalIgnoreCase)));
        }
        if (targets.Count == 0) return Task.FromResult(ToolResult.Fail(ctx.T("No matching process is running.", "مفيش process بالاسم ده شغالة."), status: ToolStatus.NotFound));
        var killed = 0;
        foreach (var p in targets)
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(3000); if (p.HasExited) killed++; }
            catch { }
            finally { p.Dispose(); }
        }
        return Task.FromResult(killed == targets.Count
            ? ToolResult.Ok(ctx.T($"Terminated {killed} process(es).", $"قفلت {killed} process."), new { killed })
            : ToolResult.Fail(ctx.T($"Terminated {killed} of {targets.Count}; the rest refused (they may need administrator rights).", $"قفلت {killed} من {targets.Count}، الباقي رفض (ممكن يحتاج صلاحيات أدمن).")));
    }

    private static Process? TryGet(int pid)
    {
        try { return Process.GetProcessById(pid); } catch { return null; }
    }
}
