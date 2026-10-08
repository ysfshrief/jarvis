using Jarvis.Core.Agent;
using Jarvis.Core.Tools;
using Jarvis.Core.Tools.Builtin;

namespace Jarvis.Core.Tests;

public sealed class ProjectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-projects", Guid.NewGuid().ToString("n"));

    public ProjectTests() => Directory.CreateDirectory(_root);

    private string MakeProject(string name, params (string File, string Content)[] files)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        foreach (var (f, c) in files) File.WriteAllText(Path.Combine(dir, f), c);
        return dir;
    }

    [Fact]
    public void Recognises_project_types_and_their_commands()
    {
        var node = ProjectLocator.Describe(MakeProject("web", ("package.json", """{"scripts":{"build":"vite build","test":"vitest"}}"""), ("pnpm-lock.yaml", "")))!;
        Assert.Equal("node", node.Kind);
        Assert.Equal("pnpm run build", node.BuildCommand);
        Assert.Equal("pnpm test", node.TestCommand);

        var dotnet = ProjectLocator.Describe(MakeProject("api", ("Api.csproj", "<Project/>")))!;
        Assert.Equal("dotnet", dotnet.Kind);
        Assert.Equal("dotnet build \"Api.csproj\"", dotnet.BuildCommand);

        Assert.Equal("rust", ProjectLocator.Describe(MakeProject("tool", ("Cargo.toml", "")))!.Kind);
        Assert.Equal("python", ProjectLocator.Describe(MakeProject("ml", ("pyproject.toml", "")))!.Kind);
        Assert.Null(ProjectLocator.Describe(MakeProject("notes", ("todo.txt", "x"))));
    }

    [Fact]
    public async Task Finds_projects_by_name_in_allowed_folders()
    {
        MakeProject("citycrep-portal", ("package.json", "{}"));
        MakeProject("other", ("go.mod", "module x"));
        using var host = new TestHost(s => s.Files.AllowedRoots = [_root]);
        var (result, _) = await host.Get<ToolExecutor>().ExecuteAsync("project_find", ToolArgs.From(new { name = "citycrep" }), host.Ctx());
        Assert.True(result.Success, result.Message);
        Assert.Contains("citycrep-portal", result.Message);
        Assert.DoesNotContain("other", result.Message);
    }

    [Fact]
    public void Editor_window_title_identifies_the_current_project()
    {
        MakeProject("citycrep-portal", ("package.json", "{}"));
        using var host = new TestHost(s => s.Files.AllowedRoots = [_root]);
        var p = host.Get<ProjectLocator>().FromWindowTitle("app.ts - citycrep-portal - Visual Studio Code", host.Settings.Current);
        Assert.Equal("citycrep-portal", p?.Name);
    }

    [Fact]
    public async Task Failing_build_reports_the_extracted_errors()
    {
        if (OperatingSystem.IsWindows()) return; // the Windows CI job covers PowerShell; this uses a POSIX shell
        var dir = MakeProject("broken", ("package.json", """
            {"scripts":{"build":"node -e \"console.log('compiling...'); console.error('src/app.ts(3,5): error TS2304: Cannot find name foo.'); process.exit(2)\""}}
            """));
        using var host = new TestHost(s => s.Permissions.AutoApproveSensitive = true);

        var (result, step) = await host.Get<ToolExecutor>().ExecuteAsync("project_build", ToolArgs.From(new { name = dir }), host.Ctx());

        Assert.False(result.Success);
        Assert.Equal(ToolStatus.Failed, step.Status);
        Assert.Contains("TS2304", result.Message);
        Assert.Contains("exit code 2", result.Message);
    }

    [Fact]
    public async Task Building_asks_for_approval_by_default()
    {
        var dir = MakeProject("ok", ("package.json", """{"scripts":{"build":"node -e \"process.exit(0)\""}}"""));
        using var host = new TestHost();
        var turn = host.Get<ToolExecutor>().ExecuteAsync("project_build", ToolArgs.From(new { name = dir }), host.Ctx());
        var approval = await host.AnswerNextApproval(approve: false);
        var (result, _) = await turn;
        Assert.Equal("project_build", approval.Tool);
        Assert.Equal(ToolStatus.Denied, result.Status);
    }

    [Theory]
    [InlineData("src/app.ts(3,5): error TS2304: Cannot find name 'foo'.", true)]
    [InlineData("Program.cs(10,13): error CS0103: The name 'x' does not exist", true)]
    [InlineData("npm ERR! code ELIFECYCLE", true)]
    [InlineData("Traceback (most recent call last):", true)]
    [InlineData("error[E0425]: cannot find value `y` in this scope", true)]
    [InlineData("    0 Error(s)", false)]
    [InlineData("warning CS8618: Non-nullable property", false)]
    [InlineData("Compiled successfully in 2.3s", false)]
    public void Extracts_error_lines(string line, bool isError)
    {
        var errors = ProjectBuildTool.ExtractErrors("start\n" + line + "\nend");
        Assert.Equal(isError, errors.Count == 1);
    }

    [Theory]
    [InlineData("open my project citycrep", "project_open", "citycrep")]
    [InlineData("open project CityCrep Portal", "project_open", "CityCrep Portal")]
    [InlineData("افتح مشروع citycrep", "project_open", "citycrep")]
    [InlineData("build citycrep", "project_build", "citycrep")]
    [InlineData("run tests for citycrep", "project_build", "citycrep")]
    [InlineData("why is the build failing in citycrep", "project_build", "citycrep")]
    [InlineData("ليه البيلد بيفشل في citycrep", "project_build", "citycrep")]
    public void Project_intents(string text, string tool, string name)
    {
        var intent = Assert.IsType<ToolIntent>(IntentEngine.Match(text, DateTimeOffset.Now));
        Assert.Equal(tool, intent.Tool);
        Assert.Equal(name, intent.Args.GetString("name"));
    }

    [Fact]
    public void Why_questions_prefer_the_ai_and_may_omit_the_project()
    {
        var intent = Assert.IsType<ToolIntent>(IntentEngine.Match("why is the build failing?", DateTimeOffset.Now));
        Assert.True(intent.PreferAi);
        Assert.Null(intent.Args.GetString("name"));
        Assert.Equal("test", Assert.IsType<ToolIntent>(IntentEngine.Match("run tests for citycrep", DateTimeOffset.Now)).Args.GetString("action"));
    }

    [Fact]
    public async Task Why_questions_go_to_the_ai_with_the_real_build_output()
    {
        if (OperatingSystem.IsWindows()) return; // POSIX shell build script, as above
        MakeProject("citycrep", ("package.json", """
            {"scripts":{"build":"node -e \"console.error('src/app.ts(3,5): error TS2304: Cannot find name foo.'); process.exit(2)\""}}
            """));
        using var host = new TestHost(s => { s.Files.AllowedRoots = [_root]; s.Permissions.AutoApproveSensitive = true; }, withModel: true);
        host.Model.Reply("`foo` isn't declared in src/app.ts line 3.");
        var result = await host.Say("why is the build failing in citycrep");
        Assert.Equal("ai", result.Route);
        Assert.Contains(result.Steps, st => st.Tool == "project_build");
        // The model explained the build it was shown, not one it imagined.
        Assert.Contains(host.Model.Requests[0].Messages, m => m.Content?.Contains("TS2304") == true);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
