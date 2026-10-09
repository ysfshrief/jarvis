using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace Jarvis.Core.Inbox;

public sealed record OutgoingMail(string From, string? FromName, IReadOnlyList<string> To, IReadOnlyList<string> Cc, string Subject, string Body, string? InReplyTo);

public sealed class MailConnectorException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A way to reach one kind of mailbox.</summary>
public interface IMailConnector
{
    string Kind { get; }
    Task TestAsync(MailAccount account, string secret, CancellationToken ct);
    Task<IReadOnlyList<FetchedMail>> FetchAsync(MailAccount account, string secret, DateTimeOffset since, int max, CancellationToken ct);
    /// <summary>The most recent messages in the account's Sent folder (read-only); empty if there isn't one.</summary>
    Task<IReadOnlyList<FetchedMail>> FetchSentAsync(MailAccount account, string secret, int max, CancellationToken ct);
    /// <summary>Sends and returns the Message-ID. Called only after the user approved this exact message.</summary>
    Task<string> SendAsync(MailAccount account, string secret, OutgoingMail mail, CancellationToken ct);
}

/// <summary>
/// Standard IMAP (reading) + SMTP (sending) with a password or app password. Works with Gmail (app password),
/// Yahoo, iCloud, Zoho, Fastmail, company mail servers… Reading never marks messages as read.
/// </summary>
public sealed class ImapSmtpConnector : IMailConnector
{
    public const string KindName = "imap";
    public string Kind => KindName;

