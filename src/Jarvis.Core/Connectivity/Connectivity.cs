using System.Net.NetworkInformation;
using Jarvis.Core.Events;
using Jarvis.Core.Persistence;
using Jarvis.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Connectivity;

public interface IConnectivity
{
    bool IsOnline { get; }
    DateTimeOffset? LastChecked { get; }
    event Action<bool>? Changed;
}

/// <summary>
/// Knows whether the internet is reachable. Uses the OS network state plus a lightweight
/// HTTP probe (the same URL Windows itself uses), so "connected to Wi-Fi without internet"
/// counts as offline.
/// </summary>
public sealed class ConnectivityMonitor(ISettingsStore settings, IEventBus events, ILogger<ConnectivityMonitor> logger, HttpClient? http = null) : IConnectivity
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
    private volatile bool _online = true;

    public bool IsOnline => _online;
    public DateTimeOffset? LastChecked { get; private set; }
    public event Action<bool>? Changed;

    public async Task<bool> CheckAsync(CancellationToken ct)
    {
        var online = NetworkInterface.GetIsNetworkAvailable() && await ProbeAsync(ct).ConfigureAwait(false);
        LastChecked = DateTimeOffset.Now;
        Set(online);
        return online;
    }

    /// <summary>Force a state (used by tests and by the "simulate offline" developer switch).</summary>
    public void Set(bool online)
    {
        if (_online == online) return;
        _online = online;
        logger.LogInformation("Connectivity changed: {State}", online ? "online" : "offline");
        events.Publish(EventTypes.ConnectivityChanged, new { online });
        Changed?.Invoke(online);
    }

    /// <summary>Online if any of several independent endpoints answers (one blocked host must not mean "offline").</summary>
    private async Task<bool> ProbeAsync(CancellationToken ct)
    {
        string[] urls =
        [
            settings.Current.Runtime.ConnectivityProbeUrl,
            "https://www.gstatic.com/generate_204",
            "https://cloudflare.com/cdn-cgi/trace",
        ];
        foreach (var url in urls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct())
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if ((int)resp.StatusCode < 500) return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogDebug("Connectivity probe {Url} failed: {Message}", url, ex.Message);
            }
        }
        return false;
    }
}

public sealed record QueuedAction(string Id, DateTimeOffset CreatedAt, string Tool, string Args, string Summary, string? ConversationId, string? Lang, string Status);

/// <summary>Actions that need the internet, captured while offline so they can run later with the user's OK.</summary>
public sealed class OfflineQueue(JarvisDatabase db, IEventBus events)
{
    public QueuedAction Enqueue(string tool, string args, string summary, string? conversationId, string? lang)
    {
        var id = Guid.NewGuid().ToString("n");
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO offline_queue (id, created_at, tool, args, summary, conversation_id, lang, status)
                VALUES ($id, $now, $tool, $args, $summary, $conv, $lang, 'queued');
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$now", JarvisDatabase.Now());
            cmd.Parameters.AddWithValue("$tool", tool);
            cmd.Parameters.AddWithValue("$args", args);
            cmd.Parameters.AddWithValue("$summary", summary);
            cmd.Parameters.AddWithValue("$conv", (object?)conversationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lang", (object?)lang ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        events.Publish(EventTypes.QueueChanged, new { action = "queued", id });
        return Get(id)!;
    }

    public QueuedAction? Get(string id) => List(includeDone: true).FirstOrDefault(q => q.Id == id);

    public IReadOnlyList<QueuedAction> List(bool includeDone = false)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, created_at, tool, args, summary, conversation_id, lang, status FROM offline_queue
            WHERE ($all = 1 OR status = 'queued') ORDER BY created_at;
            """;
        cmd.Parameters.AddWithValue("$all", includeDone ? 1 : 0);
        using var r = cmd.ExecuteReader();
        var list = new List<QueuedAction>();
        while (r.Read())
        {
            list.Add(new QueuedAction(r.GetString(0), JarvisDatabase.Parse(r.GetString(1)), r.GetString(2), r.GetString(3),
                r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7)));
        }
        return list;
    }

    public void SetStatus(string id, string status)
    {
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE offline_queue SET status = $s WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$s", status);
            cmd.ExecuteNonQuery();
        }
        events.Publish(EventTypes.QueueChanged, new { action = status, id });
    }
}
