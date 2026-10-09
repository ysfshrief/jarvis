using System.Text.RegularExpressions;
using Jarvis.Core.Language;

namespace Jarvis.Core.Inbox;

public sealed record Classification(string Category, string Reason, string Source = "rules");

/// <summary>What the classifier knows about the user and their world.</summary>
public sealed record ClassifierContext(
    IReadOnlyCollection<string> MyAddresses,
    IReadOnlyCollection<string> VipSenders,
    Func<string, string?> SenderRule,
    Func<FetchedMail, string?> KnownContact);

/// <summary>
/// Sorts mail into urgent / important / needs response / FYI / noise with deterministic, explainable rules
/// (English and Arabic). Your own corrections win: once you move a sender's mail, theirs follows.
/// </summary>
public static partial class InboxClassifier
{
    public static Classification Classify(FetchedMail m, ClassifierContext ctx)
    {
        var from = m.FromAddress.ToLowerInvariant();
        if (ctx.SenderRule(from) is { } learned)
            return new(learned, "You sorted this sender's mail here before.", "you");

        var text = TextNormalizer.Normalize($"{m.Subject}\n{Head(m.Body)}");
        var subject = TextNormalizer.Normalize(m.Subject);
        var vip = ctx.VipSenders.Any(v => Matches(from, v));
        var contact = ctx.KnownContact(m);
        var automated = Automated().IsMatch(from) || m.Bulk;
        var toMe = m.To.Any(a => ctx.MyAddresses.Contains(a.ToLowerInvariant())) || ctx.MyAddresses.Count == 0;

        if (!vip && contact is null && (automated || Promotional().IsMatch(subject)) && !Security().IsMatch(text))
            return new(MailCategories.Noise, m.Bulk ? "Mailing list or bulk mail." : automated ? "Sent by an automated address." : "Looks promotional.");

        if (Urgent().IsMatch(text) && (!automated || vip || contact is not null || Security().IsMatch(text)))
            return new(MailCategories.Urgent, $"Marked or worded as urgent{Who(vip, contact)}.");

        if (automated && Security().IsMatch(text))
            return new(MailCategories.Important, "Security or account notice.");

        if (!automated && toMe && Asks().IsMatch(text))
            return new(MailCategories.NeedsResponse, $"Asks you something directly{Who(vip, contact)}.");

        if (vip) return new(MailCategories.Important, "From a VIP sender.");
        if (contact is not null) return new(MailCategories.Important, $"From {contact}, someone JARVIS knows.");
        if (Business().IsMatch(text)) return new(MailCategories.Important, "About a contract, invoice, payment or meeting.");

        return new(MailCategories.Fyi, toMe ? "Informational." : "You're copied, not addressed.");
    }

    private static string Who(bool vip, string? contact) => vip ? " by a VIP sender" : contact is not null ? $" by {contact}" : "";

    private static bool Matches(string from, string vip)
    {
        vip = vip.Trim().ToLowerInvariant().TrimStart('@');
        return vip.Length > 0 && (from == vip || from.EndsWith("@" + vip) || from.EndsWith("." + vip));
    }

    private static string Head(string body) => body.Length > 3000 ? body[..3000] : body;

    [GeneratedRegex(@"(?:^|[._+-])(?:no-?reply|do-?not-?reply|donotreply|notifications?|newsletter|news|marketing|mailer|bounce|digest|updates|promo|offers)(?:[._+-]|@)", RegexOptions.IgnoreCase)]
    private static partial Regex Automated();

    [GeneratedRegex(@"\b(?:\d+% off|sale|discount|deal of|limited time|webinar|newsletter|unsubscribe|black friday|coupon|promo)\b|خصم|عرض خاص|تخفيضات|كوبون", RegexOptions.IgnoreCase)]
    private static partial Regex Promotional();

    [GeneratedRegex(@"\b(?:urgent|asap|immediately|right away|by (?:today|tonight|eod|end of (?:the )?day)|deadline (?:is )?today|overdue|final notice|time[- ]sensitive|emergency)\b|مستعجل|ضروري|حالا|فورا|النهارده ضروري|اخر ميعاد النهارده|طارئ", RegexOptions.IgnoreCase)]
    private static partial Regex Urgent();

    [GeneratedRegex(@"\b(?:password|sign-?in|login attempt|security alert|verification code|two-factor|2fa|suspicious|unusual activity)\b|كلمه السر|تسجيل دخول|رمز التحقق", RegexOptions.IgnoreCase)]
    private static partial Regex Security();

    [GeneratedRegex(@"\?|\b(?:can you|could you|would you|will you|please (?:confirm|review|send|let me know|advise|approve|reply)|let me know|what do you think|are you available|your (?:thoughts|feedback|approval)|waiting for your)\b|ممكن|ياريت|محتاج رأيك|محتاجين|رد عليا|قولي|تقدر|هل", RegexOptions.IgnoreCase)]
    private static partial Regex Asks();

    [GeneratedRegex(@"\b(?:contract|invoice|payment|proposal|quotation|quote|agreement|offer letter|meeting|interview|purchase order|po\b|nda)\b|عقد|فاتوره|دفع|عرض سعر|اجتماع|ميتنج|مقابله", RegexOptions.IgnoreCase)]
    private static partial Regex Business();
}
