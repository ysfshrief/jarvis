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
}
