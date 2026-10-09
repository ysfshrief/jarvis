using System.Diagnostics;
using System.Text.Json.Serialization;
using Jarvis.Core.Activity;
using Jarvis.Core.Connectivity;
using Jarvis.Core.Events;
using Jarvis.Core.Permissions;
using Jarvis.Core.Settings;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Agent;

/// <summary>One executed (or refused) action, as shown in the chat and the activity log.</summary>
public sealed record ToolStep(
    string Tool,
    string Summary,
    RiskLevel Risk,
    [property: JsonConverter(typeof(JsonStringEnumConverter<ToolStatus>))] ToolStatus Status,
    string Message,
    long DurationMs,
    object? Data);

/// <summary>
/// The only path from intent to action: look up the tool, assess risk, check permission,
/// ask for approval when needed, queue if offline, execute with a timeout, verify, and audit.
/// </summary>
public sealed class ToolExecutor(
    IToolRegistry registry,
    PermissionService permissions,
    ApprovalBroker approvals,
    ActivityLog activity,
    IEventBus events,
    ISettingsStore settings,
    IConnectivity connectivity,
    OfflineQueue offlineQueue,
    ILogger<ToolExecutor> logger)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public async Task<(ToolResult Result, ToolStep Step)> ExecuteAsync(string name, ToolArgs args, ToolContext ctx, bool allowQueue = true)
    {
        var tool = registry.Find(name);
        if (tool is null)
        {
            var missing = ToolResult.Fail(ctx.T($"I don't have a tool called '{name}'.", $"معنديش أداة اسمها '{name}'."), status: ToolStatus.NotFound);
            return (missing, Step(name, name, RiskLevel.Safe, missing, 0));
        }

        RiskAssessment assessment;
        try
        {
            assessment = tool.Assess(args, ctx);
        }
        catch (ToolArgumentException ex)
        {
            var bad = ToolResult.Fail(ctx.T($"I couldn't run {name}: {ex.Message}", $"مقدرتش أشغل {name}: {ex.Message}"));
            return (bad, Step(name, name, tool.Definition.Risk, bad, 0));
        }

        var s = settings.Current;
        var decision = permissions.Evaluate(tool.Definition, assessment, s);
        var summary = assessment.Summary;

        // Content from the web or someone's email may contain instructions aimed at JARVIS. Whatever is
        // proposed after reading it must be confirmed by the user, even if they normally allow it.
        if (decision.Outcome == PermissionOutcome.Allow && decision.Risk >= RiskLevel.Sensitive && ctx.Turn.UntrustedSeen)
            decision = decision with
            {
                Outcome = PermissionOutcome.RequireApproval,
                Reason = $"Proposed after reading untrusted content ({ctx.Turn.UntrustedSource}); confirming it's what you want.",
            };
        // A paired phone can be lost or borrowed: what it asks for that changes anything is always confirmed.
        else if (decision.Outcome == PermissionOutcome.Allow && decision.Risk >= RiskLevel.Sensitive && ctx.Via == "remote")
            decision = decision with { Outcome = PermissionOutcome.RequireApproval, Reason = "Requested from a paired phone; confirming it's you." };

        if (decision.Outcome == PermissionOutcome.Deny)
        {
            var denied = ToolResult.Fail(ctx.T($"I'm not allowed to do that: {decision.Reason}", $"مش مسموحلي أعمل ده: {decision.Reason}"), status: ToolStatus.Denied);
            activity.Record(ActivityKinds.Tool, summary, name, decision.Risk.ToString(), "blocked", decision.Reason, ctx.ConversationId);
            return (denied, Step(name, summary, decision.Risk, denied, 0));
        }

        if (tool.Definition.RequiresInternet && !connectivity.IsOnline)
        {
            if (!allowQueue)
            {
                var off = ToolResult.Fail(ctx.T("We're offline, so I can't do that right now.", "إحنا أوفلاين دلوقتي، مش هقدر أعمل ده."), status: ToolStatus.Failed);
                return (off, Step(name, summary, decision.Risk, off, 0));
            }
            var q = offlineQueue.Enqueue(name, args.ToString(), summary, ctx.ConversationId, ctx.Lang.ToString().ToLowerInvariant());
            var queued = new ToolResult
            {
                Success = false,
                Status = ToolStatus.Queued,
                Message = ctx.T($"We're currently offline{ctx.CommaSir}. I've queued this and will ask you before running it when we're back online.",
                                $"إحنا أوفلاين دلوقتي{ctx.CommaSir}. حطيتها في الطابور وهسألك قبل ما أنفذها لما النت يرجع."),
                Data = new { queueId = q.Id },
            };
            activity.Record(ActivityKinds.Tool, summary, name, decision.Risk.ToString(), "queued", "Offline", ctx.ConversationId);
            return (queued, Step(name, summary, decision.Risk, queued, 0));
        }

        if (decision.Outcome == PermissionOutcome.RequireApproval)
        {
            var request = new ApprovalRequest(
                Guid.NewGuid().ToString("n"), ctx.ConversationId, name, decision.Risk, summary, decision.Reason,
                args.ToString(), DateTimeOffset.Now, DateTimeOffset.Now.AddSeconds(s.Permissions.ApprovalTimeoutSeconds));
            activity.Record(ActivityKinds.Approval, $"Approval requested: {summary}", name, decision.Risk.ToString(), "pending", decision.Reason, ctx.ConversationId);

            var outcome = await approvals.RequestAsync(request, ctx.CancellationToken).ConfigureAwait(false);
            activity.Record(ActivityKinds.Approval, $"{outcome}: {summary}", name, decision.Risk.ToString(), outcome.ToString().ToLowerInvariant(), null, ctx.ConversationId);
            if (outcome != ApprovalOutcome.Approved)
            {
                var msg = outcome switch
                {
                    ApprovalOutcome.TimedOut => ctx.T($"No answer on the approval, so I didn't {Lower(summary)}.", "محدش وافق في الوقت، فمعملتش الحاجة دي."),
                    _ => ctx.T($"Understood{ctx.CommaSir}. I won't do it.", $"تمام{ctx.CommaSir}، مش هعملها."),
                };
                var refused = ToolResult.Fail(msg, outcome.ToString(), outcome == ApprovalOutcome.TimedOut ? ToolStatus.TimedOut : ToolStatus.Denied);
                return (refused, Step(name, summary, decision.Risk, refused, 0));
            }
        }

        events.Publish(EventTypes.ToolStarted, new { tool = name, summary, risk = decision.Risk, conversationId = ctx.ConversationId, turnId = ctx.TurnId });
        var sw = Stopwatch.StartNew();
        ToolResult result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
            timeout.CancelAfter(DefaultTimeout);
            var runCtx = ctx.WithToken(timeout.Token);
            result = await tool.ExecuteAsync(args, runCtx).ConfigureAwait(false);
        }
        catch (ToolArgumentException ex)
        {
            result = ToolResult.Fail(ctx.T($"I couldn't do that: {ex.Message}", $"مقدرتش أعمل ده: {ex.Message}"));
        }
        catch (OperationCanceledException) when (!ctx.CancellationToken.IsCancellationRequested)
        {
            result = ToolResult.Fail(ctx.T("That took too long, so I stopped it.", "ده خد وقت طويل أوي فوقفته."), "Timed out", ToolStatus.TimedOut);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tool {Tool} crashed", name);
            result = ToolResult.Fail(ctx.T($"Something went wrong while running {name}: {ex.Message}", $"حصلت مشكلة وأنا بشغل {name}: {ex.Message}"), ex.ToString());
        }
        sw.Stop();
        if (tool.Definition.ReadsUntrustedContent && result.Success)
            ctx.Turn.UntrustedSource ??= $"{name}: {summary}";

        activity.Record(ActivityKinds.Tool, summary, name, decision.Risk.ToString(), result.Success ? "ok" : result.Status.ToString().ToLowerInvariant(),
            result.Success ? Truncate(result.Message) : result.Error ?? result.Message, ctx.ConversationId, sw.ElapsedMilliseconds);
        var step = Step(name, summary, decision.Risk, result, sw.ElapsedMilliseconds);
        events.Publish(EventTypes.ToolCompleted, new { step, conversationId = ctx.ConversationId, turnId = ctx.TurnId });
        return (result, step);
    }

    private static ToolStep Step(string name, string summary, RiskLevel risk, ToolResult r, long ms) =>
        new(name, summary, risk, r.Success ? ToolStatus.Ok : r.Status == ToolStatus.Ok ? ToolStatus.Failed : r.Status, r.Message, ms, r.Data);

    private static string Lower(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
    private static string Truncate(string s) => s.Length > 500 ? s[..497] + "..." : s;
}
