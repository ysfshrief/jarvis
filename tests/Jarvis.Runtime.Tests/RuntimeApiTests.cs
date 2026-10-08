using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Runtime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Jarvis.Runtime.Tests;

/// <summary>Boots the real runtime (all services, real database in a temp folder) behind an in-memory server.</summary>
public sealed class RuntimeFixture : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "jarvis-runtime-tests", Guid.NewGuid().ToString("n"));

    public RuntimeFixture()
    {
        Environment.SetEnvironmentVariable("JARVIS_DATA_DIR", DataDir);
    }

    public string Token => Services.GetRequiredService<RuntimeState>().Token;

    public HttpClient Authed()
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Jarvis-Token", Token);
        return c;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDir, true); } catch { }
    }
}

public class RuntimeApiTests : IClassFixture<RuntimeFixture>
{
    private readonly RuntimeFixture _f;
    public RuntimeApiTests(RuntimeFixture f) => _f = f;

    [Fact]
    public async Task Health_is_public()
    {
        var resp = await _f.CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Api_requires_the_runtime_token()
    {
        var anon = await _f.CreateClient().GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        var wrong = _f.CreateClient();
        wrong.DefaultRequestHeaders.Add("X-Jarvis-Token", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/memory")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await _f.Authed().GetAsync("/api/status")).StatusCode);
    }

    [Fact]
    public async Task Foreign_host_headers_are_rejected()
    {
        var c = _f.Authed();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        req.Headers.Host = "attacker.example";
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await c.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Chat_runs_the_agent()
    {
        var resp = await _f.Authed().PostAsJsonAsync("/api/chat", new { text = "add task Review the CityCrep contract" });
        resp.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("deterministic", body.GetProperty("route").GetString());
        Assert.Equal("Ok", body.GetProperty("steps")[0].GetProperty("status").GetString());

        var tasks = await _f.Authed().GetFromJsonAsync<JsonElement>("/api/tasks");
        Assert.Contains(tasks.EnumerateArray(), t => t.GetProperty("title").GetString() == "Review the CityCrep contract");
    }

    [Fact]
    public async Task Memory_crud_and_clear_requires_confirmation()
    {
        var c = _f.Authed();
        var created = await (await c.PostAsJsonAsync("/api/memory", new { content = "Prefers Arabic replies in the evening", kind = "preference" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();

        var found = await c.GetFromJsonAsync<JsonElement>("/api/memory?q=arabic");
        Assert.Contains(found.EnumerateArray(), m => m.GetProperty("id").GetString() == id);

        Assert.Equal(HttpStatusCode.BadRequest, (await c.DeleteAsync("/api/memory")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync($"/api/memory/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/memory", new { content = "x", kind = "nonsense" })).StatusCode);
    }

    [Fact]
    public async Task Secrets_are_write_only()
    {
        var c = _f.Authed();
        (await c.PutAsJsonAsync("/api/secrets/test_key", new { value = "sk-secret-value" })).EnsureSuccessStatusCode();
        var list = await c.GetStringAsync("/api/secrets");
        Assert.Contains("test_key", list);
        Assert.DoesNotContain("sk-secret-value", list);
        Assert.DoesNotContain("sk-secret-value", await c.GetStringAsync("/api/settings"));
        (await c.DeleteAsync("/api/secrets/test_key")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Settings_round_trip_and_cannot_overwrite_the_pin()
    {
        var c = _f.Authed();
        var settings = await c.GetFromJsonAsync<JsonElement>("/api/settings");
        var json = settings.GetRawText().Replace("\"honorific\": \"Sir\"", "\"honorific\": \"Boss\"").Replace("\"honorific\":\"Sir\"", "\"honorific\":\"Boss\"");
        var doc = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        doc["general"]!["honorific"] = "Boss";
        doc["security"]!["pinHash"] = "forged";
        var put = await c.PutAsync("/api/settings", new StringContent(doc.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        put.EnsureSuccessStatusCode();
        var after = await c.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("Boss", after.GetProperty("general").GetProperty("honorific").GetString());
        Assert.NotEqual("forged", after.GetProperty("security").GetProperty("pinHash").GetString());

        doc["general"]!["honorific"] = "Sir";
        (await c.PutAsync("/api/settings", new StringContent(doc.ToJsonString(), System.Text.Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Pin_lock_blocks_the_api_until_unlocked()
    {
        var c = _f.Authed();
        (await c.PutAsJsonAsync("/api/auth/pin", new { newPin = "4321" })).EnsureSuccessStatusCode();
        try
        {
            await c.PostAsync("/api/auth/lock", null);
            Assert.Equal(HttpStatusCode.Locked, (await c.GetAsync("/api/memory")).StatusCode);
            var status = await c.GetFromJsonAsync<JsonElement>("/api/status");
            Assert.True(status.GetProperty("locked").GetBoolean());

            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/auth/unlock", new { pin = "0000" })).StatusCode);
            (await c.PostAsJsonAsync("/api/auth/unlock", new { pin = "4321" })).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/memory")).StatusCode);
        }
        finally
        {
            (await c.PutAsJsonAsync("/api/auth/pin", new { currentPin = "4321", newPin = "" })).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Ai_models_are_listed_with_recommendations_and_bad_pulls_rejected()
    {
        var c = _f.Authed();
        var models = await c.GetFromJsonAsync<JsonElement>("/api/ai/models");
        Assert.Contains(models.GetProperty("recommended").EnumerateArray(), m => m.GetProperty("name").GetString() == "qwen2.5:7b");
        var ollama = models.GetProperty("providers").EnumerateArray().Single(p => p.GetProperty("provider").GetString() == "ollama");
        Assert.True(ollama.GetProperty("canPull").GetBoolean());

        var bad = await c.PostAsJsonAsync("/api/ai/providers/ollama/pull", new { model = "rm -rf /" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var unknown = await c.PostAsJsonAsync("/api/ai/providers/nope/pull", new { model = "qwen2.5:3b" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task System_metrics_disks_and_processes_are_real()
    {
        var c = _f.Authed();
        await c.GetFromJsonAsync<JsonElement>("/api/system/metrics");
        await Task.Delay(1100); // samples are cached for a second
        var m = await c.GetFromJsonAsync<JsonElement>("/api/system/metrics");
        var cur = m.GetProperty("current");
        Assert.True(cur.GetProperty("runtimeMemoryMb").GetDouble() > 10);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
        {
            Assert.True(cur.GetProperty("memoryTotalGb").GetDouble() > 0.5);
            Assert.InRange(cur.GetProperty("cpuPercent").GetDouble(), 0, 100);
        }
        Assert.True(m.GetProperty("history").GetArrayLength() >= 2);
        var disks = await c.GetFromJsonAsync<JsonElement>("/api/system/disks");
        Assert.True(disks.GetArrayLength() >= 1);
        var procs = await c.GetFromJsonAsync<JsonElement>("/api/system/processes?limit=5");
        Assert.Equal(5, procs.GetArrayLength());
    }

    [Fact]
    public async Task Tools_capabilities_and_status_are_exposed()
    {
        var c = _f.Authed();
        var tools = await c.GetFromJsonAsync<JsonElement>("/api/tools");
        Assert.Contains(tools.EnumerateArray(), t => t.GetProperty("name").GetString() == "run_command" && t.GetProperty("risk").GetString() == "Sensitive");
        var caps = await c.GetFromJsonAsync<JsonElement>("/api/capabilities");
        Assert.True(caps.GetArrayLength() > 10);
        var status = await c.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.False(status.GetProperty("locked").GetBoolean());
        Assert.True(status.TryGetProperty("voice", out _));
    }

    [Fact]
    public async Task Event_stream_says_hello_and_relays_events()
    {
        var ws = _f.Server.CreateWebSocketClient();
        var socket = await ws.ConnectAsync(new Uri(_f.Server.BaseAddress, $"/ws?access_token={_f.Token}"), CancellationToken.None);
        var hello = await Receive(socket);
        Assert.Equal("hello", hello.GetProperty("type").GetString());

        await _f.Authed().PostAsJsonAsync("/api/chat", new { text = "what time is it" });
        for (var i = 0; i < 20; i++)
        {
            var evt = await Receive(socket);
            if (evt.GetProperty("type").GetString() == "agent.turn.completed")
            {
                Assert.StartsWith("It's", evt.GetProperty("data").GetProperty("reply").GetString());
                return;
            }
        }
        Assert.Fail("No turn-completed event received.");
    }

    private static async Task<JsonElement> Receive(System.Net.WebSockets.WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        System.Net.WebSockets.WebSocketReceiveResult r;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        do
        {
            r = await socket.ReceiveAsync(buffer, cts.Token);
            ms.Write(buffer, 0, r.Count);
        } while (!r.EndOfMessage);
        return JsonDocument.Parse(ms.ToArray()).RootElement;
    }
}