    public async Task TestAsync(MailAccount account, string secret, CancellationToken ct)
    {
        using var imap = await ImapAsync(account, secret, ct).ConfigureAwait(false);
        await imap.Inbox.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
        await imap.DisconnectAsync(true, ct).ConfigureAwait(false);
        using var smtp = await SmtpAsync(account, secret, ct).ConfigureAwait(false);
        await smtp.DisconnectAsync(true, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FetchedMail>> FetchAsync(MailAccount account, string secret, DateTimeOffset since, int max, CancellationToken ct)
    {
        using var imap = await ImapAsync(account, secret, ct).ConfigureAwait(false);
        var folder = string.Equals(account.Config.Folder, "INBOX", StringComparison.OrdinalIgnoreCase) ? imap.Inbox : await imap.GetFolderAsync(account.Config.Folder, ct).ConfigureAwait(false);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false); // read-only: JARVIS never changes your mailbox by reading
        var uids = await folder.SearchAsync(SearchQuery.DeliveredAfter(since.LocalDateTime.Date.AddDays(-1)), ct).ConfigureAwait(false);
        var pick = uids.OrderByDescending(u => u.Id).Take(max).ToList();
        var summaries = pick.Count == 0 ? [] : await folder.FetchAsync(pick, MessageSummaryItems.Flags | MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate, ct).ConfigureAwait(false);
        var info = summaries.ToDictionary(s => s.UniqueId);
        var list = new List<FetchedMail>();
        foreach (var uid in pick)
        {
            var msg = await folder.GetMessageAsync(uid, ct).ConfigureAwait(false);
            var s = info.GetValueOrDefault(uid);
            // Not every sender sets a Date header; the server's arrival time is the fallback.
            var date = msg.Date != DateTimeOffset.MinValue ? msg.Date : s?.InternalDate ?? DateTimeOffset.Now;
            if (date < since.AddDays(-1)) continue;
            list.Add(Map(msg, $"{folder.UidValidity}:{uid.Id}", s?.Flags?.HasFlag(MessageFlags.Seen) == true) with { ReceivedAt = date });
        }
        await imap.DisconnectAsync(true, ct).ConfigureAwait(false);
        return list;
    }

    private static readonly string[] SentNames = ["Sent", "Sent Items", "Sent Mail", "Sent Messages", "[Gmail]/Sent Mail", "INBOX.Sent", "INBOX/Sent", "المرسل", "البريد المرسل"];

    public async Task<IReadOnlyList<FetchedMail>> FetchSentAsync(MailAccount account, string secret, int max, CancellationToken ct)
    {
        using var imap = await ImapAsync(account, secret, ct).ConfigureAwait(false);
        IMailFolder? sent = null;
        if ((imap.Capabilities & (ImapCapabilities.SpecialUse | ImapCapabilities.XList)) != 0)
        {
            try { sent = imap.GetFolder(SpecialFolder.Sent); } catch (NotSupportedException) { }
        }
        if (sent is null && imap.PersonalNamespaces.Count > 0)
        {
            var all = await imap.GetFoldersAsync(imap.PersonalNamespaces[0], cancellationToken: ct).ConfigureAwait(false);
            sent = all.FirstOrDefault(f => SentNames.Any(n => n.Equals(f.FullName, StringComparison.OrdinalIgnoreCase)))
                ?? all.FirstOrDefault(f => SentNames.Any(n => n.Equals(f.Name, StringComparison.OrdinalIgnoreCase)));
        }
        if (sent is null) { await imap.DisconnectAsync(true, ct).ConfigureAwait(false); return []; }
        await sent.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
        var list = new List<FetchedMail>();
        for (var i = sent.Count - 1; i >= 0 && list.Count < max; i--)
        {
            var msg = await sent.GetMessageAsync(i, ct).ConfigureAwait(false);
            if (!msg.From.Mailboxes.Any(m => m.Address.Equals(account.Address, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(Map(msg, msg.MessageId ?? $"{sent.UidValidity}:{i}", true)); // Message-ID: stable as new mail arrives
        }
        await imap.DisconnectAsync(true, ct).ConfigureAwait(false);
        return list;
    }

    public async Task<string> SendAsync(MailAccount account, string secret, OutgoingMail mail, CancellationToken ct)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(mail.FromName ?? "", mail.From));
        foreach (var t in mail.To) msg.To.Add(MailboxAddress.Parse(t));
        foreach (var c in mail.Cc) msg.Cc.Add(MailboxAddress.Parse(c));
        msg.Subject = mail.Subject;
        if (!string.IsNullOrEmpty(mail.InReplyTo))
        {
            msg.InReplyTo = mail.InReplyTo;
            msg.References.Add(mail.InReplyTo);
        }
        msg.Body = new TextPart("plain") { Text = mail.Body };
        msg.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(mail.From.Split('@').LastOrDefault() ?? "jarvis.local");
        using var smtp = await SmtpAsync(account, secret, ct).ConfigureAwait(false);
        await smtp.SendAsync(msg, ct).ConfigureAwait(false);
        await smtp.DisconnectAsync(true, ct).ConfigureAwait(false);
        return msg.MessageId;
    }

    internal static FetchedMail Map(MimeMessage msg, string externalId, bool seen)
    {
        var from = msg.From.Mailboxes.FirstOrDefault();
        var body = msg.TextBody;
        if (string.IsNullOrWhiteSpace(body) && msg.HtmlBody is { } html)
            body = Jarvis.Core.Tools.Builtin.WebReadTool.Extract(html, "text/html").Text;
        return new FetchedMail
        {
            ExternalId = externalId,
            MessageIdHeader = msg.MessageId is null ? null : $"<{msg.MessageId}>",
            InReplyTo = msg.InReplyTo is null ? null : $"<{msg.InReplyTo}>",
            FromName = string.IsNullOrWhiteSpace(from?.Name) ? null : from!.Name,
            FromAddress = from?.Address ?? "unknown",
            To = msg.To.Mailboxes.Select(m => m.Address).ToList(),
            Cc = msg.Cc.Mailboxes.Select(m => m.Address).ToList(),
            Subject = msg.Subject ?? "",
            Body = body ?? "",
            ReceivedAt = msg.Date == DateTimeOffset.MinValue ? DateTimeOffset.Now : msg.Date,
            IsRead = seen,
            Bulk = msg.Headers.Contains("List-Unsubscribe") || msg.Headers.Contains(HeaderId.ListId) ||
                   (msg.Headers["Precedence"] ?? "").Contains("bulk", StringComparison.OrdinalIgnoreCase) ||
                   (msg.Headers["Auto-Submitted"] is { } auto && !auto.Equals("no", StringComparison.OrdinalIgnoreCase)),
        };
    }

    private static async Task<ImapClient> ImapAsync(MailAccount a, string secret, CancellationToken ct)
    {
        var c = new ImapClient { Timeout = 30_000 };
        try
        {
            await c.ConnectAsync(a.Config.ImapHost, a.Config.ImapPort, Security(a.Config.ImapSecurity), ct).ConfigureAwait(false);
            await c.AuthenticateAsync(a.Config.Username.Length > 0 ? a.Config.Username : a.Address, secret, ct).ConfigureAwait(false);
            return c;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            c.Dispose();
            throw Explain("IMAP", a.Config.ImapHost, ex);
        }
    }

    private static async Task<SmtpClient> SmtpAsync(MailAccount a, string secret, CancellationToken ct)
    {
        var c = new SmtpClient { Timeout = 30_000 };
        try
        {
            await c.ConnectAsync(a.Config.SmtpHost, a.Config.SmtpPort, Security(a.Config.SmtpSecurity), ct).ConfigureAwait(false);
            if (c.Capabilities.HasFlag(SmtpCapabilities.Authentication))
                await c.AuthenticateAsync(a.Config.Username.Length > 0 ? a.Config.Username : a.Address, secret, ct).ConfigureAwait(false);
            return c;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            c.Dispose();
            throw Explain("SMTP", a.Config.SmtpHost, ex);
        }
    }

    private static SecureSocketOptions Security(string s) => s switch
    {
        "ssl" => SecureSocketOptions.SslOnConnect,
        "starttls" => SecureSocketOptions.StartTls,
        "none" => SecureSocketOptions.None,
        _ => SecureSocketOptions.Auto,
    };

    private static MailConnectorException Explain(string what, string host, Exception ex) => ex switch
    {
        AuthenticationException => new($"{what} sign-in to {host} was refused. Check the username and password (Gmail needs an app password).", ex),
        System.Net.Sockets.SocketException => new($"Couldn't reach the {what} server {host}.", ex),
        SslHandshakeException => new($"Secure connection to {host} failed; check the port and security setting.", ex),
        _ => new($"{what} error with {host}: {ex.Message}", ex),
    };
}

/// <summary>Known providers: settings presets and an honest note on what each needs.</summary>
public sealed record MailPreset(string Id, string Name, MailAccountConfig Config, string Note);

/// <summary>Every messaging service JARVIS was asked to support, and what's actually possible today.</summary>
public sealed record ConnectorInfo(string Id, string Name, string Status, string How, string Note);

public static class MailCatalog
{
    public static readonly IReadOnlyList<MailPreset> Presets =
    [
        new("gmail", "Gmail", new() { ImapHost = "imap.gmail.com", ImapPort = 993, SmtpHost = "smtp.gmail.com", SmtpPort = 465 },
            "Turn on 2-step verification, then create an app password at myaccount.google.com/apppasswords and use it here."),
        new("yahoo", "Yahoo Mail", new() { ImapHost = "imap.mail.yahoo.com", ImapPort = 993, SmtpHost = "smtp.mail.yahoo.com", SmtpPort = 465 },
            "Use an app password (Account security → Generate app password)."),
        new("icloud", "iCloud Mail", new() { ImapHost = "imap.mail.me.com", ImapPort = 993, SmtpHost = "smtp.mail.me.com", SmtpPort = 587, SmtpSecurity = "starttls" },
            "Use an app-specific password from appleid.apple.com."),
        new("zoho", "Zoho Mail", new() { ImapHost = "imap.zoho.com", ImapPort = 993, SmtpHost = "smtp.zoho.com", SmtpPort = 465 }, "Enable IMAP access in Zoho settings."),
        new("custom", "Other (IMAP/SMTP)", new(), "Your provider's IMAP and SMTP settings."),
    ];

    public static readonly IReadOnlyList<ConnectorInfo> Connectors =
    [
        new("imap", "Email (IMAP/SMTP)", "supported", "Password or app password", "Gmail, Yahoo, iCloud, Zoho, company servers. Read-only access to your inbox; sending only after your approval."),
        new("outlook", "Outlook.com / Microsoft 365", "official API required", "Microsoft sign-in (OAuth)",
            "Microsoft turned off password sign-in for Outlook mail, so it needs a Microsoft app registration and sign-in. Not available in this build."),
        new("gmail-api", "Gmail (Google sign-in)", "official API required", "Google sign-in (OAuth)",
            "Signing in with Google needs a Google Cloud app; until then, use Gmail with an app password over IMAP."),
        new("whatsapp", "WhatsApp", "official API required", "WhatsApp Business Cloud API", "Only business numbers through Meta's Cloud API; personal chats can't be read by other apps."),
        new("instagram", "Instagram DMs", "official API required", "Meta Graph API (business/creator accounts, app review)", "Personal account messages aren't available to other apps."),
        new("facebook", "Facebook Messenger", "official API required", "Messenger Platform (Pages only)", "Personal Messenger chats aren't available to other apps."),
        new("linkedin", "LinkedIn messages", "not possible", "—", "LinkedIn has no public messaging API; automating the website would break its terms."),
        new("x", "X (Twitter) DMs", "paid API required", "X API paid tier", "Direct messages need a paid X API plan, so it's off (free-first)."),
    ];
}
