using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Core.Language;
using Jarvis.Core.Memory;

namespace Jarvis.Core.Tools.Builtin;

public sealed record ProjectInfo(string Name, string Path, string Kind, string? BuildCommand, string? TestCommand, DateTime LastModified);

/// <summary>
/// Finds the user's code projects (folders with .git, package.json, *.sln, Cargo.toml, …) in the usual
/// places and in memory, and works out how to build and test them.
/// </summary>
public sealed partial class ProjectLocator(FilePolicy policy, MemoryStore memory)
{
    private static readonly string[] CommonRoots =
    [
        "source/repos", "repos", "projects", "Projects", "dev", "Dev", "code", "Code", "src", "work", "workspace",
        "Documents/GitHub", "Documents/Projects", "Documents/Visual Studio 2022/Projects", "Desktop", "Documents",
    ];

    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", "dist", "build", "target", ".venv", "venv", "__pycache__", "AppData", ".vs", ".idea",
    };

    public IReadOnlyList<ProjectInfo> Find(string? query, Settings.JarvisSettings settings, int max = 10)
    {
        var results = new Dictionary<string, ProjectInfo>(StringComparer.OrdinalIgnoreCase);

        // 1. Projects the user told JARVIS about ("remember that my CityCrep project is in D:\work\citycrep").
        foreach (var m in memory.Search(string.IsNullOrWhiteSpace(query) ? "project" : query + " project", 10))
        {
            foreach (Match pm in PathInText().Matches(m.Content))
            {
                var p = pm.Value.TrimEnd('.', ',', ')');
                if (Directory.Exists(p) && Describe(p) is { } info) results[info.Path] = info;
            }
        }

        // 2. Scan common development folders (shallow, fast).
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = CommonRoots.Select(r => System.IO.Path.Combine(home, r))
            .Concat(policy.AllowedRoots(settings))
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        foreach (var root in roots)
            Scan(new DirectoryInfo(root), 0, results, deadline);

        var q = TextNormalizer.Normalize(query ?? "").Replace(" ", "");
        var ranked = results.Values
            .Select(p => (p, score: Score(p, q)))
            .Where(x => q.Length == 0 || x.score > 0)
            .OrderByDescending(x => x.score).ThenByDescending(x => x.p.LastModified)
            .Select(x => x.p)
            .Take(max)
            .ToList();
        return ranked;
    }

    private static int Score(ProjectInfo p, string q)
    {
        if (q.Length == 0) return 1;
        var name = TextNormalizer.Normalize(p.Name).Replace(" ", "").Replace("-", "").Replace("_", "");
        var qq = q.Replace("-", "").Replace("_", "");
        if (name == qq) return 100;
        if (name.StartsWith(qq)) return 80;
        if (name.Contains(qq)) return 60;
        if (TextNormalizer.Normalize(p.Path).Replace(" ", "").Contains(qq)) return 30;
        return 0;
    }

    private void Scan(DirectoryInfo dir, int depth, Dictionary<string, ProjectInfo> found, DateTime deadline)
    {
        if (depth > 3 || DateTime.UtcNow > deadline || found.Count > 300) return;
        IEnumerable<DirectoryInfo> subs;
        try { subs = dir.EnumerateDirectories().ToList(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { return; }

        foreach (var sub in subs)
        {
            if (Skip.Contains(sub.Name) || sub.Name.StartsWith('.') || sub.Attributes.HasFlag(FileAttributes.Hidden)) continue;
            if (policy.ProtectedReason(sub.FullName) is not null) continue;
            var info = Describe(sub.FullName);
            if (info is not null)
            {
                found[info.Path] = info;
                continue; // don't descend into a project
            }
            Scan(sub, depth + 1, found, deadline);
        }
    }

    /// <summary>
    /// Editors put the folder name in the window title ("app.ts - citycrep - Visual Studio Code");
    /// use that to know which project "the build" refers to.
    /// </summary>
    public ProjectInfo? FromWindowTitle(string? title, Settings.JarvisSettings settings)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        foreach (var part in title.Split([" - ", " — ", " – "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (part.Length < 2 || part.Contains("Visual Studio") || part.Contains("Rider") || part.Contains("IntelliJ")) continue;
            var hit = Find(part.Replace("[", "").Replace("]", "").Trim(), settings, 1).FirstOrDefault();
            if (hit is not null && TextNormalizer.Normalize(hit.Name) == TextNormalizer.Normalize(part.Trim('[', ']', ' '))) return hit;
        }
        return null;
    }

    /// <summary>Recognises a project folder and its build/test commands. Returns null for ordinary folders.</summary>
    public static ProjectInfo? Describe(string path)
    {
        string? F(string name) => File.Exists(System.IO.Path.Combine(path, name)) ? name : null;
        string? Glob(string pattern)
        {
            try { return Directory.EnumerateFiles(path, pattern).Select(System.IO.Path.GetFileName).FirstOrDefault(); }
            catch { return null; }
        }

        string kind;
        string? build = null, test = null;
        if (F("package.json") is not null)
        {
            kind = "node";
            var pm = F("pnpm-lock.yaml") is not null ? "pnpm" : F("yarn.lock") is not null ? "yarn" : "npm";
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(path, "package.json")));
                if (doc.RootElement.TryGetProperty("scripts", out var scripts))
                {
                    if (scripts.TryGetProperty("build", out _)) build = $"{pm} run build";
                    if (scripts.TryGetProperty("test", out _)) test = pm == "npm" ? "npm test" : $"{pm} test";
                }
            }
            catch { /* malformed package.json: still a project */ }
        }
        else if ((Glob("*.slnx") ?? Glob("*.sln") ?? Glob("*.csproj") ?? Glob("*.fsproj")) is { } dotnet)
        {
            kind = "dotnet";
            build = $"dotnet build \"{dotnet}\"";
            test = $"dotnet test \"{dotnet}\"";
        }
        else if (F("Cargo.toml") is not null) { kind = "rust"; build = "cargo build"; test = "cargo test"; }
        else if (F("go.mod") is not null) { kind = "go"; build = "go build ./..."; test = "go test ./..."; }
        else if (F("pyproject.toml") is not null || F("requirements.txt") is not null || F("setup.py") is not null)
        {
            kind = "python";
            test = "python -m pytest";
        }
        else if (F("gradlew.bat") is not null || F("build.gradle") is not null || F("build.gradle.kts") is not null)
        {
            kind = "gradle";
            var g = OperatingSystem.IsWindows() && F("gradlew.bat") is not null ? ".\\gradlew.bat" : F("gradlew") is not null ? "./gradlew" : "gradle";
            build = $"{g} build";
            test = $"{g} test";
        }
        else if (F("pom.xml") is not null) { kind = "maven"; build = "mvn -q package"; test = "mvn -q test"; }
        else if (F("CMakeLists.txt") is not null) { kind = "cmake"; build = "cmake --build build"; }
        else if (F("Makefile") is not null) { kind = "make"; build = "make"; test = "make test"; }
        else if (Directory.Exists(System.IO.Path.Combine(path, ".git"))) kind = "git";
        else return null;

        DateTime modified;
        try { modified = Directory.GetLastWriteTime(path); } catch { modified = DateTime.MinValue; }
        return new ProjectInfo(System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar)), path, kind, build, test, modified);
    }

    [GeneratedRegex(@"(?:[A-Za-z]:\\|/|~/)[^\s""'<>|*?]+")]
    private static partial Regex PathInText();
}

