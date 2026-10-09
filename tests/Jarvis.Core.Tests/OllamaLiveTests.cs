using Jarvis.Core.AI;
using Jarvis.Core.Settings;
using Jarvis.Core.Tools;

namespace Jarvis.Core.Tests;

/// <summary>Runs only when JARVIS_OLLAMA_URL points at a real Ollama server (CI installs one).</summary>
public sealed class OllamaFactAttribute : FactAttribute
{
    public OllamaFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JARVIS_OLLAMA_URL")))
            Skip = "Set JARVIS_OLLAMA_URL (and JARVIS_OLLAMA_MODEL) to run against a real Ollama server.";
    }
}

/// <summary>
/// End-to-end checks against a real Ollama install with a small real model — no stubs. These prove the
/// native API integration (capabilities, streaming, tool calling, embeddings, the full agent loop).
/// </summary>
public class OllamaLiveTests
{
    private static string Url => Environment.GetEnvironmentVariable("JARVIS_OLLAMA_URL") ?? "http://127.0.0.1:11434";
    private static string Model => Environment.GetEnvironmentVariable("JARVIS_OLLAMA_MODEL") ?? "qwen2.5:1.5b";
    private static string EmbedModel => Environment.GetEnvironmentVariable("JARVIS_OLLAMA_EMBED_MODEL") ?? "all-minilm";

    private static OllamaProvider Provider() => new(
        new ProviderConfig { Id = "ollama", Name = "Ollama", Kind = ProviderKinds.Ollama, BaseUrl = Url, IsLocal = true, Enabled = true },
        new HttpClient { Timeout = TimeSpan.FromMinutes(5) });

    [OllamaFact]
    public async Task Reports_real_models_and_capabilities()
    {
        var p = Provider();
        var status = await p.CheckAsync(default);
        Assert.True(status.Available, status.Message);
        var models = await p.ListModelsAsync(default);
        var chat = models.Single(m => m.Name == Model);
        Assert.True(chat.Has(ModelCapabilities.Tools), string.Join(",", chat.Capabilities));
        Assert.True(chat.ContextLength > 0);
        Assert.Contains(models, m => m.Name.StartsWith(EmbedModel) && !m.IsChatModel);
    }

    [OllamaFact]
    public async Task Streams_a_real_reply()
    {
        var deltas = new List<string>();
        var resp = await Provider().CompleteAsync(new ChatRequest
        {
            Model = Model,
            Messages = [ChatMessage.System("Reply with one short sentence."), ChatMessage.User("Say hello to Sir.")],
            MaxTokens = 40,
            Temperature = 0,
            OnTextDelta = deltas.Add,
        }, default);
        Assert.False(string.IsNullOrWhiteSpace(resp.Content));
        Assert.True(deltas.Count > 1, "expected several streamed chunks");
        Assert.Equal(resp.Content, string.Concat(deltas));
        Assert.True(resp.Usage!.OutputTokens > 0);
    }

    [OllamaFact]
    public async Task Real_model_calls_a_tool()
    {
        var tool = new ToolDefinition
        {
            Name = "reminder_create", Description = "Create a reminder for the user.", Category = "tasks",
            Parameters = [new("text", "string", "What to remind about", true), new("minutes", "integer", "Minutes from now", true)],
        };
        ChatResponse? resp = null;
        // Small models are not perfectly deterministic about tool use; give it a few tries.
        for (var attempt = 0; attempt < 3 && resp?.ToolCalls.Count is null or 0; attempt++)
        {
            resp = await Provider().CompleteAsync(new ChatRequest
            {
                Model = Model,
                Messages = [ChatMessage.System("You are an assistant. Use tools to act."), ChatMessage.User("Remind me in 10 minutes to call Ahmed.")],
                Tools = [tool],
                Temperature = 0,
                MaxTokens = 200,
            }, default);
        }
        var call = Assert.Single(resp!.ToolCalls);
        Assert.Equal("reminder_create", call.Name);
        var args = ToolArgs.Parse(call.ArgumentsJson);
        Assert.Contains("ahmed", args.GetString("text")?.ToLowerInvariant() ?? "");
    }

