using Jarvis.Core.Agent;
using Jarvis.Core.Tools;
using Jarvis.Core.Vision;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Core.Tests;

public sealed class FakeScreen : IScreenCapture
{
    public int Captures { get; private set; }
    public bool IsAvailable => true;
    public byte[] CapturePng(int maxSide = 1600) { Captures++; return [0x89, 0x50, 0x4E, 0x47]; }
}

public class VisionTests
{
    [Fact]
    public async Task Screen_capture_respects_privacy_and_says_when_it_needs_a_vision_model()
    {
        var screen = new FakeScreen();
        using var host = new TestHost(services: sc => sc.AddSingleton<IScreenCapture>(screen));
        var exec = host.Get<ToolExecutor>();

        var (noModel, _) = await exec.ExecuteAsync("screen_describe", new ToolArgs(), host.Ctx());
        Assert.Equal(ToolStatus.NotFound, noModel.Status);
        Assert.Contains("vision model", noModel.Message);

        host.Settings.Update(s => s.Privacy.AllowScreenCapture = false);
        var before = screen.Captures;
        var (blocked, _) = await exec.ExecuteAsync("screen_describe", new ToolArgs(), host.Ctx());
        Assert.Equal(ToolStatus.Denied, blocked.Status);
        Assert.Equal(before, screen.Captures); // nothing was captured
    }

    private sealed class FakeCamera : ICamera
    {
        public int Shots { get; private set; }
        public bool IsAvailable => true;
        public string? Name => "Test camera";
        public Task<byte[]> CaptureJpegAsync(CancellationToken ct) { Shots++; return Task.FromResult(new byte[] { 0xFF, 0xD8 }); }
    }

    [Fact]
    public async Task Camera_is_off_by_default_and_always_asks_when_on()
    {
        var cam = new FakeCamera();
        using var host = new TestHost(s => s.Permissions.AutoApproveSensitive = true, services: sc => sc.AddSingleton<ICamera>(cam));
        var exec = host.Get<ToolExecutor>();

        var (off, _) = await exec.ExecuteAsync("camera_look", ToolArgs.From(new { question = "what am I holding?" }), host.Ctx());
        Assert.Equal(ToolStatus.Denied, off.Status);
        Assert.Contains("Privacy", off.Message);

        host.Settings.Update(s => s.Privacy.AllowCamera = true);
        var call = exec.ExecuteAsync("camera_look", ToolArgs.From(new { question = "what am I holding?" }), host.Ctx());
        var ask = await host.AnswerNextApproval(approve: false);
        Assert.Equal(RiskLevel.Critical, ask.Risk);
        Assert.Equal(ToolStatus.Denied, (await call).Result.Status);
        Assert.Equal(0, cam.Shots);
    }
}