public sealed class ProjectFindTool(ProjectLocator locator) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "project_find",
        Category = "dev",
        Description = "Find the user's code projects by (part of) their name, returning path, type (node, dotnet, python…) and the build/test commands. Use when the user says 'my project' or names one.",
        Parameters = [new("name", "string", "Project name or part of it. Omit to list recent projects.")],
    };

    protected override string Describe(ToolArgs args) => $"Find project {args.GetString("name") ?? "(recent)"}";

    public override async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var name = args.GetString("name");
        var found = await Task.Run(() => locator.Find(name, ctx.Settings), ctx.CancellationToken).ConfigureAwait(false);
        if (found.Count == 0)
            return ToolResult.Fail(ctx.T(
                $"I couldn't find a project{(name is null ? "" : $" called \"{name}\"")}. Tell me where it is (e.g. \"remember that my {name ?? "X"} project is in D:\\work\\{name ?? "x"}\").",
                $"ملقتش مشروع{(name is null ? "" : $" اسمه \"{name}\"")}. قولي هو فين (مثلاً: \"افتكر إن مشروع {name ?? "X"} في D:\\work\\{name ?? "x"}\")."), status: ToolStatus.NotFound);
        var lines = string.Join("\n", found.Take(5).Select(p => $"• {p.Name} ({p.Kind}) — {p.Path}"));
        return ToolResult.Ok(ctx.T($"Projects:\n{lines}", $"المشاريع:\n{lines}"), found);
    }
}

