namespace Arcl.Api.Services;

/// <summary>
/// Relevance-based memory carry-forward: of the session's recent answered turns, keep only the
/// ones that actually relate to the new message, instead of always replaying the last N.
///
/// Why: a visitor who asks about no-balls, then wides, then how to upload a scorecard doesn't
/// need two cricket-rule exchanges in the prompt for the scorecard question — it costs tokens and
/// gives the model unrelated text to wander into. But "what about a wide?" right after a no-ball
/// question needs that previous turn no matter how little the words overlap.
///
/// Rules: a likely follow-up (ChatOrchestrator.IsLikelyFollowUp) always keeps the most recent
/// turn. Any other turn is kept if enough of the new message's terms (same tokenization as BM25)
/// appear in that turn's question or answer. Kept turns stay in chronological order; at most
/// maxTurns, preferring the most recent.
/// </summary>
public static class MemorySelector
{
    // Share of the new message's terms that must appear in an earlier turn for it to count as
    // related. Low on purpose: questions are short, so two shared terms out of five is a real link.
    internal const double MinOverlap = 0.25;

    public static IReadOnlyList<ConversationTurn> Select(IReadOnlyList<ConversationTurn> recentTurns, string message, int maxTurns)
    {
        if (recentTurns.Count == 0 || maxTurns <= 0) return Array.Empty<ConversationTurn>();

        var messageTerms = Bm25Index.Tokenize(message).ToHashSet();
        var followUp = ChatOrchestrator.IsLikelyFollowUp(message);

        var keep = new List<ConversationTurn>();
        for (var i = 0; i < recentTurns.Count; i++)
        {
            var turn = recentTurns[i];
            var isLast = i == recentTurns.Count - 1;
            if ((isLast && followUp) || Overlap(messageTerms, turn) >= MinOverlap)
                keep.Add(turn);
        }

        return keep.Count <= maxTurns ? keep : keep.Skip(keep.Count - maxTurns).ToList();
    }

    internal static double Overlap(HashSet<string> messageTerms, ConversationTurn turn)
    {
        if (messageTerms.Count == 0) return 0;
        var turnTerms = Bm25Index.Tokenize(turn.Question + " " + turn.Answer).ToHashSet();
        return messageTerms.Count(turnTerms.Contains) / (double)messageTerms.Count;
    }
}
