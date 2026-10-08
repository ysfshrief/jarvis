using System.Text;
using System.Text.Json;
using Jarvis.Core.AI;
using Jarvis.Core.Tools;

namespace Jarvis.Core.Agent;

/// <summary>
/// Keeps a request inside the model's context window. Local models usually run with 4-8K tokens, so a
/// long conversation must shrink: old tool outputs are shortened first, then the oldest turns are
/// replaced by a one-line-per-turn recap. Nothing here asks a model to summarise (that would cost a
/// second slow call); the recap is extractive and honest about being condensed.
/// </summary>
public static class ContextBudget
{
    public const int OldToolResultChars = 400;
    public const int RecapChars = 1200;

    /// <summary>Rough token estimate: ~4 characters per token for Latin text, ~2 for Arabic.</summary>
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int ascii = 0, other = 0;
        foreach (var ch in text)
        {
            if (ch < 128) ascii++;
            else other++;
        }
        return ascii / 4 + other / 2 + 1;
    }

    public static int Estimate(ChatMessage m) =>
        4 + Estimate(m.Content) + (m.ToolCalls?.Sum(c => Estimate(c.Name) + Estimate(c.ArgumentsJson)) ?? 0);

    public static int Estimate(IReadOnlyList<ToolDefinition> tools) =>
        tools.Sum(t => Estimate(t.Name) + Estimate(t.Description) + Estimate(JsonSerializer.Serialize(t.ParametersSchema())) + 8);

    public sealed record Fitted(List<ChatMessage> Messages, int EstimatedTokens, int DroppedMessages, bool Trimmed);

    /// <summary>
    /// Returns system + (recap) + as much recent history as fits in <paramref name="budgetTokens"/>
    /// after reserving room for the tools and the reply. The newest user message is always kept.
    /// </summary>
    public static Fitted Fit(string system, IReadOnlyList<ChatMessage> history, IReadOnlyList<ToolDefinition> tools,
        int budgetTokens, int reserveForReply)
    {
        var available = Math.Max(256, budgetTokens - reserveForReply - Estimate(tools) - Estimate(system) - 8);
        var msgs = history.ToList();

        // The current exchange starts at the last user message; everything before it is "old".
        var currentStart = msgs.FindLastIndex(m => m.Role == ChatRole.User);
        if (currentStart < 0) currentStart = 0;

        var trimmed = false;
        if (Total(msgs) > available)
        {
            for (var i = 0; i < currentStart; i++)
            {
                if (msgs[i].Role == ChatRole.Tool && (msgs[i].Content?.Length ?? 0) > OldToolResultChars)
                {
                    msgs[i] = msgs[i] with { Content = msgs[i].Content![..OldToolResultChars] + "…(shortened)" };
                    trimmed = true;
                }
            }
        }

        var dropped = new List<ChatMessage>();
        // The recap of dropped turns takes space too, so it counts against the budget while dropping.
        while (Total(msgs) + Estimate(Recap(dropped)) > available && currentStart > 0)
        {
            // Drop the oldest whole turn: a user message and everything until the next user message.
            var next = msgs.FindIndex(1, m => m.Role == ChatRole.User);
            if (next <= 0 || next > currentStart) next = currentStart;
            dropped.AddRange(msgs.Take(next));
            msgs.RemoveRange(0, next);
            currentStart -= next;
            trimmed = true;
        }

        // A single huge exchange (e.g. a long file read): shorten its tool results, longest first.
        while (Total(msgs) + Estimate(Recap(dropped)) > available)
        {
            var longest = msgs.Select((m, i) => (m, i)).Where(x => x.m.Role == ChatRole.Tool && (x.m.Content?.Length ?? 0) > 600)
                .OrderByDescending(x => x.m.Content!.Length).FirstOrDefault();
            if (longest.m is null) break;
            var keep = Math.Max(600, longest.m.Content!.Length / 2);
            msgs[longest.i] = longest.m with { Content = longest.m.Content[..keep] + "…(truncated to fit)" };
            trimmed = true;
        }

        var result = new List<ChatMessage> { ChatMessage.System(system) };
        var recap = Recap(dropped);
        if (recap is not null) result.Add(ChatMessage.System(recap));
        result.AddRange(msgs);
        return new Fitted(result, Total(result) + Estimate(tools), dropped.Count, trimmed);
    }

    /// <summary>One line per dropped turn, newest kept when space runs out.</summary>
    internal static string? Recap(IReadOnlyList<ChatMessage> dropped)
    {
        var lines = new List<string>();
        foreach (var m in dropped)
        {
            if (string.IsNullOrWhiteSpace(m.Content)) continue;
            if (m.Role == ChatRole.User) lines.Add("- User: " + OneLine(m.Content, 140));
            else if (m.Role == ChatRole.Assistant) lines.Add("  You: " + OneLine(m.Content, 140));
        }
        if (lines.Count == 0) return null;
        var sb = new StringBuilder();
        for (var i = lines.Count - 1; i >= 0 && sb.Length + lines[i].Length < RecapChars; i--)
            sb.Insert(0, lines[i] + "\n");
        return "Earlier in this conversation (condensed to save space; details may be missing):\n" + sb.ToString().TrimEnd();
    }

    private static int Total(IEnumerable<ChatMessage> msgs) => msgs.Sum(Estimate);

    private static string OneLine(string s, int max)
    {
        var t = s.ReplaceLineEndings(" ").Trim();
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }
}
