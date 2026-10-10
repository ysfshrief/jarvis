using Jarvis.Core.AI;
using Jarvis.Core.Settings;

namespace Jarvis.Core.Tests;

/// <summary>How JARVIS waits for a local model on a slow PC: loading is its own step, and only silence times out.</summary>
public sealed class ModelTimeoutTests
{
    /// <summary>A local server with two models that can be made slow, silent, or not yet loaded.</summary>
    private sealed class SlowLocalModel : IChatProvider, IModelLoader
    {
        public Func<ChatRequest, CancellationToken, Task<ChatResponse>> Answer { get; set; } = (_, _) => Task.FromResult(new ChatResponse { Content = "ok" });
        public bool Loaded { get; set; } = true;
        public List<string> Calls { get; } = [];
        public string Id => "local";
        public string Name => "Local";
        public bool IsLocal => true;

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct)
        {
            lock (Calls) Calls.Add($"chat {request.Model}");
            return Answer(request, ct);
        }

        public Task<ProviderStatus> CheckAsync(CancellationToken ct) =>
            Task.FromResult(new ProviderStatus(Id, true, "ok", ["big:7b", "small:3b"], DateTimeOffset.Now));

        public Task<bool> IsLoadedAsync(string model, CancellationToken ct) => Task.FromResult(Loaded);

        public async Task LoadAsync(string model, int contextTokens, CancellationToken ct)
        {
            lock (Calls) Calls.Add($"load {model} {contextTokens}");
            await Task.Delay(300, ct);
            Loaded = true;
        }
    }

    private static (TestHost Host, SlowLocalModel Model) Host()
    {
        var model = new SlowLocalModel();
        var host = new TestHost(s =>
        {
            s.Ai.Providers = [new ProviderConfig { Id = "local", Name = "Local", Kind = "scripted", IsLocal = true, Enabled = true }];
            s.Ai.Roles = new() { [ModelRoles.General] = [new RoleBinding { Provider = "local", Model = "big:7b" }, new RoleBinding { Provider = "local", Model = "small:3b" }] };
            s.Ai.RequestTimeoutSeconds = 10;
        });
        host.Get<ProviderRegistry>().Factory = _ => model;
        host.Settings.Update(_ => { });
        host.Agent.TimeoutUnit = TimeSpan.FromMilliseconds(20); // the 10-second limit becomes 200 ms
        return (host, model);
    }

    [Fact]
    public async Task A_slow_model_that_keeps_writing_is_not_cut_off()
    {
        var (host, model) = Host();
        using var _ = host;
        model.Answer = async (req, ct) =>
        {
            var text = "";
            for (var i = 0; i < 8; i++) // 800 ms in total, four times the limit, but never 200 ms of silence
            {
                await Task.Delay(100, ct);
                req.OnProgress?.Invoke();
                text += $"{i}";
            }
            return new ChatResponse { Content = text };
        };

        var r = await host.Say("tell me something long");

        Assert.True(r.Success, r.Reply);
        Assert.Equal("01234567", r.Reply);
    }

    [Fact]
    public async Task A_silent_local_model_times_out_with_advice_and_no_second_local_model_is_loaded()
    {
        var (host, model) = Host();
        using var _ = host;
        model.Answer = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new ChatResponse(); };

        var r = await host.Say("tell me something long");

        Assert.False(r.Success);
        Assert.Contains("big:7b went 10 seconds without answering", r.Reply);
        Assert.Contains("qwen2.5:3b", r.Reply); // a 7B model on a slow PC: suggest the smaller one
        Assert.Equal(["chat big:7b"], model.Calls); // small:3b was not loaded on top of it
    }

    [Fact]
    public async Task A_model_that_is_not_in_memory_is_loaded_first_with_the_chat_context_size()
    {
        var (host, model) = Host();
        using var _ = host;
        model.Loaded = false;
        // Loading takes 300 ms (longer than the 200 ms answer limit) and is not counted against it.
        var r = await host.Say("hello there, how are things");

        Assert.True(r.Success, r.Reply);
        Assert.Equal([$"load big:7b {host.Settings.Current.Ai.LocalContextTokens}", "chat big:7b"], model.Calls);
    }
}
