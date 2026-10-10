using Jarvis.Core.Activity;
using Jarvis.Core.AI;
using Jarvis.Core.Agent;
using Jarvis.Core.Connectivity;
using Jarvis.Core.Memory;
using Jarvis.Core.Settings;
using Jarvis.Core.Tasks;
using Jarvis.Core.Tools;

namespace Jarvis.Core.Tests;

/// <summary>The core loop: input → intent/AI → permission → execution → verification → reply → memory/activity.</summary>
public class AgentLoopTests
{
    [Fact]
    public async Task Deterministic_command_runs_without_any_model()
    {
        using var host = new TestHost();
        var result = await host.Say("add task Send the CityCrep proposal");

        Assert.Equal("deterministic", result.Route);
        Assert.True(result.Success);
        var step = Assert.Single(result.Steps);
        Assert.Equal("task_create", step.Tool);
        Assert.Equal(ToolStatus.Ok, step.Status);
        Assert.Contains(host.Get<TaskStore>().List(), t => t.Title == "Send the CityCrep proposal");
        Assert.Contains(host.Get<ActivityLog>().Recent(), a => a.Tool == "task_create" && a.Status == "ok");
    }

    [Fact]
    public async Task Arabic_input_gets_an_arabic_reply()
    {
        using var host = new TestHost();
        var result = await host.Say("ضيف مهمة أراجع العقد");
        Assert.Equal("ar", result.Lang);
        Assert.Contains("ضفتها", result.Reply);
    }

    [Fact]
    public async Task Without_a_model_open_questions_fail_honestly()
    {
        using var host = new TestHost();
        var result = await host.Say("summarize my week for me");
        Assert.Equal("none", result.Route);
        Assert.False(result.Success);
        Assert.Contains("no language model", result.Reply);
        Assert.Contains("Ollama", result.Reply);
    }

    [Fact]
    public async Task Ai_plans_calls_a_tool_and_answers_from_the_result()
    {
        using var host = new TestHost(withModel: true);
        host.Model
            .CallTool("task_create", new { title = "Prepare CityCrep briefing", priority = "high" })
            .Then(req =>
            {
                // The model must see the real tool result before answering.
                var toolMsg = req.Messages.Last(m => m.Role == ChatRole.Tool);
                Assert.Contains("\"success\":true", toolMsg.Content);
                return new ChatResponse { Content = "I've added the briefing task, Sir." };
            });

        var result = await host.Say("I need to get ready for the CityCrep meeting, put it on my list as high priority");

        Assert.Equal("ai", result.Route);
        Assert.Equal("scripted/test-model", result.Model);
        Assert.Equal("I've added the briefing task, Sir.", result.Reply);
        Assert.Contains(host.Get<TaskStore>().List(), t => t.Title == "Prepare CityCrep briefing" && t.Priority == "high");

        // The model was given the tools, the persona and the context.
        var first = host.Model.Requests[0];
        Assert.Contains(first.Tools, t => t.Name == "task_create");
        Assert.Contains("You are JARVIS", first.Messages[0].Content);
    }

    [Fact]
    public async Task Ai_tool_calls_still_require_approval_and_denial_is_reported_to_the_model()
    {
        using var host = new TestHost(withModel: true);
        var target = Path.Combine(host.DataDir, "..", $"note-{Guid.NewGuid():n}.txt");
        host.Settings.Update(s => s.Files.AllowedRoots = [Path.GetFullPath(Path.Combine(host.DataDir, ".."))]);
        host.Model
            .CallTool("file_write", new { path = target, content = "hello" })
            .Then(req =>
            {
                var toolMsg = req.Messages.Last(m => m.Role == ChatRole.Tool);
                Assert.True(toolMsg.IsError);
                Assert.Contains("Denied", toolMsg.Content);
                return new ChatResponse { Content = "Understood, I won't write the file." };
            });

        var turn = host.Say("write hello into a note");
        var approval = await host.AnswerNextApproval(approve: false);
        var result = await turn;

        Assert.Equal("file_write", approval.Tool);
        Assert.False(File.Exists(target));
        Assert.Equal(ToolStatus.Denied, Assert.Single(result.Steps).Status);
        Assert.Equal("Understood, I won't write the file.", result.Reply);
    }

