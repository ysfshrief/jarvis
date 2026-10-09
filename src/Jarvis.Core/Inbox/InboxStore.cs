using System.Text.Json;
using Jarvis.Core.Events;
using Jarvis.Core.Language;
using Jarvis.Core.Memory;
using Jarvis.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace Jarvis.Core.Inbox;

public static class MailCategories
{
    public const string Urgent = "urgent";
    public const string Important = "important";
    public const string NeedsResponse = "needs_response";
    public const string Fyi = "fyi";
    public const string Noise = "noise";
    public static readonly string[] All = [Urgent, Important, NeedsResponse, Fyi, Noise];
}

public static class DraftStatus
{
    public const string Draft = "draft";
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string Discarded = "discarded";
}

/// <summary>Connection details (never the password or tokens — those live in the secret store).</summary>
public sealed record MailAccountConfig
{
    public string ImapHost { get; init; } = "";
    public int ImapPort { get; init; } = 993;
    /// <summary>"ssl", "starttls" or "none".</summary>
    public string ImapSecurity { get; init; } = "ssl";
    public string SmtpHost { get; init; } = "";
    public int SmtpPort { get; init; } = 465;
    public string SmtpSecurity { get; init; } = "ssl";
    public string Username { get; init; } = "";
    public string Folder { get; init; } = "INBOX";
}

public sealed record MailAccount
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Address { get; init; }
    public string? DisplayName { get; init; }
    public MailAccountConfig Config { get; init; } = new();
    public bool Enabled { get; init; } = true;
    public string Status { get; init; } = "new";
    public string? StatusMessage { get; init; }
    public DateTimeOffset? LastSync { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public string PasswordSecret => $"mail.{Id}.password";
}

/// <summary>A message as fetched from a provider, before JARVIS classifies it.</summary>
public sealed record FetchedMail
{
    public required string ExternalId { get; init; }
    public string? MessageIdHeader { get; init; }
    public string? InReplyTo { get; init; }
    public string? FromName { get; init; }
    public required string FromAddress { get; init; }
    public IReadOnlyList<string> To { get; init; } = [];
    public IReadOnlyList<string> Cc { get; init; } = [];
    public string Subject { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTimeOffset ReceivedAt { get; init; }
    public bool IsRead { get; init; }
    /// <summary>Mailing-list / bulk markers (List-Unsubscribe, Precedence: bulk, Auto-Submitted).</summary>
    public bool Bulk { get; init; }
}

public sealed record InboxMessage
{
    public required string Id { get; init; }
    public required string AccountId { get; init; }
    public string? MessageIdHeader { get; init; }
    public string? InReplyTo { get; init; }
    public string? FromName { get; init; }
    public required string FromAddress { get; init; }
    public IReadOnlyList<string> To { get; init; } = [];
    public IReadOnlyList<string> Cc { get; init; } = [];
    public string Subject { get; init; } = "";
    public string Snippet { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTimeOffset ReceivedAt { get; init; }
    public bool IsRead { get; init; }
    public bool Bulk { get; init; }
    public required string Category { get; init; }
    /// <summary>"rules", "ai" or "you".</summary>
    public string CategorySource { get; init; } = "rules";
    public string? Reason { get; init; }
    public bool Handled { get; init; }

    public string Sender => string.IsNullOrWhiteSpace(FromName) ? FromAddress : FromName!;
}

public sealed record Draft
{
    public required string Id { get; init; }
    public required string AccountId { get; init; }
    public string? ReplyToId { get; init; }
    public IReadOnlyList<string> To { get; init; } = [];
    public IReadOnlyList<string> Cc { get; init; } = [];
    public string Subject { get; init; } = "";
    public string Body { get; init; } = "";
    public string Status { get; init; } = DraftStatus.Draft;
    /// <summary>"you" or "jarvis".</summary>
    public string CreatedBy { get; init; } = "you";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? SentAt { get; init; }
    public string? Error { get; init; }
}

/// <summary>Local store for mail accounts, messages (with full-text search), drafts and learned sender categories.</summary>
public sealed class InboxStore(JarvisDatabase db, IEventBus events)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- accounts ----

