using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Core.Agent;
using Jarvis.Core.AI;
using Jarvis.Core.Events;
using Jarvis.Core.Settings;
using Jarvis.Core.Tools;

namespace Jarvis.Core.Tests;

/// <summary>A fake Ollama server: answers the native API from in-memory data and records requests.</summary>
internal sealed class FakeOllama : HttpMessageHandler
{
    public List<(string Path, JsonNode? Body)> Calls { get; } = [];
    public List<(string Name, string Family, string Size, string[] Caps)> Models { get; } = [];
    public Func<JsonNode, HttpResponseMessage>? Chat { get; set; }
    /// <summary>Tags that are the same model as another tag (like "qwen3:latest" and "qwen3:8b"): name → shared digest.</summary>
    public Dictionary<string, string> SharedDigests { get; } = [];
    /// <summary>Models currently in memory (what /api/ps reports).</summary>
    public List<string> Running { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var body = string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text);
        Calls.Add((path, body));
        switch (path)
        {
            case "/api/tags":
                var arr = new JsonArray(Models.Select(m => (JsonNode)new JsonObject
                {
                    ["name"] = m.Name,
                    ["digest"] = SharedDigests.GetValueOrDefault(m.Name) ?? "sha-" + m.Name,
                    ["size"] = 4_700_000_000L,
                    ["details"] = new JsonObject { ["family"] = m.Family, ["parameter_size"] = m.Size, ["quantization_level"] = "Q4_K_M" },
                }).ToArray());
                return Json(new JsonObject { ["models"] = arr }.ToJsonString());
            case "/api/show":
                var name = body!["model"]!.GetValue<string>();
                var model = Models.First(m => m.Name == name);
                return Json(new JsonObject
                {
                    ["capabilities"] = new JsonArray(model.Caps.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
                    ["model_info"] = new JsonObject { [$"{model.Family}.context_length"] = 32768 },
                }.ToJsonString());
            case "/api/chat":
                return Chat?.Invoke(body!) ?? Json("""{"message":{"role":"assistant","content":"hello"},"done":true,"prompt_eval_count":10,"eval_count":2}""");
            case "/api/pull":
                return Ndjson("""{"status":"pulling manifest"}""", """{"status":"downloading","completed":50,"total":100}""", """{"status":"success"}""");
            case "/api/ps":
                return Json(new JsonObject { ["models"] = new JsonArray(Running.Select(m => (JsonNode)new JsonObject { ["name"] = m, ["model"] = m }).ToArray()) }.ToJsonString());
            case "/api/generate":
                Running.Add(body!["model"]!.GetValue<string>());
                return Json("""{"model":"x","response":"","done":true,"done_reason":"load"}""");
            case "/api/embed":
                return Json("""{"embeddings":[[0.1,0.2],[0.3,0.4]]}""");
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Ndjson(params string[] lines) =>
        new(HttpStatusCode.OK) { Content = new StringContent(string.Join("\n", lines) + "\n", Encoding.UTF8, "application/x-ndjson") };
}

public class OllamaProviderTests
{
    private static readonly ProviderConfig Cfg = new() { Id = "ollama", Name = "Ollama", Kind = ProviderKinds.Ollama, BaseUrl = "http://127.0.0.1:11434/v1", IsLocal = true, Enabled = true };

    private static FakeOllama Server()
    {
        var f = new FakeOllama();
        f.Models.Add(("qwen2.5:7b", "qwen2", "7.6B", ["completion", "tools"]));
        f.Models.Add(("qwen2.5:0.5b", "qwen2", "494M", ["completion", "tools"]));
        f.Models.Add(("bge-m3:latest", "bert", "567M", ["embedding"]));
        f.Models.Add(("gemma2:9b", "gemma2", "9.2B", ["completion"]));
        f.Models.Add(("qwen3:8b", "qwen3", "8.2B", ["completion", "tools", "thinking"]));
        return f;
    }

    [Fact]
    public async Task Lists_models_with_reported_capabilities_and_context()
    {
        var server = Server();
        var p = new OllamaProvider(Cfg, new HttpClient(server));
        Assert.Equal("http://127.0.0.1:11434", p.BaseUrl);

        var models = await p.ListModelsAsync(default);
        var qwen = models.Single(m => m.Name == "qwen2.5:7b");
        Assert.True(qwen.Has(ModelCapabilities.Tools));
        Assert.True(qwen.CapabilitiesReported);
        Assert.Equal(32768, qwen.ContextLength);
        Assert.Equal("7.6B", qwen.ParameterSize);
        Assert.False(models.Single(m => m.Name.StartsWith("bge")).IsChatModel);

        // /api/show is cached per model digest.
        await p.ListModelsAsync(default);
        Assert.Equal(5, server.Calls.Count(c => c.Path == "/api/show"));

        var status = await p.CheckAsync(default);
        Assert.True(status.Available);
        Assert.Contains("4 chat model", status.Message);
    }

    [Fact]
    public async Task Unreachable_server_is_reported_honestly()
    {
        var p = new OllamaProvider(Cfg, new HttpClient(new ThrowingHandler()));
        var status = await p.CheckAsync(default);
        Assert.False(status.Available);
        Assert.Contains("not reachable", status.Message);
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => p.CompleteAsync(new ChatRequest { Model = "x", Messages = [ChatMessage.User("hi")] }, default));
        Assert.True(ex.Retryable);
    }

    [Fact]
    public async Task Chat_sends_context_size_tools_and_parses_tool_calls()
    {
        var server = Server();
        server.Chat = _ => FakeOllama.Json("""
            {"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"app_open","arguments":{"name":"calc"}}}]},
             "done":true,"done_reason":"stop","prompt_eval_count":120,"eval_count":9}
            """);
        var p = new OllamaProvider(Cfg, new HttpClient(server), defaultContextTokens: 8192);
        var tool = new ToolDefinition { Name = "app_open", Description = "Open", Category = "apps", Parameters = [new("name", "string", "n", true)] };

        var resp = await p.CompleteAsync(new ChatRequest
        {
            Model = "qwen2.5:7b",
            Messages = [ChatMessage.System("sys"), ChatMessage.User("open calc"), ChatMessage.Assistant(null, [new ToolCall("c0", "app_list", "{}")]), ChatMessage.ToolResult("c0", "app_list", "{}")],
            Tools = [tool],
            ContextTokens = 6000,
        }, default);

        var call = Assert.Single(resp.ToolCalls);
        Assert.Equal("app_open", call.Name);
        Assert.Equal("""{"name":"calc"}""", call.ArgumentsJson);
        Assert.Equal(120, resp.Usage!.InputTokens);

        var sent = server.Calls.Last(c => c.Path == "/api/chat").Body!;
        Assert.Equal(6000, sent["options"]!["num_ctx"]!.GetValue<int>());
        Assert.False(sent["stream"]!.GetValue<bool>());
        Assert.Equal("app_open", sent["tools"]![0]!["function"]!["name"]!.GetValue<string>());
        // Tool calls go back as objects, tool results carry the tool name.
        Assert.Equal("app_list", sent["messages"]![2]!["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("app_list", sent["messages"]![3]!["tool_name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Streams_text_deltas_and_turns_off_thinking()
    {
        var server = Server();
        server.Chat = _ => FakeOllama.Ndjson(
            """{"message":{"role":"assistant","content":"Good "},"done":false}""",
            """{"message":{"role":"assistant","content":"evening."},"done":false}""",
            """{"message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","prompt_eval_count":5,"eval_count":3}""");
        var p = new OllamaProvider(Cfg, new HttpClient(server));
        await p.ListModelsAsync(default); // learn that qwen3 thinks

        var deltas = new List<string>();
        var resp = await p.CompleteAsync(new ChatRequest { Model = "qwen3:8b", Messages = [ChatMessage.User("hi")], OnTextDelta = deltas.Add }, default);

        Assert.Equal(["Good ", "evening."], deltas);
        Assert.Equal("Good evening.", resp.Content);
        Assert.Equal(3, resp.Usage!.OutputTokens);
        var sent = server.Calls.Last(c => c.Path == "/api/chat").Body!;
        Assert.True(sent["stream"]!.GetValue<bool>());
        Assert.False(sent["think"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Reports_streaming_progress_and_loads_models_with_the_chat_context_size()
    {
        var server = Server();
        server.Chat = _ => FakeOllama.Ndjson(
            """{"message":{"role":"assistant","content":""},"done":false}""",
            """{"message":{"role":"assistant","content":"Hi."},"done":false}""",
            """{"message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}""");
        var p = new OllamaProvider(Cfg, new HttpClient(server));

        Assert.False(await p.IsLoadedAsync("qwen2.5:7b", default));
        await p.LoadAsync("qwen2.5:7b", 6000, default);
        var load = server.Calls.Single(c => c.Path == "/api/generate").Body!;
        Assert.Equal(6000, load["options"]!["num_ctx"]!.GetValue<int>()); // the same size the chat asks for, or Ollama reloads
        Assert.Null(load["prompt"]); // loads without generating anything
        Assert.True(await p.IsLoadedAsync("qwen2.5:7b", default));

        var progress = 0;
        await p.CompleteAsync(new ChatRequest { Model = "qwen2.5:7b", Messages = [ChatMessage.User("hi")], OnTextDelta = _ => { }, OnProgress = () => progress++ }, default);
        Assert.Equal(3, progress); // every chunk counts as a sign of life, even one without text (tool calls arrive that way)
    }

    [Fact]
    public async Task Two_tags_of_one_model_are_both_listed_under_their_own_names()
    {
        var server = Server();
        server.Models.Add(("qwen3:latest", "qwen3", "8.2B", ["completion", "tools", "thinking"]));
        server.SharedDigests["qwen3:latest"] = "sha-qwen3:8b";
        var p = new OllamaProvider(Cfg, new HttpClient(server));

        var names = (await p.ListModelsAsync(default)).Select(m => m.Name).ToList();
        Assert.Contains("qwen3:8b", names);
        Assert.Contains("qwen3:latest", names); // not a second "qwen3:8b", so "qwen3" still resolves to it
        Assert.Equal(5, server.Calls.Count(c => c.Path == "/api/show")); // still asked once per digest

        await p.CompleteAsync(new ChatRequest { Model = "qwen3:latest", Messages = [ChatMessage.User("hi")] }, default);
        Assert.False(server.Calls.Last(c => c.Path == "/api/chat").Body!["think"]!.GetValue<bool>()); // what /api/show said applies to both tags
    }

    [Fact]
    public async Task Model_without_tool_support_falls_back_to_plain_chat()
    {
        var server = Server();
        var attempts = 0;
        server.Chat = body =>
        {
            attempts++;
            return body["tools"] is null
                ? FakeOllama.Json("""{"message":{"role":"assistant","content":"plain answer"},"done":true}""")
                : FakeOllama.Json("""{"error":"registry.ollama.ai/library/gemma2:9b does not support tools"}""", HttpStatusCode.BadRequest);
        };
        var p = new OllamaProvider(Cfg, new HttpClient(server));
        var tool = new ToolDefinition { Name = "t", Description = "d", Category = "c" };
        var resp = await p.CompleteAsync(new ChatRequest { Model = "gemma2:9b", Messages = [ChatMessage.User("hi")], Tools = [tool] }, default);
        Assert.Equal("plain answer", resp.Content);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Missing_model_gives_an_actionable_message()
    {
        var server = Server();
        server.Chat = _ => FakeOllama.Json("""{"error":"model \"llama9\" not found, try pulling it first"}""", HttpStatusCode.NotFound);
        var p = new OllamaProvider(Cfg, new HttpClient(server));
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => p.CompleteAsync(new ChatRequest { Model = "llama9", Messages = [ChatMessage.User("hi")] }, default));
        Assert.Contains("isn't installed", ex.Message);
    }

    [Fact]
    public async Task Pulls_with_progress_and_embeds()
    {
        var server = Server();
        var p = new OllamaProvider(Cfg, new HttpClient(server));
        var seen = new List<PullProgress>();
        await p.PullAsync("qwen2.5:3b", new SyncProgress<PullProgress>(seen.Add), default);
        Assert.Contains(seen, s => s is { Status: "downloading", Completed: 50, Total: 100 });
        Assert.Equal("success", seen[^1].Status);

        var vectors = await p.EmbedAsync("bge-m3", ["a", "b"], default);
        Assert.Equal(2, vectors.Length);
        Assert.Equal(0.3f, vectors[1][0]);
    }

    [Fact]
    public void Guesses_capabilities_for_old_servers()
    {
        Assert.Contains(ModelCapabilities.Tools, OllamaProvider.GuessCapabilities("qwen2.5:7b", null));
        Assert.Contains(ModelCapabilities.Vision, OllamaProvider.GuessCapabilities("qwen2.5vl:7b", null));
        Assert.Equal([ModelCapabilities.Embedding], OllamaProvider.GuessCapabilities("nomic-embed-text", "nomic-bert"));
        Assert.DoesNotContain(ModelCapabilities.Tools, OllamaProvider.GuessCapabilities("gemma2:9b", null));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("Connection refused");
    }
}

/// <summary>IProgress that reports synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

public class OpenAiStreamingTests
{
    [Fact]
    public async Task Parses_server_sent_events_with_tool_call_fragments()
    {
        const string sse = """
            data: {"model":"local","choices":[{"index":0,"delta":{"role":"assistant","content":"On "}}]}

            data: {"choices":[{"index":0,"delta":{"content":"it."}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"app_open","arguments":"{\"na"}}]}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"me\":\"calc\"}"}}]},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        var handler = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var cfg = new ProviderConfig { Id = "lms", Name = "LM Studio", BaseUrl = "http://127.0.0.1:1234/v1", IsLocal = true, Enabled = true };
        var p = new OpenAiCompatibleProvider(cfg, () => null, new HttpClient(handler));
        var deltas = new List<string>();

        var resp = await p.CompleteAsync(new ChatRequest { Model = "local", Messages = [ChatMessage.User("open calc")], OnTextDelta = deltas.Add }, default);

        Assert.Equal(["On ", "it."], deltas);
        Assert.Equal("On it.", resp.Content);
        var call = Assert.Single(resp.ToolCalls);
        Assert.Equal("c1", call.Id);
        Assert.Equal("""{"name":"calc"}""", call.ArgumentsJson);
        Assert.Equal("tool_calls", resp.FinishReason);
        Assert.True(JsonNode.Parse(handler.LastBody!)!["stream"]!.GetValue<bool>());
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }
}

public class ModelSelectionTests
{
    private static ModelInfo M(string name, string size, params string[] caps) =>
        new() { Name = name, ParameterSize = size, Capabilities = caps, CapabilitiesReported = true };

    [Theory]
    [InlineData("moondream", "moondream:latest")]   // no tag means :latest, as in Ollama (found by the real-vision CI test)
    [InlineData("Qwen2.5:1.5B", "qwen2.5:1.5b")]
    [InlineData("qwen2.5", null)]                    // only :latest is implied, never another size
    [InlineData("llava:13b", null)]
    public void Configured_model_names_resolve_like_ollama(string configured, string? expected) =>
        Assert.Equal(expected, ModelRouter.Installed(configured, ["moondream:latest", "qwen2.5:1.5b", "llava:7b"]));

    [Fact]
    public void Picks_a_tool_capable_mid_size_chat_model()
    {
        var models = new[]
        {
            M("bge-m3:latest", "567M", "embedding"),
            M("gemma2:9b", "9.2B", "completion"),
            M("qwen2.5:0.5b", "494M", "completion", "tools"),
            M("qwen2.5:7b", "7.6B", "completion", "tools"),
            M("qwen2.5:72b", "72B", "completion", "tools"),
        };
        Assert.Equal("qwen2.5:7b", ModelRouter.PickBest(models, vision: false, null, "ollama"));
        Assert.Equal("qwen2.5:0.5b", ModelRouter.PickBest(models, false, new HashSet<string> { "ollama/qwen2.5:7b", "ollama/qwen2.5:72b" }, "ollama"));
        Assert.Null(ModelRouter.PickBest(models, vision: true, null, "ollama"));
        Assert.Equal("llava:7b", ModelRouter.PickBest([.. models, M("llava:7b", "7B", "completion", "vision")], vision: true, null, "ollama"));
    }

    [Theory]
    [InlineData("7.6B", 7.6)]
    [InlineData("494M", 0.494)]
    [InlineData("qwen2.5:14b", 14)]
    public void Parses_parameter_sizes(string text, double expected) =>
        Assert.Equal(expected, ModelRouter.ParseBillions(text.Contains(':') ? text.Split(':')[1] : text)!.Value, 3);

    [Fact]
    public void Legacy_ollama_settings_move_to_the_native_api()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jarvis-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var legacy = new JarvisSettings();
            legacy.Ai.Providers[0].Kind = ProviderKinds.OpenAiCompatible;
            legacy.Ai.Providers[0].BaseUrl = "http://127.0.0.1:11434/v1";
            File.WriteAllText(Path.Combine(dir, "settings.json"), JsonSerializer.Serialize(legacy, SettingsStore.JsonOptions));
            var store = new SettingsStore(new JarvisPaths(dir), Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsStore>.Instance);
            var ollama = store.Current.Ai.Providers.Single(p => p.Id == "ollama");
            Assert.Equal(ProviderKinds.Ollama, ollama.Kind);
            Assert.Equal("http://127.0.0.1:11434", ollama.BaseUrl);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class ContextBudgetTests
{
    [Fact]
    public void Short_conversations_are_untouched()
    {
        var fitted = ContextBudget.Fit("sys", [ChatMessage.User("hi"), ChatMessage.Assistant("hello"), ChatMessage.User("how are you")], [], 8192, 1024);
        Assert.False(fitted.Trimmed);
        Assert.Equal(4, fitted.Messages.Count);
    }

    [Fact]
    public void Long_conversations_keep_the_latest_turn_and_a_recap()
    {
        var history = new List<ChatMessage>();
        for (var i = 0; i < 30; i++)
        {
            history.Add(ChatMessage.User($"question {i} " + new string('x', 400)));
            history.Add(ChatMessage.Assistant($"answer {i} " + new string('y', 400)));
        }
        history.Add(ChatMessage.User("the latest question"));

        var fitted = ContextBudget.Fit("system prompt", history, [], 2048, 512);

        Assert.True(fitted.Trimmed);
        Assert.True(fitted.DroppedMessages > 0);
        Assert.True(fitted.EstimatedTokens <= 2048 - 512, $"estimated {fitted.EstimatedTokens}");
        Assert.Equal("the latest question", fitted.Messages[^1].Content);
        Assert.Equal(ChatRole.System, fitted.Messages[1].Role);
        Assert.Contains("Earlier in this conversation", fitted.Messages[1].Content);
        // The recap keeps the most recent dropped turns.
        var firstKept = fitted.Messages.Skip(2).First();
        Assert.Equal(ChatRole.User, firstKept.Role);
    }

    [Fact]
    public void Huge_tool_output_in_the_current_turn_is_truncated_not_dropped()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.User("read the file"),
            ChatMessage.Assistant(null, [new ToolCall("c1", "file_read", "{}")]),
            ChatMessage.ToolResult("c1", "file_read", new string('z', 40_000)),
        };
        var fitted = ContextBudget.Fit("sys", history, [], 4096, 1024);
        Assert.Equal(4, fitted.Messages.Count);
        Assert.Contains("truncated to fit", fitted.Messages[^1].Content);
        Assert.True(fitted.EstimatedTokens <= 4096);
    }

    [Fact]
    public void Arabic_counts_as_denser_than_english() =>
        Assert.True(ContextBudget.Estimate("ازيك يا جارفيس عامل ايه النهارده") > ContextBudget.Estimate("how are you doing today jarvis"));
}

public class AgentBrainTests
{
    private static List<(string Type, JsonNode Data)> Capture(TestHost host, out IDisposable sub)
    {
        var list = new List<(string, JsonNode)>();
        sub = host.Get<IEventBus>().Subscribe(e =>
        {
            if (e.Type is EventTypes.TurnPhase or EventTypes.TurnDelta)
                lock (list) list.Add((e.Type, JsonNode.Parse(JsonSerializer.Serialize(e.Data))!));
        });
        return list;
    }

    [Fact]
    public async Task Ai_turn_reports_phases_in_order()
    {
        using var host = new TestHost(withModel: true);
        host.Model.CallTool("task_list", new { }).Reply("You have no open tasks.");
        var events = Capture(host, out var sub);
        using (sub)
        {
            var r = await host.Say("what should I focus on today, given everything");
            Assert.True(r.Success);
            Assert.NotEmpty(r.TurnId);
        }
        var phases = events.Where(e => e.Type == EventTypes.TurnPhase).Select(e => e.Data["phase"]!.GetValue<string>()).ToList();
        Assert.Equal([TurnPhases.Understanding, TurnPhases.Analyzing, TurnPhases.Analyzing, TurnPhases.SelectingTool, TurnPhases.Executing, TurnPhases.Analyzing, TurnPhases.Completed], phases);
        var selecting = events.First(e => e.Data["phase"]?.GetValue<string>() == TurnPhases.SelectingTool);
        Assert.Equal("task_list", selecting.Data["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Deterministic_turn_reports_executing_and_completed()
    {
        using var host = new TestHost();
        var events = Capture(host, out var sub);
        using (sub) await host.Say("add task buy milk");
        var phases = events.Select(e => e.Data["phase"]!.GetValue<string>()).ToList();
        Assert.Equal([TurnPhases.Understanding, TurnPhases.Executing, TurnPhases.Completed], phases);
    }

    [Fact]
    public async Task Streams_reply_text_as_batched_deltas()
    {
        using var host = new TestHost(withModel: true);
        host.Model.Then(req =>
        {
            foreach (var piece in new[] { "Certainly", ", ", "Sir", "." }) req.OnTextDelta!(piece);
            return new ChatResponse { Content = "Certainly, Sir." };
        });
        var events = Capture(host, out var sub);
        using (sub) await host.Say("tell me something nice about mondays");
        var text = string.Concat(events.Where(e => e.Type == EventTypes.TurnDelta).Select(e => e.Data["text"]!.GetValue<string>()));
        Assert.Equal("Certainly, Sir.", text);
    }

    [Fact]
    public async Task Streaming_can_be_turned_off()
    {
        using var host = new TestHost(s => s.Ai.StreamResponses = false, withModel: true);
        host.Model.Reply("ok");
        await host.Say("tell me something nice about mondays");
        Assert.Null(host.Model.Requests[0].OnTextDelta);
    }

    [Fact]
    public async Task Falls_back_to_the_next_model_when_one_fails()
    {
        var broken = new ScriptedChatProvider { FailWith = new AiProviderException("Ollama returned 500.", retryable: true) };
        var healthy = new ScriptedChatProvider().Reply("Here you go.");
        using var host = new TestHost(s =>
        {
            s.Ai.Providers =
            [
                new ProviderConfig { Id = "a", Name = "A", Kind = "a", IsLocal = true, Enabled = true },
                new ProviderConfig { Id = "b", Name = "B", Kind = "b", IsLocal = true, Enabled = true },
            ];
            s.Ai.Roles = new() { [ModelRoles.General] = [new RoleBinding { Provider = "a", Model = "test-model" }, new RoleBinding { Provider = "b", Model = "test-model" }] };
        });
        host.Get<ProviderRegistry>().Factory = cfg => cfg.Kind == "a" ? new Named("a", broken) : new Named("b", healthy);
        host.Settings.Update(_ => { });

        var r = await host.Say("tell me something nice about mondays");

        Assert.True(r.Success);
        Assert.Equal("Here you go.", r.Reply);
        Assert.Equal("b/test-model", r.Model);
        Assert.Equal("a/test-model", r.FallbackFrom);
    }

    [Fact]
    public async Task Reports_failure_when_every_model_fails()
    {
        using var host = new TestHost(withModel: true);
        host.Model.FailWith = new AiProviderException("Ollama returned 500.");
        var r = await host.Say("tell me something nice about mondays");
        Assert.False(r.Success);
        Assert.Contains("500", r.Reply);
    }

    [Fact]
    public async Task Models_without_tools_get_no_tool_schemas_and_are_told_so()
    {
        var model = new CatalogProvider(new ModelInfo { Name = "gemma2:9b", Capabilities = [ModelCapabilities.Completion], CapabilitiesReported = true });
        model.Inner.Reply("I can't do that with this model.");
        using var host = new TestHost(s =>
        {
            s.Ai.Providers = [new ProviderConfig { Id = "cat", Name = "Cat", Kind = "cat", IsLocal = true, Enabled = true }];
            s.Ai.Roles = new() { [ModelRoles.General] = [new RoleBinding { Provider = "cat" }] };
        });
        host.Get<ProviderRegistry>().Factory = _ => model;
        host.Settings.Update(_ => { });

        var r = await host.Say("tell me something nice about mondays");

        Assert.Equal("cat/gemma2:9b", r.Model);
        var req = Assert.Single(model.Inner.Requests);
        Assert.Empty(req.Tools);
        Assert.Contains("cannot run tools", req.Messages[0].Content);
        Assert.Equal(8192, req.ContextTokens);
    }

    [Fact]
    public async Task Continues_a_stored_conversation_after_restart()
    {
        using var host = new TestHost(withModel: true);
        var store = host.Get<ConversationStore>();
        store.Append("old-conv", "user", "my favourite colour is teal", "en", "text");
        store.Append("old-conv", "assistant", "Noted.", "en", "text");
        host.Model.Reply("Teal.");

        await host.Agent.HandleAsync(new UserInput("what did I say my colour was?", "old-conv"));

        var msgs = host.Model.Requests[0].Messages;
        Assert.Contains(msgs, m => m.Role == ChatRole.User && m.Content == "my favourite colour is teal");
        // The new message carries JARVIS's context block first; the stored history stays exactly as said.
        Assert.StartsWith("<context>", msgs[^1].Content);
        Assert.EndsWith("</context>\n\nwhat did I say my colour was?", msgs[^1].Content);
    }

    private sealed class Named(string id, ScriptedChatProvider inner) : IChatProvider
    {
        public string Id => id;
        public string Name => id.ToUpperInvariant();
        public bool IsLocal => true;
        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct) => inner.CompleteAsync(request, ct);
        public Task<ProviderStatus> CheckAsync(CancellationToken ct) => inner.CheckAsync(ct);
    }

    private sealed class CatalogProvider(ModelInfo info) : IChatProvider, IModelCatalog
    {
        public ScriptedChatProvider Inner { get; } = new();
        public string Id => "cat";
        public string Name => "Cat";
        public bool IsLocal => true;
        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct) => Inner.CompleteAsync(request, ct);
        public Task<ProviderStatus> CheckAsync(CancellationToken ct) =>
            Task.FromResult(new ProviderStatus(Id, true, "ok", [info.Name], DateTimeOffset.Now));
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ModelInfo>>([info]);
    }
}
