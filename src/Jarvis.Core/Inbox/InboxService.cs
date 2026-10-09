using Jarvis.Core.Activity;
using Jarvis.Core.Memory;
using Jarvis.Core.Notifications;
using Jarvis.Core.Security;
using Jarvis.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Inbox;

public sealed record SyncResult(int New, IReadOnlyDictionary<string, int> NewByCategory, IReadOnlyList<string> Errors);

/// <summary>
/// The executive inbox: syncs connected accounts (read-only), sorts mail, links it to people and
/// organisations JARVIS knows, alerts on urgent mail, and keeps drafts. It never sends on its own:
/// <see cref="SendAsync"/> is only reached through the critical, always-approved inbox_send tool.
/// </summary>
public sealed class InboxService(
    InboxStore store,
    IEnumerable<IMailConnector> connectors,
    ISecretStore secrets,
    EntityStore entities,
    NotificationCenter notifications,
    ISettingsStore settings,
    ActivityLog activity,
    Learning.WritingSamples writing,
    ILogger<InboxService> logger)
{
    private readonly SemaphoreSlim _sync = new(1, 1);

    public InboxStore Store => store;
    public bool HasAccounts => store.Accounts().Any(a => a.Enabled);

    public IMailConnector Connector(string kind) =>
        connectors.FirstOrDefault(c => c.Kind == kind) ?? throw new MailConnectorException($"No connector for '{kind}'.");

    /// <summary>Adds an account after proving the credentials work. The password goes only to the secret store.</summary>
    public async Task<MailAccount> AddAccountAsync(string kind, string address, string? displayName, MailAccountConfig config, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(address) || !address.Contains('@')) throw new ArgumentException("Enter the email address.");
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Enter the password or app password.");
        if (string.IsNullOrWhiteSpace(config.ImapHost) || string.IsNullOrWhiteSpace(config.SmtpHost)) throw new ArgumentException("Enter the IMAP and SMTP servers.");
        var connector = Connector(kind);
        var account = store.AddAccount(kind, address, displayName, config);
        try
        {
            await connector.TestAsync(account, password, ct).ConfigureAwait(false);
        }
        catch
        {
            store.RemoveAccount(account.Id);
            throw;
        }
        secrets.Set(account.PasswordSecret, password);
        store.SetAccountStatus(account.Id, "connected", null);
        activity.Record(ActivityKinds.System, $"Mail account connected: {address}", status: "ok");
        return store.Account(account.Id)!;
    }

    public bool RemoveAccount(string id)
    {
        var a = store.Account(id);
        if (a is null) return false;
        secrets.Remove(a.PasswordSecret);
        activity.Record(ActivityKinds.System, $"Mail account removed: {a.Address}", status: "ok");
        return store.RemoveAccount(id);
    }

    /// <summary>
    /// Reads recent email from each account's Sent folder (read-only) into the writing samples, for
    /// writing-style learning. Does nothing unless that's turned on; turning it off deletes the samples.
    /// </summary>
    public async Task<int> CollectWritingSamplesAsync(CancellationToken ct)
    {
        if (!settings.Current.Memory.LearnWritingStyle)
        {
            if (writing.Count() > 0) writing.Clear();
            return 0;
        }
        var added = 0;
        foreach (var a in store.Accounts().Where(a => a.Enabled))
        {
            var secret = secrets.Get(a.PasswordSecret);
            if (secret is null) continue;
            try
            {
                foreach (var m in await Connector(a.Kind).FetchSentAsync(a, secret, 60, ct).ConfigureAwait(false))
                    if (writing.Add($"sent:{a.Id}:{m.ExternalId}", "sent folder", m.Body, m.ReceivedAt)) added++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation(ex, "Couldn't read the Sent folder of {Account}", a.Address);
            }
        }
        return added;
    }

    public async Task<SyncResult> SyncAsync(CancellationToken ct, string? accountId = null)
    {
        await _sync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var accounts = store.Accounts().Where(a => a.Enabled && (accountId is null || a.Id == accountId)).ToList();
            var mine = accounts.Select(a => a.Address.ToLowerInvariant()).ToHashSet();
            var s = settings.Current;
            var ctx = new ClassifierContext(mine, s.Inbox.VipSenders, store.SenderRule, KnownContact);
            var byCat = MailCategories.All.ToDictionary(c => c, _ => 0);
            var errors = new List<string>();
            var total = 0;
            foreach (var a in accounts)
            {
                var secret = secrets.Get(a.PasswordSecret);
                if (secret is null) { store.SetAccountStatus(a.Id, "error", "The password is missing; reconnect the account."); errors.Add(a.Address); continue; }
                var initial = a.LastSync is null;
                var since = a.LastSync?.AddDays(-2) ?? DateTimeOffset.Now.AddDays(-s.Inbox.InitialDays);
                IReadOnlyList<FetchedMail> mail;
                try { mail = await Connector(a.Kind).FetchAsync(a, secret, since, initial ? 300 : 150, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Mail sync failed for {Account}", a.Address);
                    store.SetAccountStatus(a.Id, "error", ex.Message);
                    errors.Add($"{a.Address}: {ex.Message}");
                    continue;
                }
                foreach (var m in mail.OrderBy(m => m.ReceivedAt))
                {
                    if (mine.Contains(m.FromAddress.ToLowerInvariant()) || store.Exists(a.Id, m.ExternalId)) continue;
                    var c = InboxClassifier.Classify(m, ctx);
                    var saved = store.Insert(a.Id, m, c.Category, c.Source, c.Reason);
                    foreach (var e in entities.Mentioned($"{m.FromName} {m.Subject}")) store.LinkEntity(saved.Id, e.Id);
                    if (OrganizationOf(m) is { } org) store.LinkEntity(saved.Id, org.Id);
                    byCat[c.Category]++;
                    total++;
                    if (c.Category == MailCategories.Urgent && !initial && !m.IsRead && s.Inbox.NotifyUrgent)
                        await NotifyAsync(saved, ct).ConfigureAwait(false);
                }
                store.SetAccountStatus(a.Id, "connected", null, synced: true);
            }
            if (total > 0) activity.Record(ActivityKinds.System, $"Inbox: {total} new message(s)", status: "ok",
                details: string.Join(", ", byCat.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key} {kv.Value}")));
            return new SyncResult(total, byCat, errors);
        }
        finally { _sync.Release(); }
    }

    /// <summary>Prepares a reply as a draft. Nothing is sent.</summary>
    public Draft DraftReply(string messageId, string body, string createdBy)
    {
        var m = store.Get(messageId) ?? throw new ArgumentException("That message isn't in the inbox.");
        var subject = System.Text.RegularExpressions.Regex.IsMatch(m.Subject, @"^\s*re\s*:", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? m.Subject : $"Re: {m.Subject}";
        return store.CreateDraft(m.AccountId, m.Id, [m.FromAddress], [], subject, body, createdBy);
    }

    /// <summary>Prepares a new message as a draft from the first (or given) account. Nothing is sent.</summary>
    public Draft DraftNew(IReadOnlyList<string> to, IReadOnlyList<string> cc, string subject, string body, string createdBy, string? accountId = null)
    {
        var account = (accountId is null ? store.Accounts().FirstOrDefault(a => a.Enabled) : store.Account(accountId))
            ?? throw new ArgumentException("Connect a mail account first (Settings → Accounts).");
        foreach (var addr in to.Concat(cc))
            if (!MimeKit.MailboxAddress.TryParse(addr, out _)) throw new ArgumentException($"“{addr}” isn't a valid email address.");
        return store.CreateDraft(account.Id, null, to, cc, subject, body, createdBy);
    }

    /// <summary>Sends an approved draft. Only the inbox_send tool calls this, after the user's explicit approval.</summary>
    public async Task<Draft> SendAsync(string draftId, CancellationToken ct)
    {
        var d = store.GetDraft(draftId) ?? throw new ArgumentException("That draft doesn't exist.");
        if (d.Status == DraftStatus.Sent) throw new ArgumentException("That draft was already sent.");
        if (d.Status != DraftStatus.Draft && d.Status != DraftStatus.Failed) throw new ArgumentException("That draft was discarded.");
        var a = store.Account(d.AccountId) ?? throw new ArgumentException("The account for this draft was removed.");
        var secret = secrets.Get(a.PasswordSecret) ?? throw new MailConnectorException("The account's password is missing; reconnect it.");
        var replyTo = d.ReplyToId is null ? null : store.Get(d.ReplyToId);
        try
        {
            var id = await Connector(a.Kind).SendAsync(a, secret, new OutgoingMail(a.Address, a.DisplayName, d.To, d.Cc, d.Subject, d.Body, replyTo?.MessageIdHeader), ct).ConfigureAwait(false);
            store.SetDraftStatus(d.Id, DraftStatus.Sent);
            if (replyTo is not null) store.SetHandled(replyTo.Id, true);
            // What the user approved and sent is how they write (opt-in).
            if (settings.Current.Memory.LearnWritingStyle) writing.Add("draft:" + d.Id, "sent draft", d.Body, DateTimeOffset.Now);
            activity.Record(ActivityKinds.Tool, $"Email sent to {string.Join(", ", d.To)}: {d.Subject}", "inbox_send", status: "ok", details: id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            store.SetDraftStatus(d.Id, DraftStatus.Failed, ex.Message);
            throw;
        }
        return store.GetDraft(d.Id)!;
    }

    /// <summary>Moves a message (and, from now on, its sender's mail) to a category.</summary>
    public bool Recategorize(string messageId, string category)
    {
        var m = store.Get(messageId);
        if (m is null) return false;
        store.SetCategory(messageId, category, "you");
        store.SetSenderRule(m.FromAddress, category);
        return true;
    }

    private string? KnownContact(FetchedMail m)
    {
        if (!string.IsNullOrWhiteSpace(m.FromName) && entities.Find(m.FromName!, EntityTypes.Person) is { } p) return p.Name;
        return OrganizationOf(m)?.Name;
    }

    /// <summary>ahmed@citycrep.com → the "CityCrep" organisation, if JARVIS knows it.</summary>
    private Entity? OrganizationOf(FetchedMail m)
    {
        var domain = m.FromAddress.Split('@').LastOrDefault() ?? "";
        var parts = domain.Split('.');
        if (parts.Length < 2) return null;
        var name = parts[^2];
        if (name is "gmail" or "yahoo" or "outlook" or "hotmail" or "icloud" or "live" or "proton" or "protonmail" or "aol" or "zoho" or "mail" or "co" or "com") return null;
        return entities.Find(name, EntityTypes.Organization);
    }

    private Task NotifyAsync(InboxMessage m, CancellationToken ct)
    {
        var ar = Jarvis.Core.Settings.LanguagePolicy.Default(settings.Current) == Jarvis.Core.Language.Lang.Ar;
        return notifications.PostAsync(new Notification
        {
            Title = ar ? $"إيميل مستعجل من {m.Sender}" : $"Urgent email from {m.Sender}",
            Body = m.Subject,
            Priority = NotificationPriority.High,
            Source = "inbox",
            GroupKey = $"mail-{m.Id}",
        }, ct);
    }
}