    public MailAccount AddAccount(string kind, string address, string? displayName, MailAccountConfig config)
    {
        var a = new MailAccount { Id = Guid.NewGuid().ToString("n"), Kind = kind, Address = address.Trim(), DisplayName = displayName, Config = config, CreatedAt = DateTimeOffset.Now };
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO mail_accounts(id, kind, address, display_name, config, enabled, status, created_at) VALUES ($id, $k, $a, $d, $c, 1, 'new', $t);";
        cmd.Parameters.AddWithValue("$id", a.Id);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$a", a.Address);
        cmd.Parameters.AddWithValue("$d", (object?)displayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c", JsonSerializer.Serialize(config, Json));
        cmd.Parameters.AddWithValue("$t", JarvisDatabase.Format(a.CreatedAt));
        cmd.ExecuteNonQuery();
        Changed();
        return a;
    }

    public IReadOnlyList<MailAccount> Accounts()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, kind, address, display_name, config, enabled, status, status_message, last_sync, created_at FROM mail_accounts ORDER BY created_at;";
        using var r = cmd.ExecuteReader();
        var list = new List<MailAccount>();
        while (r.Read())
            list.Add(new MailAccount
            {
                Id = r.GetString(0), Kind = r.GetString(1), Address = r.GetString(2), DisplayName = r.IsDBNull(3) ? null : r.GetString(3),
                Config = JsonSerializer.Deserialize<MailAccountConfig>(r.GetString(4), Json) ?? new(), Enabled = r.GetInt64(5) == 1,
                Status = r.GetString(6), StatusMessage = r.IsDBNull(7) ? null : r.GetString(7),
                LastSync = r.IsDBNull(8) ? null : DateTimeOffset.Parse(r.GetString(8)), CreatedAt = DateTimeOffset.Parse(r.GetString(9)),
            });
        return list;
    }

    public MailAccount? Account(string id) => Accounts().FirstOrDefault(a => a.Id == id);

    public void SetAccountStatus(string id, string status, string? message, bool synced = false)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE mail_accounts SET status = $s, status_message = $m{(synced ? ", last_sync = $t" : "")} WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$m", (object?)message ?? DBNull.Value);
        if (synced) cmd.Parameters.AddWithValue("$t", JarvisDatabase.Now());
        cmd.ExecuteNonQuery();
        Changed();
    }