public sealed class ProjectOpenTool(ProjectLocator locator) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "project_open",
        Category = "dev",
        Description = "Open a code project by name in VS Code (or File Explorer if VS Code isn't installed).",
        Parameters =
        [
            new("name", "string", "Project name, or a folder path.", true),
            new("app", "string", "Where to open it.", false, ["vscode", "explorer", "terminal"]),
        ],
    };

    protected override string Describe(ToolArgs args) => $"Open project {args.GetString("name")}";

    public override async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var name = args.RequireString("name");
        var project = Directory.Exists(name) ? ProjectLocator.Describe(name) ?? new ProjectInfo(Path.GetFileName(name), name, "folder", null, null, DateTime.Now)
            : (await Task.Run(() => locator.Find(name, ctx.Settings, 1), ctx.CancellationToken).ConfigureAwait(false)).FirstOrDefault();
        if (project is null)
            return ToolResult.Fail(ctx.T($"I couldn't find a project called \"{name}\".", $"ملقتش مشروع اسمه \"{name}\"."), status: ToolStatus.NotFound);

        var app = args.GetString("app") ?? "vscode";
        try
        {
            switch (app)
            {
                case "explorer":
                    OpenFolder(project.Path);
                    break;
                case "terminal":
                    if (OperatingSystem.IsWindows())
                        Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{project.Path}\"") { UseShellExecute = true });
                    else OpenFolder(project.Path);
                    break;
                default:
                    if (!TryStartCode(project.Path))
                    {
                        OpenFolder(project.Path);
                        return ToolResult.Ok(ctx.T($"VS Code isn't available, so I opened {project.Name} in File Explorer.", $"الـVS Code مش موجود، ففتحت {project.Name} في مستكشف الملفات."), project);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ctx.T($"I couldn't open {project.Name}: {ex.Message}", $"مقدرتش أفتح {project.Name}: {ex.Message}"));
        }
        return ToolResult.Ok(ctx.T($"Opening {project.Name}{ctx.CommaSir}.", $"بفتحلك {project.Name}{ctx.CommaSir}."), project);
    }

    private static bool TryStartCode(string path)
    {
        // "code" is a .cmd shim on Windows, so it needs the shell.
        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", $"/d /c code \"{path}\"") { CreateNoWindow = true, UseShellExecute = false }
                : new ProcessStartInfo("code", $"\"{path}\"") { UseShellExecute = false };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(5000);
            return !p.HasExited || p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static void OpenFolder(string path)
    {
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
        else Process.Start(new ProcessStartInfo("xdg-open", path) { UseShellExecute = false });
    }
}