    [Fact]
    public async Task Approved_sensitive_action_executes()
    {
        using var host = new TestHost(withModel: true);
        var dir = Path.GetFullPath(Path.Combine(host.DataDir, ".."));
        var target = Path.Combine(dir, $"note-{Guid.NewGuid():n}.txt");
        host.Settings.Update(s => s.Files.AllowedRoots = [dir]);
        host.Model.CallTool("file_write", new { path = target, content = "hello" }).Reply("Saved.");

        var turn = host.Say("save hello to a note");
        await host.AnswerNextApproval(approve: true);
        var result = await turn;

        Assert.Equal("hello", File.ReadAllText(target));
        Assert.Equal(ToolStatus.Ok, Assert.Single(result.Steps).Status);
        File.Delete(target);
    }

    [Fact]
    public async Task Saying_yes_in_chat_approves_the_pending_action()
    {
        using var host = new TestHost(withModel: true);
        var dir = Path.GetFullPath(Path.Combine(host.DataDir, ".."));
        var target = Path.Combine(dir, $"note-{Guid.NewGuid():n}.txt");
        host.Settings.Update(s => s.Files.AllowedRoots = [dir]);
        host.Model.CallTool("file_write", new { path = target, content = "via voice" }).Reply("Done.");

        var turn = host.Say("write a note");
        while (host.Approvals.Pending.Count == 0) await Task.Delay(20);
        var yes = await host.Agent.HandleAsync(new UserInput("أيوه", "another-conversation", InputSource.Voice));
        var result = await turn;

        Assert.Equal("ar", yes.Lang);
        Assert.Equal("via voice", File.ReadAllText(target));
        Assert.True(result.Success);
        File.Delete(target);
    }

    [Fact]
    public async Task Critical_actions_ask_even_when_sensitive_actions_are_auto_approved()
    {
        using var host = new TestHost(s => s.Permissions.AutoApproveSensitive = true, withModel: true);
        host.Model.CallTool("run_command", new { command = "Remove-Item -Recurse C:\\Projects" }).Reply("ok");

        var turn = host.Say("clean up my projects folder");
        var approval = await host.AnswerNextApproval(approve: false);
        await turn;
        Assert.Equal(RiskLevel.Critical, approval.Risk);
    }

    [Fact]
    public async Task Blocked_tools_are_hidden_from_the_model_and_refused()
    {
        using var host = new TestHost(s => s.Permissions.ToolOverrides["run_command"] = ToolPolicy.Block, withModel: true);
        host.Model.CallTool("run_command", new { command = "git status" }).Reply("I couldn't run it.");

        var result = await host.Say("check git status in my repo");

        Assert.DoesNotContain(host.Model.Requests[0].Tools, t => t.Name == "run_command");
        Assert.Equal(ToolStatus.Denied, Assert.Single(result.Steps).Status);
    }

    [Fact]
    public async Task Model_failure_produces_a_useful_message()
    {
        using var host = new TestHost(withModel: true);
        host.Model.FailWith = new AiProviderException("Ollama is not reachable.", retryable: true);

        var result = await host.Say("summarize my week");

        Assert.False(result.Success);
        Assert.Contains("Ollama is not reachable", result.Reply);
        Assert.Contains(host.Get<ActivityLog>().Recent(), a => a.Kind == ActivityKinds.Ai && a.Status == "failed");
    }

    [Fact]
    public async Task Runaway_tool_loops_stop_at_the_step_limit()
    {
        using var host = new TestHost(s => s.Ai.MaxAgentSteps = 3, withModel: true);
        for (var i = 0; i < 5; i++) host.Model.CallTool("task_list", new { });

        var result = await host.Say("keep checking my tasks");

        Assert.Equal(3, result.Steps.Count);
        Assert.Contains("3 steps", result.Reply);
    }

    [Fact]
    public async Task Unknown_tools_and_bad_arguments_are_reported_back_not_thrown()
    {
        using var host = new TestHost(withModel: true);
        host.Model.CallTool("teleport", new { where = "Mars" })
            .Then(_ => new ChatResponse { ToolCalls = [new ToolCall("c2", "task_create", "{not json")] })
            .Reply("Sorry, I can't do that.");

        var result = await host.Say("teleport me");

        Assert.Equal(2, result.Steps.Count);
        Assert.All(result.Steps, s => Assert.NotEqual(ToolStatus.Ok, s.Status));
        Assert.Equal("Sorry, I can't do that.", result.Reply);
    }