    public bool RemoveAccount(string id)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.CommandText = "DELETE FROM message_entities WHERE message_id IN (SELECT id FROM mail_messages WHERE account_id = $id); DELETE FROM mail_messages WHERE account_id = $id; DELETE FROM drafts WHERE account_id = $id;";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "DELETE FROM mail_accounts WHERE id = $id;";
        var n = cmd.ExecuteNonQuery();
        tx.Commit();
        Changed();
        return n > 0;
    }

    // ---- messages ----

    public bool Exists(string accountId, string externalId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM mail_messages WHERE account_id = $a AND external_id = $e;";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$e", externalId);
        return cmd.ExecuteScalar() is not null;
    }

    public InboxMessage Insert(string accountId, FetchedMail m, string category, string source, string reason)
    {
        var body = m.Body.Length > 200_000 ? m.Body[..200_000] : m.Body;
        var msg = new InboxMessage
        {
            Id = Guid.NewGuid().ToString("n"), AccountId = accountId, MessageIdHeader = m.MessageIdHeader, InReplyTo = m.InReplyTo,
            FromName = m.FromName, FromAddress = m.FromAddress.ToLowerInvariant(), To = m.To, Cc = m.Cc, Subject = m.Subject,
            Snippet = Snippet(body), Body = body, ReceivedAt = m.ReceivedAt, IsRead = m.IsRead, Bulk = m.Bulk,
            Category = category, CategorySource = source, Reason = reason,
        };
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO mail_messages(id, account_id, external_id, folder, message_id_header, in_reply_to, thread_key, from_name, from_addr, to_addrs, cc_addrs,
                subject, snippet, body, received_at, is_read, bulk, category, category_source, reason, handled, search_text)
            VALUES ($id, $acc, $ext, 'INBOX', $mid, $irt, $thr, $fn, $fa, $to, $cc, $sub, $snip, $body, $at, $read, $bulk, $cat, $src, $why, 0, $search);
            """;
        cmd.Parameters.AddWithValue("$id", msg.Id);
        cmd.Parameters.AddWithValue("$acc", accountId);
        cmd.Parameters.AddWithValue("$ext", m.ExternalId);
        cmd.Parameters.AddWithValue("$mid", (object?)m.MessageIdHeader ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$irt", (object?)m.InReplyTo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thr", ThreadKey(m.Subject));
        cmd.Parameters.AddWithValue("$fn", (object?)m.FromName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fa", msg.FromAddress);
        cmd.Parameters.AddWithValue("$to", string.Join(", ", m.To));
        cmd.Parameters.AddWithValue("$cc", string.Join(", ", m.Cc));
        cmd.Parameters.AddWithValue("$sub", m.Subject);
        cmd.Parameters.AddWithValue("$snip", msg.Snippet);
        cmd.Parameters.AddWithValue("$body", body);
        cmd.Parameters.AddWithValue("$at", JarvisDatabase.Format(m.ReceivedAt));
        cmd.Parameters.AddWithValue("$read", m.IsRead ? 1 : 0);
        cmd.Parameters.AddWithValue("$bulk", m.Bulk ? 1 : 0);
        cmd.Parameters.AddWithValue("$cat", category);
        cmd.Parameters.AddWithValue("$src", source);
        cmd.Parameters.AddWithValue("$why", reason);
        cmd.Parameters.AddWithValue("$search", TextNormalizer.Normalize($"{m.FromName} {m.FromAddress} {m.Subject} {body}"));
        cmd.ExecuteNonQuery();
        return msg;
    }

    public InboxMessage? Get(string id) => Query("WHERE id = $id", c => c.Parameters.AddWithValue("$id", id), 1).FirstOrDefault();

    public IReadOnlyList<InboxMessage> List(string? category = null, bool includeHandled = false, int limit = 100, string? accountId = null) =>
        Query($"WHERE ($cat IS NULL OR category = $cat) AND ($acc IS NULL OR account_id = $acc){(includeHandled ? "" : " AND handled = 0")}", c =>
        {
            c.Parameters.AddWithValue("$cat", (object?)category ?? DBNull.Value);
            c.Parameters.AddWithValue("$acc", (object?)accountId ?? DBNull.Value);
        }, limit);

    public IReadOnlyList<InboxMessage> Search(string query, int limit = 50)
    {
        var tokens = MemoryStore.Tokens(query);
        if (tokens.Count == 0) return [];
        var match = string.Join(" AND ", tokens.Select(t => $"\"{t}\"*"));
        return Query("WHERE rowid IN (SELECT rowid FROM mail_messages_fts WHERE mail_messages_fts MATCH $q)", c => c.Parameters.AddWithValue("$q", match), limit);
    }

    public IReadOnlyList<InboxMessage> FromSender(string address, int limit = 20) =>
        Query("WHERE from_addr = $a", c => c.Parameters.AddWithValue("$a", address.ToLowerInvariant()), limit);

    public IReadOnlyDictionary<string, int> Counts()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT category, COUNT(*) FROM mail_messages WHERE handled = 0 GROUP BY category;";
        using var r = cmd.ExecuteReader();
        var d = MailCategories.All.ToDictionary(c => c, _ => 0);
        while (r.Read()) d[r.GetString(0)] = r.GetInt32(1);
        return d;
    }

    public bool SetCategory(string id, string category, string source)
    {
        if (!MailCategories.All.Contains(category)) throw new ArgumentException($"Unknown category '{category}'.");
        var n = Exec("UPDATE mail_messages SET category = $c, category_source = $s, reason = $r WHERE id = $id;", c =>
        {
            c.Parameters.AddWithValue("$id", id);
            c.Parameters.AddWithValue("$c", category);
            c.Parameters.AddWithValue("$s", source);
            c.Parameters.AddWithValue("$r", source == "you" ? "You put it here." : "Reclassified.");
        });
        Changed();
        return n > 0;
    }

    public bool SetHandled(string id, bool handled)
    {
        var n = Exec("UPDATE mail_messages SET handled = $h WHERE id = $id;", c => { c.Parameters.AddWithValue("$id", id); c.Parameters.AddWithValue("$h", handled ? 1 : 0); });
        Changed();
        return n > 0;
    }

    public void LinkEntity(string messageId, string entityId) =>
        Exec("INSERT OR IGNORE INTO message_entities(message_id, entity_id) VALUES ($m, $e);", c => { c.Parameters.AddWithValue("$m", messageId); c.Parameters.AddWithValue("$e", entityId); });

    public IReadOnlyList<string> EntityIdsOf(string messageId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT entity_id FROM message_entities WHERE message_id = $m;";
        cmd.Parameters.AddWithValue("$m", messageId);
        using var r = cmd.ExecuteReader();
        var ids = new List<string>();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    }

    public IReadOnlyList<InboxMessage> AboutEntity(string entityId, int limit = 20) =>
        Query("WHERE id IN (SELECT message_id FROM message_entities WHERE entity_id = $e)", c => c.Parameters.AddWithValue("$e", entityId), limit);

    // ---- learned sender categories ----

    public string? SenderRule(string address)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT category FROM mail_sender_rules WHERE address = $a;";
        cmd.Parameters.AddWithValue("$a", address.ToLowerInvariant());
        return cmd.ExecuteScalar() as string;
    }

    public void SetSenderRule(string address, string category) =>
        Exec("INSERT INTO mail_sender_rules(address, category, created_at) VALUES ($a, $c, $t) ON CONFLICT(address) DO UPDATE SET category = $c;", c =>
        {
            c.Parameters.AddWithValue("$a", address.ToLowerInvariant());
            c.Parameters.AddWithValue("$c", category);
            c.Parameters.AddWithValue("$t", JarvisDatabase.Now());
        });

    // ---- drafts ----

    public Draft CreateDraft(string accountId, string? replyToId, IReadOnlyList<string> to, IReadOnlyList<string> cc, string subject, string body, string createdBy)
    {
        if (to.Count == 0) throw new ArgumentException("A draft needs at least one recipient.");
        var now = DateTimeOffset.Now;
        var d = new Draft { Id = Guid.NewGuid().ToString("n"), AccountId = accountId, ReplyToId = replyToId, To = to, Cc = cc, Subject = subject, Body = body, CreatedBy = createdBy, CreatedAt = now, UpdatedAt = now };
        Exec("""
            INSERT INTO drafts(id, account_id, reply_to_id, to_addrs, cc_addrs, subject, body, status, created_by, created_at, updated_at)
            VALUES ($id, $acc, $rt, $to, $cc, $sub, $body, 'draft', $by, $t, $t);
            """, c =>
        {
            c.Parameters.AddWithValue("$id", d.Id);
            c.Parameters.AddWithValue("$acc", accountId);
            c.Parameters.AddWithValue("$rt", (object?)replyToId ?? DBNull.Value);
            c.Parameters.AddWithValue("$to", string.Join(", ", to));
            c.Parameters.AddWithValue("$cc", string.Join(", ", cc));
            c.Parameters.AddWithValue("$sub", subject);
            c.Parameters.AddWithValue("$body", body);
            c.Parameters.AddWithValue("$by", createdBy);
            c.Parameters.AddWithValue("$t", JarvisDatabase.Format(now));
        });
        Changed();
        return d;
    }

    public Draft? UpdateDraft(string id, IReadOnlyList<string>? to, IReadOnlyList<string>? cc, string? subject, string? body)
    {
        var d = GetDraft(id);
        if (d is null || d.Status != DraftStatus.Draft) return null;
        Exec("UPDATE drafts SET to_addrs = $to, cc_addrs = $cc, subject = $sub, body = $body, updated_at = $t WHERE id = $id;", c =>
        {
            c.Parameters.AddWithValue("$id", id);
            c.Parameters.AddWithValue("$to", string.Join(", ", to ?? d.To));
            c.Parameters.AddWithValue("$cc", string.Join(", ", cc ?? d.Cc));
            c.Parameters.AddWithValue("$sub", subject ?? d.Subject);
            c.Parameters.AddWithValue("$body", body ?? d.Body);
            c.Parameters.AddWithValue("$t", JarvisDatabase.Now());
        });
        Changed();
        return GetDraft(id);
    }

    public void SetDraftStatus(string id, string status, string? error = null)
    {
        Exec($"UPDATE drafts SET status = $s, error = $e, updated_at = $t{(status == DraftStatus.Sent ? ", sent_at = $t" : "")} WHERE id = $id;", c =>
        {
            c.Parameters.AddWithValue("$id", id);
            c.Parameters.AddWithValue("$s", status);
            c.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
            c.Parameters.AddWithValue("$t", JarvisDatabase.Now());
        });
        Changed();
    }

    public Draft? GetDraft(string id) => Drafts(null, id).FirstOrDefault();

    public IReadOnlyList<Draft> Drafts(string? status = DraftStatus.Draft, string? id = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, account_id, reply_to_id, to_addrs, cc_addrs, subject, body, status, created_by, created_at, updated_at, sent_at, error
            FROM drafts WHERE ($s IS NULL OR status = $s) AND ($id IS NULL OR id = $id) ORDER BY updated_at DESC LIMIT 200;
            """;
        cmd.Parameters.AddWithValue("$s", (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<Draft>();
        while (r.Read())
            list.Add(new Draft
            {
                Id = r.GetString(0), AccountId = r.GetString(1), ReplyToId = r.IsDBNull(2) ? null : r.GetString(2),
                To = SplitAddrs(r.GetString(3)), Cc = r.IsDBNull(4) ? [] : SplitAddrs(r.GetString(4)), Subject = r.GetString(5), Body = r.GetString(6),
                Status = r.GetString(7), CreatedBy = r.GetString(8), CreatedAt = DateTimeOffset.Parse(r.GetString(9)), UpdatedAt = DateTimeOffset.Parse(r.GetString(10)),
                SentAt = r.IsDBNull(11) ? null : DateTimeOffset.Parse(r.GetString(11)), Error = r.IsDBNull(12) ? null : r.GetString(12),
            });
        return list;
    }

    // ---- helpers ----

    private IReadOnlyList<InboxMessage> Query(string where, Action<SqliteCommand> bind, int limit)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, account_id, message_id_header, in_reply_to, from_name, from_addr, to_addrs, cc_addrs, subject, snippet, body, received_at,
                   is_read, bulk, category, category_source, reason, handled
            FROM mail_messages {where} ORDER BY received_at DESC LIMIT {Math.Clamp(limit, 1, 1000)};
            """;
        bind(cmd);
        using var r = cmd.ExecuteReader();
        var list = new List<InboxMessage>();
        while (r.Read())
            list.Add(new InboxMessage
            {
                Id = r.GetString(0), AccountId = r.GetString(1), MessageIdHeader = r.IsDBNull(2) ? null : r.GetString(2), InReplyTo = r.IsDBNull(3) ? null : r.GetString(3),
                FromName = r.IsDBNull(4) ? null : r.GetString(4), FromAddress = r.GetString(5), To = r.IsDBNull(6) ? [] : SplitAddrs(r.GetString(6)),
                Cc = r.IsDBNull(7) ? [] : SplitAddrs(r.GetString(7)), Subject = r.IsDBNull(8) ? "" : r.GetString(8), Snippet = r.IsDBNull(9) ? "" : r.GetString(9),
                Body = r.IsDBNull(10) ? "" : r.GetString(10), ReceivedAt = DateTimeOffset.Parse(r.GetString(11)), IsRead = r.GetInt64(12) == 1, Bulk = r.GetInt64(13) == 1,
                Category = r.GetString(14), CategorySource = r.GetString(15), Reason = r.IsDBNull(16) ? null : r.GetString(16), Handled = r.GetInt64(17) == 1,
            });
        return list;
    }

    private int Exec(string sql, Action<SqliteCommand> bind)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        return cmd.ExecuteNonQuery();
    }

    private void Changed() => events.Publish(EventTypes.InboxChanged, new { });

    private static IReadOnlyList<string> SplitAddrs(string s) =>
        s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static string Snippet(string body)
    {
        var flat = System.Text.RegularExpressions.Regex.Replace(body, @"\s+", " ").Trim();
        return flat.Length > 220 ? flat[..217] + "…" : flat;
    }

    /// <summary>"Re: Fwd: Proposal" → "proposal": groups a conversation without provider thread ids.</summary>
    internal static string ThreadKey(string subject) =>
        TextNormalizer.Normalize(System.Text.RegularExpressions.Regex.Replace(subject, @"^(?:\s*(?:re|fw|fwd|aw|رد|تحويل)\s*:\s*)+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).Trim();
}