    [OllamaFact]
    public async Task Embeds_text()
    {
        var vectors = await Provider().EmbedAsync(EmbedModel, ["meeting with the CityCrep team", "اجتماع مع فريق سيتي كريب"], default);
        Assert.Equal(2, vectors.Length);
        Assert.True(vectors[0].Length > 100);
    }

    [OllamaFact]
    public async Task Semantic_memory_recall_with_a_real_embedding_model()
    {
        using var host = new TestHost(s =>
        {
            s.Ai.Providers = [new ProviderConfig { Id = "ollama", Name = "Ollama", Kind = ProviderKinds.Ollama, BaseUrl = Url, IsLocal = true, Enabled = true }];
            s.Ai.EmbeddingModel = EmbedModel;
        });
        host.Get<ProviderRegistry>().Factory = null;
        host.Settings.Update(_ => { });
        var knowledge = host.Get<Jarvis.Core.Memory.KnowledgeService>();
        knowledge.Remember(new Jarvis.Core.Memory.NewMemory("I drive a red Toyota to the office every day"));
        knowledge.Remember(new Jarvis.Core.Memory.NewMemory("My sister's birthday is on the 3rd of May"));
        knowledge.Remember(new Jarvis.Core.Memory.NewMemory("The quarterly board meeting is held in the Zamalek office"));
        await knowledge.BackfillAsync(default);

        var hits = await knowledge.RecallAsync("which vehicle do I own?", 3, default);
        Assert.NotEmpty(hits);
        Assert.Contains("Toyota", hits[0].Memory.Content);
        Assert.True(hits[0].Semantic);
    }

    [OllamaFact]
    public async Task Full_agent_turn_through_real_ollama()
    {
        using var host = new TestHost(s =>
        {
            s.Ai.Providers = [new ProviderConfig { Id = "ollama", Name = "Ollama", Kind = ProviderKinds.Ollama, BaseUrl = Url, IsLocal = true, Enabled = true }];
            s.Ai.Roles = new() { [ModelRoles.General] = [new RoleBinding { Provider = "ollama" }] };
            s.Ai.RequestTimeoutSeconds = 300;
        });
        host.Get<ProviderRegistry>().Factory = null;
        host.Settings.Update(_ => { });

        var r = await host.Say("In one sentence, what is a good habit for staying focused?");

        Assert.True(r.Success, r.Reply);
        Assert.Equal("ai", r.Route);
        Assert.StartsWith("ollama/", r.Model);
        Assert.False(string.IsNullOrWhiteSpace(r.Reply));
    }

    private TestHost RealHost(Action<JarvisSettings>? more = null)
    {
        var host = new TestHost(s =>
        {
            s.Ai.Providers = [new ProviderConfig { Id = "ollama", Name = "Ollama", Kind = ProviderKinds.Ollama, BaseUrl = Url, IsLocal = true, Enabled = true }];
            s.Ai.Roles = new() { [ModelRoles.General] = [new RoleBinding { Provider = "ollama", Model = Model }] };
            s.Ai.RequestTimeoutSeconds = 300;
            more?.Invoke(s);
        });
        host.Get<ProviderRegistry>().Factory = null;
        host.Settings.Update(_ => { });
        return host;
    }

    [OllamaFact]
    public async Task Real_model_tool_call_is_executed_through_the_agent()
    {
        // Phrased so no deterministic command matches: the model has to choose the tool.
        const string ask = "I keep forgetting to send the CityCrep invoice. Please put that on my task list.";
        Assert.Null(Jarvis.Core.Agent.IntentEngine.Match(ask, DateTimeOffset.Now));
        string? last = null;
        for (var attempt = 0; attempt < 3; attempt++) // a 1.5B model doesn't always pick the tool on the first try
        {
            using var host = RealHost();
            var r = await host.Say(ask);
            last = $"{r.Reply} | steps: {string.Join(", ", r.Steps.Select(st => $"{st.Tool}:{st.Status}"))}";
            var tasks = host.Get<Jarvis.Core.Tasks.TaskStore>().List();
            if (r.Steps.Any(st => st.Tool == "task_create" && st.Status == ToolStatus.Ok) && tasks.Any(t => t.Title.Contains("invoice", StringComparison.OrdinalIgnoreCase)))
                return; // the real model called the tool, JARVIS executed it, the task exists
        }
        Assert.Fail("The real model never created the task: " + last);
    }