    [Fact]
    public async Task Conversation_context_carries_over_between_turns()
    {
        using var host = new TestHost(withModel: true);
        host.Model.Reply("Noted.").Reply("You mentioned Ahmed.");

        await host.Say("my colleague is called Ahmed");
        await host.Say("who did I mention?");

        var second = host.Model.Requests[1];
        Assert.Contains(second.Messages, m => m.Role == ChatRole.User && m.Content == "my colleague is called Ahmed");
        Assert.Contains(second.Messages, m => m.Role == ChatRole.Assistant && m.Content == "Noted.");
    }

    [Fact]
    public async Task Relevant_memories_are_given_to_the_model()
    {
        using var host = new TestHost(withModel: true);
        host.Get<MemoryStore>().Add(new NewMemory("The CityCrep meeting is every Sunday at 11", MemoryKinds.Fact, "CityCrep"));
        host.Model.Reply("Sunday at 11.");

        await host.Say("when is the CityCrep meeting?");

        Assert.Contains("CityCrep meeting is every Sunday", host.Model.Requests[0].Messages[^1].Content);
    }

    [Fact]
    public async Task System_prompt_and_tools_stay_identical_between_turns_so_a_local_model_can_reuse_them()
    {
        using var host = new TestHost(withModel: true);
        host.Get<MemoryStore>().Add(new NewMemory("The CityCrep meeting is every Sunday at 11", MemoryKinds.Fact, "CityCrep"));
        host.Model.Reply("Sunday at 11.").Reply("تمام.").Reply("Done.");

        await host.Say("when is the CityCrep meeting?");
        await host.Say("فكرني بالاجتماع بتاع سيتي كريب");
        host.Get<TaskStore>().Create(new NewTask("Prepare the CityCrep briefing"));
        await host.Say("anything else about CityCrep?", InputSource.Voice);

        var requests = host.Model.Requests;
        Assert.Equal(3, requests.Count);
        // Time, memories, tasks, language and voice changed between these turns; the prefix a model caches did not.
        Assert.All(requests, r => Assert.Equal(requests[0].Messages[0].Content, r.Messages[0].Content));
        // The tools a message's topic adds come after the core tools, which stay the same and in the same order.
        var core = requests[0].Tools.Select(t => t.Name).TakeWhile(ToolSelector.Core.Contains).ToList();
        Assert.True(core.Count >= 8, string.Join(",", core));
        Assert.All(requests, r => Assert.Equal(core, r.Tools.Select(t => t.Name).Take(core.Count)));
        Assert.DoesNotContain("CityCrep", requests[0].Messages[0].Content);
        // The changing part travels with each message instead.
        Assert.Contains("Reply in: Egyptian Arabic", requests[1].Messages[^1].Content);
        Assert.Contains("Prepare the CityCrep briefing", requests[2].Messages[^1].Content);
        Assert.Contains("spoken aloud", requests[2].Messages[^1].Content);
        // Earlier messages go back exactly as they were said, so the cached prefix also covers the conversation so far.
        Assert.Contains(requests[2].Messages, m => m.Role == ChatRole.User && m.Content == "when is the CityCrep meeting?");
    }

    [Fact]
    public async Task Failed_deterministic_command_falls_back_to_ai()
    {
        using var host = new TestHost(withModel: true);
        // There is no app_open tool in the core test host, so the deterministic path fails and the AI takes over.
        host.Model.Reply("Let me handle that differently.");

        var result = await host.Say("open calculator");

        Assert.Equal("ai", result.Route);
        Assert.Single(host.Model.Requests);
    }

    [Fact]
    public async Task Internet_tools_are_queued_while_offline()
    {
        using var host = new TestHost();
        host.Get<ConnectivityMonitor>().Set(false);

        var result = await host.Say("search for best laptops for programming");

        Assert.Equal(ToolStatus.Queued, Assert.Single(result.Steps).Status);
        var queued = Assert.Single(host.Get<OfflineQueue>().List());
        Assert.Equal("web_search", queued.Tool);
        Assert.Contains("offline", result.Reply);
    }

    [Fact]
    public async Task Conversation_history_is_persisted_locally()
    {
        using var host = new TestHost();
        await host.Say("hello");
        var messages = host.Get<ConversationStore>().Messages("test-conv");
        Assert.Equal(["user", "assistant"], messages.Select(m => m.Role));
    }

    [Fact]
    public async Task Language_switch_sticks_for_the_conversation()
    {
        using var host = new TestHost();
        var switched = await host.Say("كلمني انجليزي");
        Assert.Equal("en", switched.Lang);
        var next = await host.Say("الساعة كام");
        Assert.Equal("en", next.Lang);
        Assert.StartsWith("It's", next.Reply);
    }
}
