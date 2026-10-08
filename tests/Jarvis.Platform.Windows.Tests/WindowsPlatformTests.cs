using System.Diagnostics;
using Jarvis.Core;
using Jarvis.Core.Agent;
using Jarvis.Core.Connectivity;
using Jarvis.Core.Language;
using Jarvis.Core.Presence;
using Jarvis.Core.Security;
using Jarvis.Core.Settings;
using Jarvis.Core.Tools;
using Jarvis.Core.Tools.Builtin;
using Jarvis.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Jarvis.Platform.Windows.Tests;

/// <summary>
/// Exercises the real Windows integration on a real Windows session. These run in CI on a
/// GitHub-hosted Windows runner; some hardware (speakers, microphone) may be absent there, so
/// those tests only require a graceful result, never a crash.
/// </summary>
public sealed class WindowsPlatformTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "jarvis-win-tests", Guid.NewGuid().ToString("n"));
    private readonly ServiceProvider _sp;

    public WindowsPlatformTests(ITestOutputHelper output)
    {
        _out = output;
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        sc.AddJarvisCore(new JarvisPaths(_dataDir));
        sc.AddWindowsPlatform();
        _sp = sc.BuildServiceProvider();
        _sp.GetRequiredService<ISettingsStore>().Update(s =>
        {
            s.Permissions.AutoApproveSensitive = true;
            s.Ai.Providers = [];
        });
        _sp.GetRequiredService<ConnectivityMonitor>().Set(true);
    }

    private ToolContext Ctx => new() { Lang = Lang.En, Settings = _sp.GetRequiredService<ISettingsStore>().Current, ConversationId = "win-test" };

    private Task<(ToolResult Result, ToolStep Step)> Run(string tool, object args) =>
        _sp.GetRequiredService<ToolExecutor>().ExecuteAsync(tool, ToolArgs.From(args), Ctx);

    [Fact]
    public async Task Windows_ocr_reads_text_in_an_image_and_documents_are_indexed()
    {
        var ocr = _sp.GetRequiredService<Jarvis.Core.Files.IOcrEngine>();
        _out.WriteLine($"OCR: {ocr.Name}, available={ocr.IsAvailable}");
        if (!ocr.IsAvailable) return; // no OCR language installed on this image: nothing to verify
        var png = Path.Combine(_dataDir, "ocr-test.png");
        using (var bmp = new System.Drawing.Bitmap(900, 220))
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        using (var font = new System.Drawing.Font("Segoe UI", 44, System.Drawing.FontStyle.Bold))
        {
            g.Clear(System.Drawing.Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.DrawString("Invoice CityCrep 2026", font, System.Drawing.Brushes.Black, 20, 60);
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        }
        var text = await ocr.RecognizeAsync(png, default);
        _out.WriteLine($"OCR text: {text}");
        Assert.Contains("CityCrep", text ?? "", StringComparison.OrdinalIgnoreCase);

        // The same image becomes findable by its text through the file index.
        _sp.GetRequiredService<ISettingsStore>().Update(s => { s.Files.IndexEnabled = true; s.Files.IndexRoots = [_dataDir]; });
        var indexer = _sp.GetRequiredService<Jarvis.Core.Files.FileIndexer>();
        await indexer.ScanAsync(default);
        var hits = await _sp.GetRequiredService<Jarvis.Core.Files.FileIndex>().SearchAsync("citycrep invoice", 5, null, default);
        Assert.Contains(hits, h => h.File.Name == "ocr-test.png");
    }

    [Fact]
    public void Windows_tools_are_registered_and_replace_generic_ones()
    {
        var registry = _sp.GetRequiredService<IToolRegistry>();
        foreach (var name in new[] { "app_open", "app_close", "window_list", "volume", "screenshot", "system_power", "keyboard_type" })
            Assert.NotNull(registry.Find(name));
        Assert.IsType<WindowsSystemInfoTool>(registry.Find("system_info"));
    }

    [Theory]
    [InlineData("calculator", "calc.exe")]
    [InlineData("الآلة الحاسبة", "calc.exe")]
    [InlineData("Notepad", "notepad.exe")]
    [InlineData("النوت باد", "notepad.exe")]
    [InlineData("task manager", "taskmgr.exe")]
    [InlineData("youtube", "https://www.youtube.com")]
    [InlineData("bluetooth settings", "ms-settings:bluetooth")]
    [InlineData("github.com", "https://github.com")]
    public void App_names_resolve_in_both_languages(string name, string target)
    {
        var catalog = _sp.GetRequiredService<AppCatalog>();
        var t = catalog.Resolve(name);
        Assert.NotNull(t);
        Assert.Equal(target, t.Target);
    }

    [Fact]
    public async Task Start_menu_apps_can_be_indexed()
    {
        var catalog = _sp.GetRequiredService<AppCatalog>();
        await catalog.RefreshAsync(force: true);
        _out.WriteLine($"Indexed {catalog.Apps.Count} Start-menu apps");
        foreach (var a in catalog.Apps.Take(10)) _out.WriteLine($"  {a.Name} -> {a.AppId}");
    }

    [Fact]
    public async Task Open_and_close_notepad_end_to_end()
    {
        foreach (var p in Process.GetProcessesByName("notepad")) { try { p.Kill(); } catch { } }

        var (opened, _) = await Run("app_open", new { name = "notepad" });
        _out.WriteLine(opened.Message);
        Assert.True(opened.Success, opened.Message);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && Process.GetProcessesByName("notepad").Length == 0) await Task.Delay(200);
        Assert.NotEmpty(Process.GetProcessesByName("notepad"));

        var windows = WindowManager.List();
        _out.WriteLine("Windows: " + string.Join(" | ", windows.Select(w => $"{w.ProcessName}:{w.Title}")));
        if (windows.Any(w => w.ProcessName.Equals("notepad", StringComparison.OrdinalIgnoreCase)))
        {
            var (closed, _) = await Run("app_close", new { name = "notepad" });
            _out.WriteLine(closed.Message);
            Assert.True(closed.Success, closed.Message);
        }
        foreach (var p in Process.GetProcessesByName("notepad")) { try { p.Kill(); } catch { } }
    }

    [Fact]
    public async Task Screenshot_is_saved_as_png()
    {
        var (result, _) = await Run("screenshot", new { });
        Assert.True(result.Success, result.Message);
        var path = System.Text.Json.JsonSerializer.SerializeToElement(result.Data).GetProperty("path").GetString()!;
        Assert.True(new FileInfo(path).Length > 1000);
        _out.WriteLine(path);
        File.Delete(path);
    }

    [Fact]
    public async Task PowerShell_commands_report_exit_codes()
    {
        var (ok, _) = await Run("run_command", new { command = "Write-Output 'hello from jarvis'" });
        Assert.True(ok.Success, ok.Message);
        Assert.Contains("hello from jarvis", ok.Message);

        var (bad, _) = await Run("run_command", new { command = "exit 3" });
        Assert.False(bad.Success);
        Assert.Contains("exit code 3", bad.Message);

        var (cmd, _) = await Run("run_command", new { command = "echo from cmd", shell = "cmd" });
        Assert.True(cmd.Success, cmd.Message);
    }

    [Fact]
    public async Task Failing_project_build_reports_errors_through_powershell()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jarvis-win-proj-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"),
            "{\"scripts\":{\"build\":\"node -e \\\"console.error('src/app.ts(3,5): error TS2304: Cannot find name foo.'); process.exit(2)\\\"\"}}");
        try
        {
            var (result, _) = await Run("project_build", new { name = dir });
            _out.WriteLine(result.Message);
            Assert.False(result.Success);
            Assert.Contains("TS2304", result.Message);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task System_info_reads_real_values()
    {
        var (result, _) = await Run("system_info", new { });
        Assert.True(result.Success, result.Message);
        Assert.Contains("Windows", result.Message);
        _out.WriteLine(result.Message);
    }

    [Fact]
    public async Task Volume_and_clipboard_fail_gracefully_without_hardware()
    {
        var (vol, _) = await Run("volume", new { action = "get" });
        _out.WriteLine("volume: " + vol.Message);
        var (clip, _) = await Run("clipboard_write", new { text = "jarvis clipboard test" });
        _out.WriteLine("clipboard: " + clip.Message);
        if (clip.Success)
        {
            var (read, _) = await Run("clipboard_read", new { });
            Assert.Contains("jarvis clipboard test", System.Text.Json.JsonSerializer.Serialize(read.Data));
        }
    }

    [Fact]
    public void Presence_sampling_works()
    {
        var snap = _sp.GetRequiredService<IPresenceProvider>().Sample();
        _out.WriteLine($"{snap.State} app={snap.ActiveProcess} title={snap.ActiveWindowTitle} idle={snap.IdleSeconds}s fullscreen={snap.IsFullscreen} mic={snap.MicrophoneInUse}");
        Assert.NotEqual(UserState.Unknown, snap.State);
    }

    [Fact]
    public void Dpapi_protects_secrets()
    {
        var store = _sp.GetRequiredService<ISecretStore>();
        Assert.Contains("DPAPI", store.ProtectionName);
        store.Set("k", "v-123");
        Assert.Equal("v-123", store.Get("k"));
    }

    [Fact]
    public void Deleted_files_go_to_the_recycle_bin()
    {
        var file = Path.Combine(Path.GetTempPath(), $"jarvis-recycle-{Guid.NewGuid():n}.txt");
        File.WriteAllText(file, "x");
        _sp.GetRequiredService<IFileTrash>().Trash(file);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Speech_voices_can_be_listed()
    {
        var tts = _sp.GetRequiredService<WindowsTextToSpeech>();
        _out.WriteLine($"{tts.Voices.Count} voices: " + string.Join(", ", tts.Voices.Select(v => $"{v.Name} ({v.Language})")));
    }

    public void Dispose()
    {
        _sp.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch { }
    }
}