    [OllamaFact]
    public async Task Real_model_answers_in_arabic()
    {
        using var host = RealHost();
        var r = await host.Say("مساء الخير يا جارفيس، قولي في جملة واحدة إزاي أركز في الشغل");
        Assert.True(r.Success, r.Reply);
        Assert.Equal("ai", r.Route);
        Assert.Equal("ar", r.Lang);
        var letters = r.Reply.Where(char.IsLetter).ToList();
        var arabic = letters.Count(c => c is >= '\u0600' and <= '\u06FF');
        Assert.True(letters.Count > 5 && arabic * 2 > letters.Count, $"Expected an Arabic reply, got: {r.Reply}");
    }

    private static string? VisionModel => Environment.GetEnvironmentVariable("JARVIS_OLLAMA_VISION_MODEL");

    /// <summary>A screen capture that returns a fixed picture: white, with a large red square in the middle.</summary>
    private sealed class PictureScreen : Jarvis.Core.Vision.IScreenCapture
    {
        public bool IsAvailable => true;
        public byte[] CapturePng(int maxSide = 1600) => RedSquarePng(256);
    }

    [OllamaFact]
    public async Task Real_vision_model_describes_the_screen()
    {
        if (string.IsNullOrEmpty(VisionModel)) return; // CI sets JARVIS_OLLAMA_VISION_MODEL; nothing to check without one
        using var host = RealHost(s => s.Ai.Roles[ModelRoles.Vision] = [new RoleBinding { Provider = "ollama", Model = VisionModel }]);
        var tool = new Jarvis.Core.Vision.ScreenDescribeTool(new PictureScreen(), host.Get<ModelRouter>(), new Jarvis.Core.Files.NullOcrEngine(), host.Get<JarvisPaths>());
        string? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var r = await tool.ExecuteAsync(ToolArgs.From(new { question = "What color is the big square in the middle? Answer with one word." }), host.Ctx());
            Assert.True(r.Success, r.Message);
            last = r.Message;
            if (r.Message.Contains("red", StringComparison.OrdinalIgnoreCase)) return; // the model really looked at the picture
        }
        Assert.Fail("The vision model didn't see the red square: " + last);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>A minimal PNG (RGB, zlib) so the test needs no imaging library.</summary>
    internal static byte[] RedSquarePng(int size)
    {
        var raw = new byte[size * (1 + size * 3)];
        for (var y = 0; y < size; y++)
        {
            var row = y * (1 + size * 3);
            raw[row] = 0; // no filter
            for (var x = 0; x < size; x++)
            {
                var inside = x >= size / 5 && x < size * 4 / 5 && y >= size / 5 && y < size * 4 / 5;
                raw[row + 1 + x * 3] = 255;
                raw[row + 2 + x * 3] = (byte)(inside ? 0 : 255);
                raw[row + 3 + x * 3] = (byte)(inside ? 0 : 255);
            }
        }
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length); if (BitConverter.IsLittleEndian) Array.Reverse(len);
            png.Write(len);
            var td = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            png.Write(td);
            var crc = BitConverter.GetBytes(Crc32(td)); if (BitConverter.IsLittleEndian) Array.Reverse(crc);
            png.Write(crc);
        }
        var ihdr = new byte[13];
        var w = BitConverter.GetBytes(size); if (BitConverter.IsLittleEndian) Array.Reverse(w);
        w.CopyTo(ihdr, 0); w.CopyTo(ihdr, 4);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
        Chunk("IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);
            Chunk("IDAT", z.ToArray());
        }
        Chunk("IEND", []);
        return png.ToArray();
    }
}
