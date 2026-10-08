using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Jarvis.Core.Events;
using Jarvis.Core.Tools;

namespace Jarvis.Core.Permissions;

public sealed record ApprovalRequest(
    string Id,
    string ConversationId,
    string Tool,
    RiskLevel Risk,
    string Summary,
    string Reason,
    string Arguments,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

[JsonConverter(typeof(JsonStringEnumConverter<ApprovalOutcome>))]
public enum ApprovalOutcome { Approved, Denied, TimedOut, Cancelled }

/// <summary>
/// Holds actions waiting for the user's yes/no. The agent awaits <see cref="RequestAsync"/>;
/// any surface (dashboard, quick bar, voice, future phone app) resolves it by id.
/// </summary>
public sealed class ApprovalBroker(IEventBus events)
{
    private readonly ConcurrentDictionary<string, (ApprovalRequest Request, TaskCompletionSource<ApprovalOutcome> Tcs)> _pending = new();

    public IReadOnlyList<ApprovalRequest> Pending =>
        _pending.Values.Select(p => p.Request).OrderBy(r => r.CreatedAt).ToList();

    public IReadOnlyList<ApprovalRequest> PendingFor(string conversationId) =>
        Pending.Where(r => r.ConversationId == conversationId).ToList();

    public async Task<ApprovalOutcome> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = (request, tcs);
        events.Publish(EventTypes.ApprovalRequested, request);

        var timeout = request.ExpiresAt - DateTimeOffset.Now;
        if (timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        await using var reg = timeoutCts.Token.Register(() =>
            tcs.TrySetResult(ct.IsCancellationRequested ? ApprovalOutcome.Cancelled : ApprovalOutcome.TimedOut));

        var outcome = await tcs.Task.ConfigureAwait(false);
        _pending.TryRemove(request.Id, out _);
        events.Publish(EventTypes.ApprovalResolved, new { request.Id, request.Tool, Outcome = outcome });
        return outcome;
    }

    /// <summary>Resolve a pending approval. Returns false if it no longer exists (expired/handled).</summary>
    public bool Resolve(string id, bool approved)
    {
        if (!_pending.TryGetValue(id, out var entry)) return false;
        return entry.Tcs.TrySetResult(approved ? ApprovalOutcome.Approved : ApprovalOutcome.Denied);
    }
}