/// <summary>
/// Builds (or tests) a project with its own build system and extracts the errors from the output,
/// so the user — or the AI — gets "what failed and where" instead of a wall of log.
/// </summary>
public sealed partial class ProjectBuildTool(ProjectLocator locator, RunCommandTool runner, Presence.PresenceTracker presence) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "project_build",
        Category = "dev",
        Risk = RiskLevel.Sensitive,
        Description = "Build or test a code project using its own build system (npm, dotnet, cargo, gradle, …) and return the result with the extracted error lines. Needs approval because it runs project scripts.",
        Parameters =
        [
            new("name", "string", "Project name or folder path. Omit to use the project open in the editor (or the most recent one)."),
            new("action", "string", "Build or run tests.", false, ["build", "test"]),
        ],
    };

    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx)
    {
        var name = args.GetString("name") ?? "(current project)";
        var action = args.GetString("action") ?? "build";
        return new(RiskLevel.Sensitive, $"{(action == "test" ? "Test" : "Build")} project {name}", "Runs the project's build scripts on your computer.");
    }

    public override async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var name = args.GetString("name");
        var action = args.GetString("action") ?? "build";
        var guessed = name is null;
        var project = await Task.Run(() =>
            name is not null
                ? (Directory.Exists(name) ? ProjectLocator.Describe(name) : locator.Find(name, ctx.Settings, 1).FirstOrDefault())
                : locator.FromWindowTitle(presence.Current.ActiveWindowTitle, ctx.Settings) ?? locator.Find(null, ctx.Settings, 1).FirstOrDefault(),
            ctx.CancellationToken).ConfigureAwait(false);
        if (project is null)
            return ToolResult.Fail(ctx.T($"I couldn't find a project{(name is null ? "" : $" called \"{name}\"")}.", $"ملقتش مشروع{(name is null ? "" : $" اسمه \"{name}\"")}."), status: ToolStatus.NotFound);
        var which = guessed ? ctx.T($" (I assumed you meant {project.Name} in {project.Path})", $" (افترضت إنك تقصد {project.Name} في {project.Path})") : "";

        var command = action == "test" ? project.TestCommand : project.BuildCommand;
        if (command is null)
            return ToolResult.Fail(ctx.T($"I don't know how to {action} {project.Name} ({project.Kind} project).", $"معرفش أعمل {action} لـ{project.Name} (مشروع {project.Kind})."));

        // The approval for this tool covers running the command; the inner call must not ask again.
        var inner = new ToolContext { Lang = ctx.Lang, Settings = ctx.Settings, ConversationId = ctx.ConversationId, CancellationToken = ctx.CancellationToken };
        var run = await runner.ExecuteAsync(ToolArgs.From(new { command, cwd = project.Path, timeout_seconds = 900 }), inner).ConfigureAwait(false);

        var output = run.Data is null ? "" : JsonSerializer.Serialize(run.Data);
        string stdout = "", stderr = "";
        int? exit = null;
        if (run.Data is not null)
        {
            using var doc = JsonDocument.Parse(output);
            stdout = doc.RootElement.TryGetProperty("stdout", out var so) ? so.GetString() ?? "" : "";
            stderr = doc.RootElement.TryGetProperty("stderr", out var se) ? se.GetString() ?? "" : "";
            exit = doc.RootElement.TryGetProperty("exitCode", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : null;
        }
        var errors = ExtractErrors(stdout + "\n" + stderr);
        var data = new { project = project.Name, project.Path, project.Kind, command, exitCode = exit, errors, tail = Tail(stdout + "\n" + stderr, 40) };

        if (run.Success)
            return ToolResult.Ok(ctx.T($"{project.Name}: {action} succeeded{which}.", $"{project.Name}: الـ{action} نجح{which}."), data);

        var top = errors.Count == 0 ? "" : "\n" + string.Join("\n", errors.Take(5).Select(e => "• " + e));
        return new ToolResult
        {
            Success = false, Status = run.Status == ToolStatus.TimedOut ? ToolStatus.TimedOut : ToolStatus.Failed, Data = data,
            Error = $"exit code {exit}",
            Message = ctx.T($"{project.Name}: {action} failed (exit code {exit}){which}. {errors.Count} error line(s) found:{top}",
                            $"{project.Name}: الـ{action} فشل (exit code {exit}){which}. لقيت {errors.Count} سطر أخطاء:{top}"),
        };
    }

    /// <summary>Pulls compiler/test error lines out of build output (TypeScript, C#, Rust, Python, Java, Go, npm…).</summary>
    internal static List<string> ExtractErrors(string output)
    {
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var errors = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (!ErrorLine().IsMatch(l) || WarningOnly().IsMatch(l)) continue;
            var text = l.Trim();
            if (text.Length > 400) text = text[..400] + "…";
            if (!errors.Contains(text)) errors.Add(text);
            if (errors.Count >= 40) break;
        }
        return errors;
    }

    private static string Tail(string s, int n) => string.Join("\n", s.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(n));

    [GeneratedRegex(@"\berror\b|\bERR!|\bFAILED\b|\bFAIL\b|Traceback|Exception:|\bpanicked\b|cannot find|not found|Cannot find module|SyntaxError|TypeError|ReferenceError|BUILD FAILURE|\bE\d{4}\b|\bCS\d{4}\b|\bTS\d{4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLine();

    [GeneratedRegex(@"\b0 error\(s\)|\b0 errors\b|warning\b(?!.*\berror\b)", RegexOptions.IgnoreCase)]
    private static partial Regex WarningOnly();
}
