using Jarvis.Core;
using Jarvis.Core.Agent;
using Jarvis.Core.AI;
using Jarvis.Core.Connectivity;
using Jarvis.Core.Language;
using Jarvis.Core.Permissions;
using Jarvis.Core.Settings;
using Jarvis.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Tests;

/// <summary>
/// A real JARVIS core (real database, real tools, real permission system) in a temp folder.
/// Only the language model is replaced, by <see cref="ScriptedChatProvider"/>.
/// </summary>
public sealed class TestHost : IDisposable
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "jarvis-tests", Guid.NewGuid().ToString("n"));
    public ServiceProvider Services { get; }
    public ScriptedChatProvider Model { get; } = new();

    public TestHost(Action<JarvisSettings>? configure = null, Action<IServiceCollection>? services = null, bool withModel = false)
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        sc.AddJarvisCore(new JarvisPaths(DataDir));
        services?.Invoke(sc);
        Services = sc.BuildServiceProvider();

        Settings.Update(s =>
        {
            s.Permissions.ApprovalTimeoutSeconds = 15;
            // Tests never talk to real AI servers.
            s.Ai.Providers = withModel
                ? [new ProviderConfig { Id = "scripted", Name = "Scripted test model", Kind = "scripted", IsLocal = true, Enabled = true }]
                : [];
            s.Ai.Roles = new() { [ModelRoles.General] = [new RoleBinding { Provider = "scripted", Model = "test-model" }] };
            configure?.Invoke(s);
        });
        var registry = Get<ProviderRegistry>();
        registry.Factory = cfg => cfg.Kind == "scripted" ? Model : null;
        Settings.Update(_ => { }); // rebuild providers with the factory in place
        Get<ConnectivityMonitor>().Set(true);
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();
    public ISettingsStore Settings => Get<ISettingsStore>();
    public AgentOrchestrator Agent => Get<AgentOrchestrator>();
    public ApprovalBroker Approvals => Get<ApprovalBroker>();

    public ToolContext Ctx(Lang lang = Lang.En) => new() { Lang = lang, Settings = Settings.Current, ConversationId = "test" };

    public Task<AgentTurnResult> Say(string text, InputSource source = InputSource.Text) =>
        Agent.HandleAsync(new UserInput(text, "test-conv", source));

    /// <summary>Waits until an approval is pending, then answers it.</summary>
    public async Task<ApprovalRequest> AnswerNextApproval(bool approve)
    {
        for (var i = 0; i < 200; i++)
        {
            var pending = Approvals.Pending.FirstOrDefault();
            if (pending is not null)
            {
                Approvals.Resolve(pending.Id, approve);
                return pending;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("No approval was requested.");
    }

    public void Dispose()
    {
        Services.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDir, recursive: true); } catch { }
    }
}

/// <summary>Test double for a language model: returns pre-scripted responses and records every request.</summary>
public sealed class ScriptedChatProvider : IChatProvider
{
    private readonly Queue<Func<ChatRequest, ChatResponse>> _script = new();
    public List<ChatRequest> Requests { get; } = [];
    public bool Available { get; set; } = true;
    public Exception? FailWith { get; set; }

    public string Id => "scripted";
    public string Name => "Scripted test model";
    public bool IsLocal => true;

    public ScriptedChatProvider Reply(string text)
    {
        _script.Enqueue(_ => new ChatResponse { Content = text });
        return this;
    }

    public ScriptedChatProvider CallTool(string name, object args, string? text = null)
    {
        _script.Enqueue(_ => new ChatResponse
        {
            Content = text,
            ToolCalls = [new ToolCall($"call_{Guid.NewGuid():n}", name, System.Text.Json.JsonSerializer.Serialize(args))],
        });
        return this;
    }

    public ScriptedChatProvider Then(Func<ChatRequest, ChatResponse> step)
    {
        _script.Enqueue(step);
        return this;
    }

    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        if (FailWith is not null) throw FailWith;
        if (_script.Count == 0) return Task.FromResult(new ChatResponse { Content = "(script exhausted)" });
        return Task.FromResult(_script.Dequeue()(request));
    }

    public Task<ProviderStatus> CheckAsync(CancellationToken ct) =>
        Task.FromResult(new ProviderStatus(Id, Available, Available ? "ok" : "down", Available ? ["test-model"] : [], DateTimeOffset.Now));
}
