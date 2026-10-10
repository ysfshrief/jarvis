using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Core.AI;
using Jarvis.Core.Security;
using Jarvis.Core.Settings;
using Jarvis.Core.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Core.Tests;

/// <summary>Google Gemini through its OpenAI-compatible endpoint, with the user's own key.</summary>
public sealed class GeminiTests
{
    /// <summary>Answers like Gemini's OpenAI-compatible API, and checks the key the way it does.</summary>
    private sealed class FakeGemini : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, JsonNode? Body)> Calls { get; } = [];
        /// <summary>Answer the first message with a call to this tool (signed, as Gemini 3 models do).</summary>
        public string? CallTool { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var text = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var body = text.Length == 0 ? null : JsonNode.Parse(text);
            lock (Calls) Calls.Add((request, body));
            if (request.Headers.Authorization?.ToString() != "Bearer AIza-test-key")
                return Json("""[{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT"}}]""", HttpStatusCode.BadRequest);
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1beta/openai/models")
                return Json("""{"object":"list","data":[{"id":"models/gemini-flash-latest","object":"model"},{"id":"models/text-embedding-004","object":"model"}]}""");
            if (path == "/v1beta/openai/chat/completions")
            {
                // Gemini refuses a function whose parameters are an object with no properties.
                foreach (var tool in body?["tools"]?.AsArray() ?? [])
                    if (tool?["function"]?["parameters"] is JsonObject p && p["properties"] is JsonObject { Count: 0 })
                        return Json("""[{"error":{"code":400,"message":"* GenerateContentRequest.tools[0].function_declarations[0].parameters.properties: should be non-empty for OBJECT type"}}]""", HttpStatusCode.BadRequest);
                var messages = body!["messages"]!.AsArray();
                var stream = body["stream"]?.GetValue<bool>() == true;
                if (CallTool is not null && !messages.Any(m => m?["role"]?.GetValue<string>() == "tool"))
                {
                    var call = "{\"id\":\"function-call-1\",\"type\":\"function\",\"function\":{\"name\":\"" + CallTool +
                               "\",\"arguments\":\"{}\"},\"extra_content\":{\"google\":{\"thought_signature\":\"sig-from-gemini\"}}}";
                    return stream
                        ? Sse("{\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[" + call.Insert(1, "\"index\":0,") + "]},\"finish_reason\":\"tool_calls\"}]}")
                        : Json("{\"choices\":[{\"index\":0,\"finish_reason\":\"tool_calls\",\"message\":{\"role\":\"assistant\",\"tool_calls\":[" + call + "]}}]}");
                }
                // Gemini 3 refuses a tool result unless the call it answers carries the signature Gemini gave it.
                foreach (var m in messages.Where(m => m?["tool_calls"] is not null))
                    if (m!["tool_calls"]![0]!["extra_content"]?["google"]?["thought_signature"]?.GetValue<string>() is not ("sig-from-gemini" or "skip_thought_signature_validator"))
                        return Json("""[{"error":{"code":400,"message":"Function call is missing a thought_signature in functionCall parts. This is required for tools to work correctly."}}]""", HttpStatusCode.BadRequest);
                return stream
                    ? Sse("""{"choices":[{"index":0,"delta":{"role":"assistant","content":"Hello from Gemini."},"finish_reason":"stop"}]}""")
                    : Json("""{"model":"gemini-flash-latest","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"Hello from Gemini."}}],"usage":{"prompt_tokens":9,"completion_tokens":4}}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
            new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Sse(string chunk) =>
            new(HttpStatusCode.OK) { Content = new StringContent($"data: {chunk}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
    }

    private static TestHost GeminiHost(FakeGemini gemini, bool stream = true)
    {
        var host = new TestHost(s =>
        {
            s.Ai.AllowCloud = true;
            s.Ai.StreamResponses = stream;
            s.Ai.Providers = AiSettings.BuiltInProviders();
            foreach (var p in s.Ai.Providers) p.Enabled = p.Id == AiSettings.GeminiId; // only Gemini, as for someone without a local model
            s.Ai.Roles = new AiSettings().Roles;
        }, services: sc => sc.AddSingleton(new HttpClient(gemini)));
        host.Get<ProviderRegistry>().Factory = null;
        host.Settings.Update(_ => { });
        host.Get<ISecretStore>().Set("gemini_api_key", "AIza-test-key"); // what "Save key" in Settings → AI stores
        return host;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_tool_result_goes_back_with_the_signature_gemini_gave_the_call(bool stream)
    {
        var gemini = new FakeGemini { CallTool = "task_list" };
        using var host = GeminiHost(gemini, stream);

        var r = await host.Say("what's on my list of things to do, roughly?");

        Assert.True(r.Success, r.Reply);
        Assert.Contains(r.Steps, s => s.Tool == "task_list");
        Assert.Equal("Hello from Gemini.", r.Reply);
        var second = gemini.Calls.Last().Body!["messages"]!.AsArray().Single(m => m?["tool_calls"] is not null)!;
        Assert.Equal("sig-from-gemini", second["tool_calls"]![0]!["extra_content"]!["google"]!["thought_signature"]!.GetValue<string>());
    }

    [Fact]
    public void Calls_gemini_did_not_make_carry_googles_placeholder_and_other_servers_get_nothing_extra()
    {
        var mine = new ToolCall("c1", "task_list", "{}");
        var signed = new ToolCall("c2", "task_list", "{}") { Extra = JsonNode.Parse("""{"google":{"thought_signature":"abc"}}""") };
        JsonNode? Sent(ToolCall call, bool gemini) =>
            OpenAiCompatibleProvider.BuildMessages([ChatMessage.Assistant(null, [call])], gemini)[0]!["tool_calls"]![0]!["extra_content"];

        Assert.Equal("skip_thought_signature_validator", Sent(mine, true)!["google"]!["thought_signature"]!.GetValue<string>());
        Assert.Equal("abc", Sent(signed, true)!["google"]!["thought_signature"]!.GetValue<string>());
        Assert.Null(Sent(mine, false)); // OpenAI, LM Studio…: nothing they don't expect
    }

    [Fact]
    public async Task A_saved_gemini_key_is_used_for_conversation()
    {
        var gemini = new FakeGemini();
        using var host = GeminiHost(gemini);

        var r = await host.Say("In one sentence, what makes a good morning routine?");

        Assert.True(r.Success, r.Reply);
        Assert.Equal("Hello from Gemini.", r.Reply);
        Assert.Equal($"gemini/{AiSettings.GeminiDefaultModel}", r.Model);
        var chat = gemini.Calls.Last(c => c.Request.RequestUri!.AbsolutePath.EndsWith("/chat/completions"));
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", chat.Request.RequestUri!.ToString());
        Assert.Equal(AiSettings.GeminiDefaultModel, chat.Body!["model"]!.GetValue<string>());
        Assert.True(chat.Body["tools"]!.AsArray().Count > 0); // JARVIS's tools are offered, including ones without parameters
    }

    [Fact]
    public async Task Lists_gemini_models_by_the_names_requests_use_and_reports_a_bad_key()
    {
        var gemini = new FakeGemini();
        var cfg = AiSettings.BuiltInProviders().Single(p => p.Id == AiSettings.GeminiId);
        var good = await new OpenAiCompatibleProvider(cfg, () => "AIza-test-key", new HttpClient(gemini)).CheckAsync(default);
        Assert.True(good.Available, good.Message);
        Assert.Contains("gemini-flash-latest", good.Models); // not "models/gemini-flash-latest"

        var bad = await new OpenAiCompatibleProvider(cfg, () => "wrong", new HttpClient(gemini)).CheckAsync(default);
        Assert.False(bad.Available);
        Assert.Contains("rejected the API key", bad.Message);
    }

    [Fact]
    public void Settings_from_an_older_version_gain_gemini_once_and_keep_the_users_choices()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jarvis-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            // What v0.3.3 saved: no Gemini, and a role list the user had changed.
            var old = new JarvisSettings();
            old.Ai.Providers.RemoveAll(p => p.Id == AiSettings.GeminiId);
            old.Ai.Roles[ModelRoles.General] = [new RoleBinding { Provider = "ollama", Model = "qwen2.5:3b" }];
            old.Ai.Providers.Single(p => p.Id == "ollama").BaseUrl = "http://192.168.1.5:11434";
            File.WriteAllText(Path.Combine(dir, "settings.json"), JsonSerializer.Serialize(old, SettingsStore.JsonOptions));

            SettingsStore Load() => new(new JarvisPaths(dir), Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsStore>.Instance);
            var store = Load();
            var gemini = Assert.Single(store.Current.Ai.Providers, p => p.Id == AiSettings.GeminiId);
            Assert.False(gemini.Enabled); // off until the user turns it on
            Assert.False(gemini.IsLocal);
            Assert.Equal("gemini_api_key", gemini.ApiKeySecret);
            Assert.Equal("http://192.168.1.5:11434", store.Current.Ai.Providers.Single(p => p.Id == "ollama").BaseUrl);
            var general = store.Current.Ai.Roles[ModelRoles.General];
            Assert.Equal("qwen2.5:3b", general[0].Model); // the user's first choice stays first
            Assert.Equal(AiSettings.GeminiId, general[^1].Provider);

            // Saved and loaded again: still exactly one. And if the user takes Gemini out of the list, it stays out.
            store.Update(s => s.Ai.Roles[ModelRoles.General].RemoveAll(b => b.Provider == AiSettings.GeminiId));
            var again = Load();
            Assert.Single(again.Current.Ai.Providers, p => p.Id == AiSettings.GeminiId);
            Assert.DoesNotContain(again.Current.Ai.Roles[ModelRoles.General], b => b.Provider == AiSettings.GeminiId);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
