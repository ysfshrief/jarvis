using Jarvis.Core.Agent;
using Jarvis.Core.Companion;
using Jarvis.Core.Persistence;
using Xunit;

namespace Jarvis.Core.Tests;

public class CompanionTests
{
    [Fact]
    public void A_pairing_code_works_once_and_the_token_identifies_the_phone()
    {
        using var host = new TestHost();
        var store = host.Get<DeviceStore>();
        var (code, expires) = store.NewPairingCode();
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", code);
        Assert.True(expires > DateTimeOffset.Now.AddMinutes(4));

        var paired = store.Pair(code.ToLowerInvariant(), "  Pixel 8  ", "192.168.1.20");
        Assert.NotNull(paired);
        Assert.Equal("Pixel 8", paired.Value.Device.Name);
        Assert.Null(store.Pair(code, "Second phone", null)); // single use

        var device = store.Authenticate(paired.Value.Token, "192.168.1.21");
        Assert.Equal(paired.Value.Device.Id, device?.Id);
        Assert.Equal("192.168.1.21", device?.LastAddress);
        Assert.Null(store.Authenticate("not-a-token", null));
        Assert.Null(store.Authenticate(null, null));
    }

    [Fact]
    public void Only_a_hash_of_the_token_is_stored()
    {
        using var host = new TestHost();
        var store = host.Get<DeviceStore>();
        var (code, _) = store.NewPairingCode();
        var token = store.Pair(code, "Phone", null)!.Value.Token;

        using var conn = host.Get<JarvisDatabase>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT token_hash FROM devices;";
        var stored = (string)cmd.ExecuteScalar()!;
        Assert.DoesNotContain(token, stored);
        Assert.Equal(64, stored.Length);
    }

    [Fact]
    public void A_removed_phone_loses_access()
    {
        using var host = new TestHost();
        var store = host.Get<DeviceStore>();
        var (code, _) = store.NewPairingCode();
        var (device, token) = store.Pair(code, "Phone", null)!.Value;

        Assert.True(store.Revoke(device.Id));
        Assert.Null(store.Authenticate(token, null));
        Assert.False(store.Revoke(device.Id));
        Assert.False(store.List().Single().Active);
    }

    [Fact]
    public void Guessing_codes_cancels_the_open_code()
    {
        using var host = new TestHost();
        var store = host.Get<DeviceStore>();
        var (code, _) = store.NewPairingCode();
        for (var i = 0; i < 10; i++) Assert.Null(store.Pair("ZZZZ-ZZZZ", "x", null));
        Assert.Null(store.Pair(code, "Phone", null));

        // A new code shown on the PC works again.
        var (fresh, _) = store.NewPairingCode();
        Assert.NotNull(store.Pair(fresh, "Phone", null));
    }

    [Fact]
    public async Task What_a_phone_asks_to_change_is_always_confirmed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jarvis-phone", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        using var host = new TestHost(s =>
        {
            s.Permissions.AutoApproveSensitive = true; // normally runs without asking
            s.Files.AllowedRoots = [dir];
        }, withModel: true);
        var path = Path.Combine(dir, "note.txt");
        host.Model.CallTool("file_write", new { path, content = "from the phone" }).Reply("Done.");

        var turn = host.Say("write a note", InputSource.Remote);
        var approval = await host.AnswerNextApproval(approve: false);
        await turn;
        Assert.Equal("file_write", approval.Tool);
        Assert.Contains("paired phone", approval.Reason);
        Assert.False(File.Exists(path));

        // The same request typed on the PC runs as the user configured.
        host.Model.CallTool("file_write", new { path, content = "from the PC" }).Reply("Done.");
        await host.Say("write a note");
        Assert.Equal("from the PC", File.ReadAllText(path));
        try { Directory.Delete(dir, true); } catch { }
    }
}
