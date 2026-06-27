using Priest.Providers;
using Priest.Sessions;

namespace Priest.Engine;

/// <summary>A planned compaction round.</summary>
public sealed record CompactionPlan(IReadOnlyList<Turn> ToSummarize, int SummarizedThrough);

/// <summary>
/// Conversation compaction primitives (spec 2.5.0).
///
/// Long sessions replay their full turn history on every call, so input cost
/// grows linearly per turn and quadratically over a session. Compaction folds
/// the older turns into a running summary and replays only a recent tail. It is
/// non-destructive: raw turns stay in the store; only the replayed view shrinks.
/// The summary lives in session metadata (see <see cref="Session"/>).
/// </summary>
public static class Compactor
{
    /// <summary>Compact when the previous turn's input usage exceeds this fraction of the budget.</summary>
    public const double CompactionTriggerRatio = 0.8;
    /// <summary>Most-recent turns kept verbatim; older turns fold into the summary.</summary>
    public const int DefaultCompactionKeepTurns = 6;
    /// <summary>Output cap for the summary-generation call (keeps the summary itself bounded).</summary>
    public const int SummaryMaxOutputTokens = 1024;

    /// <summary>Whether the previous turn's input size warrants compaction. Off when no budget is set.</summary>
    public static bool ShouldCompact(int? lastInputTokens, int? maxContextTokens)
    {
        if (maxContextTokens is not int budget || budget <= 0) return false;
        if (lastInputTokens is not int last) return false;
        return last > budget * CompactionTriggerRatio;
    }

    /// <summary>
    /// Plan a compaction round: fold every turn after what's already summarized
    /// and before the kept tail. Returns null when there is nothing new to fold.
    /// </summary>
    public static CompactionPlan? PlanCompaction(IReadOnlyList<Turn> turns, int alreadySummarizedThrough, int keepTurns)
    {
        var tailStart = Math.Max(0, turns.Count - Math.Max(0, keepTurns));
        if (tailStart <= alreadySummarizedThrough) return null;
        var toSummarize = new List<Turn>();
        for (var i = alreadySummarizedThrough; i < tailStart; i++) toSummarize.Add(turns[i]);
        return new CompactionPlan(toSummarize, tailStart);
    }

    private const string SummarySystem =
        "You compress prior conversation into a compact running summary so the assistant can continue without the full transcript. " +
        "Preserve the user's goals and constraints, decisions made, facts established within the conversation, and open or unresolved threads. " +
        "Durable user facts are stored separately as memory — do not re-list them. Capture the conversation's trajectory and the context needed to continue it. " +
        "Write a tight synopsis, not a turn-by-turn log. When an earlier summary is provided, merge the new turns into it and return a single updated summary with no preamble.";

    /// <summary>Build the messages for the summary-generation call (existing summary + new turns → one updated summary).</summary>
    public static IList<ChatMessage> BuildSummaryMessages(string? existingSummary, IReadOnlyList<Turn> toSummarize)
    {
        var transcript = string.Join("\n\n",
            toSummarize.Select(t => $"{(t.Role == TurnRole.User ? "USER" : "ASSISTANT")}: {t.Content}"));
        var user = !string.IsNullOrWhiteSpace(existingSummary)
            ? $"Existing summary so far:\n\n{existingSummary.Trim()}\n\n---\n\nNew conversation turns to fold in:\n\n{transcript}\n\n---\n\nReturn one updated summary."
            : $"Conversation turns to summarize:\n\n{transcript}\n\n---\n\nReturn the summary.";
        return new List<ChatMessage>
        {
            new ChatMessage("system", SummarySystem),
            new ChatMessage("user", user),
        };
    }
}
