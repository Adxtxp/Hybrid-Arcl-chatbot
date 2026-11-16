using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Arcl.Api.Models;

namespace Arcl.Api.Services;

/// <summary>
/// One event of a chat turn. The SSE endpoint (POST /api/chat/stream) writes each one as it
/// happens; the plain JSON endpoint (POST /api/chat — used by eval/) just waits for "done".
///   "sources"  Citation[] — every section given to the model, sent before the answer starts, so
///              the frontend can turn inline [§4] markers into links while text is still arriving
///   "delta"    { text } — the next piece of answer text (already output-filtered)
///   "replace"  { text } — the final filtered answer differs from what was streamed (rare: a phone
///              number only the full-text filter could catch) — replace the bubble's text
///   "done"     ChatResponse — always last, always exactly once
/// </summary>
public sealed record ChatEvent(string Type, object Data)
{
    public static ChatEvent Done(ChatResponse response) => new("done", response);
}

/// <summary>
/// Runs one chat turn end to end: pending "which team?" follow-up, intent routing, and the FAQ
/// path — conversational memory, hybrid retrieval, the graduated confidence gate, the streamed
/// Gemini answer, output filtering, citation extraction, follow-up suggestions, and logging.
///
/// THE CONFIDENCE GATE (Arcl:*CosineDistanceThreshold, on the closest chunk's cosine distance):
///   distance ≤ High                → "high":   answer normally
///   High   < distance ≤ Medium     → "medium": answer, with a "double-check this" caveat in the UI
///   Medium < distance ≤ OutOfScope → "low":    no answer — related sections + contact-admin offer
///   distance > OutOfScope          → "out_of_scope": not an ARCL question
/// The model can also downgrade high/medium to low by answering "CONFIDENT: no". Low and
/// out-of-scope never call Gemini at all — no point paying for an answer that won't be shown.
/// Every turn logs best_distance + tier, which is what the thresholds should be tuned from.
///
/// Calibrated 2026-11-16 (gemini-embedding-2, 768 dims) from the eval set + 20 probe questions:
/// answerable questions scored 0.11–0.32; uncovered-but-on-topic ones 0.24–0.36 (the model
/// correctly answered "CONFIDENT: no" on every one); clearly off-topic ones 0.42–0.47. Hence
/// 0.33 / 0.38 / 0.40. The off-topic margin is thin (0.02) — re-check as real traffic arrives.
/// </summary>
public sealed class ChatOrchestrator
{
    // "[§4]" as the prompt asks — but models regularly drop the "§" and write "[4]" or "[4, 39]", so
    // both are accepted. A bare bracket only counts when its content is a section id that was
    // actually retrieved (see NormalizeCitations), so ordinary bracketed text is left alone.
    private static readonly Regex CitationMarker = new(@"\[(§)?\s*([^\[\]\n]{1,80})\]", RegexOptions.Compiled);

    // Openers and pronouns that only make sense relative to the previous question.
    private static readonly Regex FollowUpOpener = new(
        @"^\s*(and|but|also|so|then|ok(ay)?|what about|how about|what if|same|is that|does that|can they|can it)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FollowUpPronoun = new(
        @"\b(it|that|this|they|them|those|these|there|he|she|his|her)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly SessionService _sessions;
    private readonly IntentClassifier _classifier;
    private readonly RetrievalService _retrieval;
    private readonly DecisionTreeService _decisionTrees;
    private readonly GeminiClient _gemini;
    private readonly TeamRankingService _teamRanking;
    private readonly ConversationLogger _log;
    private readonly OutputFilter _outputFilter;
    private readonly BudgetGovernor _budget;
    private readonly ILogger<ChatOrchestrator> _logger;

    private readonly string _supportContact;
    private readonly double _highThreshold;
    private readonly double _mediumThreshold;
    private readonly double _outOfScopeThreshold;
    private readonly int _memoryTurns;

    public ChatOrchestrator(
        SessionService sessions, IntentClassifier classifier, RetrievalService retrieval, DecisionTreeService decisionTrees,
        GeminiClient gemini, TeamRankingService teamRanking, ConversationLogger log, OutputFilter outputFilter,
        BudgetGovernor budget, IConfiguration config, ILogger<ChatOrchestrator> logger)
    {
        _sessions = sessions;
        _classifier = classifier;
        _retrieval = retrieval;
        _decisionTrees = decisionTrees;
        _gemini = gemini;
        _teamRanking = teamRanking;
        _log = log;
        _outputFilter = outputFilter;
        _budget = budget;
        _logger = logger;

        _supportContact = config.GetNonEmpty("Arcl:SupportContact")
            ?? throw new InvalidOperationException("Set Arcl:SupportContact in appsettings.json.");
        // HighConfidence... was called LowConfidenceCosineDistanceThreshold before the gate had
        // three tiers — still read under the old name so an existing override keeps working.
        _highThreshold = config.GetValue<double?>("Arcl:HighConfidenceCosineDistanceThreshold")
            ?? config.GetValue<double?>("Arcl:LowConfidenceCosineDistanceThreshold") ?? 0.33;
        _mediumThreshold = config.GetValue<double?>("Arcl:MediumConfidenceCosineDistanceThreshold") ?? 0.38;
        _outOfScopeThreshold = config.GetValue<double?>("Arcl:OutOfScopeCosineDistanceThreshold") ?? 0.40;
        _memoryTurns = config.GetValue<int?>("Arcl:ConversationMemoryTurns") ?? 4;
    }

    public async IAsyncEnumerable<ChatEvent> RunAsync(ChatRequest req, League league, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        // A prior turn asked "which team?" — this message IS the team name, verbatim, regardless of
        // what it looks like. Cleared immediately so a lookup failure can't leave the session stuck
        // waiting forever. See sql/006_team_ranking.sql for why this can't be parsed out of the
        // original question instead.
        if (await _sessions.GetPendingTeamRankingAsync(req.SessionId, ct))
        {
            await _sessions.SetPendingTeamRankingAsync(req.SessionId, false, ct);
            yield return ChatEvent.Done(await AnswerTeamRankingAsync(req, stopwatch, ct));
            yield break;
        }

        var intent = _classifier.Classify(req.Message);
        switch (intent.Route)
        {
            case IntentRoute.TeamRanking:
            {
                await _sessions.SetPendingTeamRankingAsync(req.SessionId, true, ct);
                const string askMessage = "Which team would you like the ranking for? Please reply with just " +
                    "the team name, exactly as it's registered on arcl.org.";
                var logId = await _log.LogAsync(req.SessionId, req.Message, RoutePath.Faq, askMessage,
                    (int)stopwatch.ElapsedMilliseconds, tokenCostUsd: 0m, retrievedChunkIds: Array.Empty<Guid>(), ct: ct);
                yield return ChatEvent.Done(new ChatResponse(logId, "team_ranking", askMessage, Array.Empty<Citation>(),
                    Array.Empty<DecisionOption>(), false, Kind: "team_ranking"));
                yield break;
            }

            case IntentRoute.DecisionTree:
            {
                var step = await _decisionTrees.StartAsync(req.SessionId, league, intent.DecisionTreeTopic!, intent.RootAnswer, ct);
                if (step is null)
                {
                    // Classifier recognized the topic but nobody's built that tree yet — hand off
                    // rather than silently doing nothing.
                    yield return ChatEvent.Done(await HandoffAsync(req, stopwatch, ct));
                    yield break;
                }
                var logId = await _log.LogAsync(req.SessionId, req.Message, RoutePath.DecisionTree, step.Message,
                    (int)stopwatch.ElapsedMilliseconds, tokenCostUsd: 0m, retrievedChunkIds: Array.Empty<Guid>(), ct: ct);
                yield return ChatEvent.Done(new ChatResponse(logId, "decision_tree", step.Message, Array.Empty<Citation>(),
                    step.Options.Select(o => new DecisionOption(o.Label)).ToList(), step.IsTerminal, Kind: "decision"));
                yield break;
            }

            case IntentRoute.Ambiguous:
                yield return ChatEvent.Done(await HandoffAsync(req, stopwatch, ct));
                yield break;

            default:
                await foreach (var ev in AnswerFaqAsync(req, league, stopwatch, ct))
                    yield return ev;
                yield break;
        }
    }

    // ------------------------------------------------------------------ FAQ

    private async IAsyncEnumerable<ChatEvent> AnswerFaqAsync(ChatRequest req, League league, Stopwatch stopwatch, [EnumeratorCancellation] CancellationToken ct)
    {
        // ---- memory + retrieval (no yields in here, so failures can be caught)
        IReadOnlyList<ConversationTurn> history = Array.Empty<ConversationTurn>();
        RetrievalResult? retrieval = null;
        Exception? failure = null;
        try
        {
            // Look further back than we'll keep, then keep only what relates to this message
            // (MemorySelector) — a fresh topic doesn't drag unrelated exchanges into the prompt.
            history = await _log.GetRecentTurnsAsync(req.SessionId, _memoryTurns * 3, ct);
            if (req.Regenerate && history.Count > 0 && history[^1].Question.Trim() == req.Message.Trim())
                history = history.Take(history.Count - 1).ToList(); // don't anchor the retry on the answer being replaced
            history = MemorySelector.Select(history, req.Message, _memoryTurns);

            var retrievalQuery = BuildRetrievalQuery(req.Message, history);
            retrieval = await _retrieval.HybridSearchAsync(retrievalQuery, league, DateOnly.FromDateTime(DateTime.UtcNow), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = ex;
        }

        if (failure is not null || retrieval is null)
        {
            yield return ChatEvent.Done(await ErrorAsync(req, stopwatch, failure, ct));
            yield break;
        }

        var chunks = retrieval.Chunks;
        var bestDistance = chunks.Count == 0 ? (double?)null : retrieval.BestDistance;

        // ---- gate, before paying for a Gemini call
        if (chunks.Count == 0)
        {
            yield return ChatEvent.Done(await LowConfidenceAsync(req, league, chunks, bestDistance, stopwatch, null, ct));
            yield break;
        }
        if (retrieval.BestDistance > _outOfScopeThreshold)
        {
            yield return ChatEvent.Done(await OutOfScopeAsync(req, league, retrieval.BestDistance, stopwatch, ct));
            yield break;
        }
        if (retrieval.BestDistance > _mediumThreshold)
        {
            yield return ChatEvent.Done(await LowConfidenceAsync(req, league, chunks, bestDistance, stopwatch, null, ct));
            yield break;
        }
        var tier = retrieval.BestDistance <= _highThreshold ? "high" : "medium";

        // ---- cost governor: past the monthly cap, answer with the sections themselves
        var budgetExhausted = false;
        try
        {
            budgetExhausted = (await _budget.GetStateAsync(ct)).Exhausted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail open: a budget-check hiccup shouldn't take answers away from visitors.
            _logger.LogError(ex, "Budget check failed; answering normally.");
        }
        if (budgetExhausted)
        {
            yield return ChatEvent.Done(await SectionsOnlyAsync(req, chunks, retrieval.BestDistance, tier, stopwatch, ct));
            yield break;
        }

        yield return new ChatEvent("sources", chunks.Select(ToCitation).ToList());

        // ---- stream the answer
        var context = string.Join("\n", chunks.Select(c => c.Content));
        var streamFilter = _outputFilter.CreateStream(context);
        var raw = new StringBuilder();
        var sent = new StringBuilder();
        bool? confident = null;
        FaqUsage? usage = null;

        var stream = _gemini.AnswerFaqStreamAsync(req.Message, league.ToDisplayName(),
            chunks.Select(c => new RetrievedChunkForPrompt(c.SectionNumber, c.SectionTitle, c.Content)).ToList(),
            history, req.Regenerate, ct);
        var enumerator = stream.GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                FaqStreamPart part;
                try
                {
                    if (!await enumerator.MoveNextAsync()) break;
                    part = enumerator.Current;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failure = ex;
                    break;
                }

                switch (part)
                {
                    case FaqHeader h:
                        confident = h.Confident;
                        break;
                    case FaqDelta d:
                        raw.Append(d.Text);
                        var safe = streamFilter.Push(d.Text);
                        if (safe.Length > 0)
                        {
                            sent.Append(safe);
                            yield return new ChatEvent("delta", new { text = safe });
                        }
                        break;
                    case FaqUsage u:
                        usage = u;
                        break;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        if (failure is not null)
        {
            yield return ChatEvent.Done(await ErrorAsync(req, stopwatch, failure, ct));
            yield break;
        }

        var tail = streamFilter.Flush();
        if (tail.Length > 0)
        {
            sent.Append(tail);
            yield return new ChatEvent("delta", new { text = tail });
        }

        if (confident != true || raw.ToString().Trim().Length == 0)
        {
            yield return ChatEvent.Done(await LowConfidenceAsync(req, league, chunks, bestDistance, stopwatch, usage, ct));
            yield break;
        }

        // ---- final full-text filter
        var filtered = _outputFilter.Filter(raw.ToString().Trim(), context);
        if (filtered.PromptLeak)
        {
            _logger.LogWarning("Output filter caught a prompt leak for session {SessionId}; answer withheld.", req.SessionId);
            yield return ChatEvent.Done(await LowConfidenceAsync(req, league, chunks, bestDistance, stopwatch, usage, ct, outputFiltered: true));
            yield break;
        }
        // ---- citations: canonicalize markers to "[§id]", dropping ids that weren't retrieved
        var (answerText, citedNumbers) = NormalizeCitations(filtered.Text, chunks.Select(c => c.SectionNumber).ToList());
        if (answerText != sent.ToString().Trim())
            yield return new ChatEvent("replace", new { text = answerText });

        // ---- citations + follow-ups
        var cited = chunks.Where(c => citedNumbers.Contains(c.SectionNumber)).ToList();
        if (cited.Count == 0)
            cited = chunks.Take(1).ToList(); // model didn't cite cleanly — still show what was retrieved

        var followUps = chunks
            .Where(c => !cited.Contains(c))
            .Select(c => SuggestionService.ToSuggestion(c.SectionNumber, c.SectionTitle))
            .Where(s => s is not null)
            .Distinct()
            .Take(2)
            .Cast<string>()
            .ToList();

        var logId = await _log.LogAsync(
            req.SessionId, req.Message, RoutePath.Faq, answerText, (int)stopwatch.ElapsedMilliseconds,
            usage?.EstimatedCostUsd, cited.Select(c => c.ChunkId).ToList(),
            promptTokens: usage?.PromptTokens, completionTokens: usage?.CompletionTokens,
            bestDistance: bestDistance, confidenceTier: tier, outputFiltered: filtered.Changed, ct: ct);

        yield return ChatEvent.Done(new ChatResponse(
            logId, "faq", answerText, cited.Select(ToCitation).ToList(), Array.Empty<DecisionOption>(), IsTerminal: true,
            Kind: "answer", Confidence: tier, FollowUps: followUps));
    }

    /// <summary>
    /// For a follow-up ("what about for a wide?"), retrieval needs the topic it follows from — the
    /// bare follow-up has almost nothing to embed or keyword-match. Prepending the previous
    /// question is deliberately simple (no extra LLM call to rewrite it) and only kicks in when the
    /// message actually looks like a follow-up, so a fresh topic isn't dragged toward the last one.
    /// </summary>
    internal static string BuildRetrievalQuery(string message, IReadOnlyList<ConversationTurn> history) =>
        history.Count > 0 && IsLikelyFollowUp(message) ? $"{history[^1].Question} {message}" : message;

    internal static bool IsLikelyFollowUp(string message)
    {
        var words = message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (words == 0) return false;
        if (FollowUpOpener.IsMatch(message)) return true;
        if (words <= 4) return true; // "for youth?", "and a wide?", "how many?"
        return words <= 10 && FollowUpPronoun.IsMatch(message);
    }

    /// <summary>
    /// Rewrites every citation marker in the answer to the canonical "[§id]" form the frontend
    /// renders as a link, keeping only ids that were actually given to the model — an invented id
    /// ("[FAQ-Fees]") is dropped rather than shown as a citation to a section that doesn't exist.
    /// A bare bracket with no known id in it ("[optional]") isn't a citation and is left as is.
    /// Returns the rewritten text and the set of cited section numbers (canonical casing).
    /// </summary>
    internal static (string Text, HashSet<string> Cited) NormalizeCitations(string text, IReadOnlyCollection<string> knownSectionNumbers)
    {
        var known = knownSectionNumbers.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(s => s, s => s, StringComparer.OrdinalIgnoreCase);
        var cited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var rewritten = CitationMarker.Replace(text, m =>
        {
            var hadSectionSign = m.Groups[1].Success;
            var valid = m.Groups[2].Value
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => known.TryGetValue(NormalizeSectionRef(p), out var canonical) ? canonical : null)
                .Where(id => id is not null)
                .Distinct()
                .ToList();

            if (valid.Count == 0) return hadSectionSign ? "" : m.Value;
            foreach (var id in valid) cited.Add(id!);
            return string.Concat(valid.Select(id => $"[§{id}]"));
        });

        // Removing a bogus marker can leave "text  ." — tidy the space it left behind.
        rewritten = Regex.Replace(rewritten, @"[ \t]+([.,;:!?])", "$1");
        rewritten = Regex.Replace(rewritten, @"[ \t]{2,}", " ");
        return (rewritten, cited);
    }

    // Strips a "Section "/"Sec. "/"§" prefix the model sometimes adds inside a marker, and cuts at
    // a colon in case it echoed the whole "<number>: <title>" label — so "[§Section 4: Innings]"
    // still matches the bare "4" that rule_chunks.section_number stores. No real section_number
    // contains a colon.
    internal static string NormalizeSectionRef(string s)
    {
        var trimmed = Regex.Replace(s.Trim(), @"^(?:§|section|sec\.?)\s*", "", RegexOptions.IgnoreCase).Trim();
        var colonIdx = trimmed.IndexOf(':');
        return (colonIdx >= 0 ? trimmed[..colonIdx] : trimmed).Trim();
    }

    private static Citation ToCitation(RetrievedChunk c) => new(c.SectionNumber, c.SectionTitle, c.SourceUrl);

    // ------------------------------------------------------------------ non-answer outcomes

    private async Task<ChatResponse> LowConfidenceAsync(
        ChatRequest req, League league, IReadOnlyList<RetrievedChunk> chunks, double? bestDistance, Stopwatch stopwatch,
        FaqUsage? usage, CancellationToken ct, bool outputFiltered = false)
    {
        // Related links first, contact-admin second: often the visitor can find it themselves in
        // one of the sections retrieval already turned up.
        var related = chunks
            .GroupBy(c => c.SectionNumber).Select(g => g.First())
            .Take(3).Select(ToCitation).ToList();

        var message = related.Count > 0
            ? $"I couldn't find a confident answer to that in the {league.ToDisplayName()} rules. These sections look related — " +
              $"or use \"Contact Human Support\" and a league admin will help (you can also email {_supportContact})."
            : $"I don't have a confident answer for that from the rulebook. Please use \"Contact Human Support\" so a " +
              $"league admin can help, or email {_supportContact} directly.";

        var logId = await _log.LogAsync(req.SessionId, req.Message, RoutePath.HumanHandoff, message,
            (int)stopwatch.ElapsedMilliseconds, usage?.EstimatedCostUsd ?? 0m, Array.Empty<Guid>(),
            promptTokens: usage?.PromptTokens, completionTokens: usage?.CompletionTokens,
            bestDistance: bestDistance, confidenceTier: "low", outputFiltered: outputFiltered, ct: ct);

        return new ChatResponse(logId, "human_handoff", message, Array.Empty<Citation>(), Array.Empty<DecisionOption>(), true,
            Kind: "low_confidence", Confidence: "low", RelatedLinks: related);
    }

    // Distinguish "essentially unrelated to the corpus" from "on-topic but not confident enough" —
    // a flat "not confident" reads as if the question WAS about rules but retrieval came up short,
    // which is misleading for something like "what's the capital of France?".
    // Monthly Gemini budget reached (BudgetGovernor): no model call — show the best-matching
    // section's opening text plus links to the top sections. Retrieval itself still runs, so this
    // is still a targeted answer, just not a summarized one.
    private async Task<ChatResponse> SectionsOnlyAsync(
        ChatRequest req, IReadOnlyList<RetrievedChunk> chunks, double bestDistance, string tier, Stopwatch stopwatch, CancellationToken ct)
    {
        var top = chunks[0];
        var (excerpt, _) = PromptSanitizer.SanitizeContext(top.Content, maxChars: 500);
        var message =
            $"Written answers are paused until next month, so here's the rule text that best matches your question.\n\n" +
            $"From Section {top.SectionNumber} ({top.SectionTitle}):\n{excerpt}\n\n" +
            $"For anything this doesn't settle, use \"Contact Human Support\" or email {_supportContact}.";
        var related = chunks.GroupBy(c => c.SectionNumber).Select(g => g.First()).Take(3).Select(ToCitation).ToList();

        var logId = await _log.LogAsync(req.SessionId, req.Message, RoutePath.Faq, message,
            (int)stopwatch.ElapsedMilliseconds, tokenCostUsd: 0m, retrievedChunkIds: new[] { top.ChunkId },
            bestDistance: bestDistance, confidenceTier: tier, ct: ct);
        return new ChatResponse(logId, "faq", message, new[] { ToCitation(top) }, Array.Empty<DecisionOption>(), true,
            Kind: "sections_only", Confidence: tier, RelatedLinks: related);
    }

    private async Task<ChatResponse> OutOfScopeAsync(ChatRequest req, League league, double bestDistance, Stopwatch stopwatch, CancellationToken ct)
    {
        var message =
            $"That doesn't look like a question about ARCL rules. I can help with rules, scheduling, and results for the " +
            $"{league.ToDisplayName()} league. If this is rules-related, try rephrasing it, or reach a league admin directly at {_supportContact}.";
        var logId = await _log.LogAsync(req.SessionId, req.Message, RoutePath.HumanHandoff, message,
            (int)stopwatch.ElapsedMilliseconds, tokenCostUsd: 0m, retrievedChunkIds: Array.Empty<Guid>(),
            bestDistance: bestDistance, confidenceTier: "out_of_scope", ct: ct);
        return new ChatResponse(logId, "human_handoff", message, Array.Empty<Citation>(), Array.Empty<DecisionOption>(), true,
            Kind: "out_of_scope", Confidence: "out_of_scope");
    }

    private async Task<ChatResponse> HandoffAsync(ChatRequest req, Stopwatch stopwatch, CancellationToken ct)
    {
        var message = $"I don't have a confident answer for that from the rulebook. " +
                      $"Please use \"Contact Human Support\" so a league admin can help, or email {_supportContact} directly.";
        var logId = await _log.LogAsync(req.SessionId, req.Message, RoutePath.HumanHandoff, message,
            (int)stopwatch.ElapsedMilliseconds, tokenCostUsd: 0m, retrievedChunkIds: Array.Empty<Guid>(), ct: ct);
        return new ChatResponse(logId, "human_handoff", message, Array.Empty<Citation>(), Array.Empty<DecisionOption>(), true, Kind: "handoff");
    }

    // Our failure, not the visitor's question — a distinct "error" kind so the UI offers a retry
    // instead of implying the rulebook has no answer. Still logged (as human_handoff, so it shows in
    // the admin review queue), but if logging itself is what's down, the visitor still gets a reply.
    private async Task<ChatResponse> ErrorAsync(ChatRequest req, Stopwatch stopwatch, Exception? ex, CancellationToken ct)
    {
        _logger.LogError(ex, "Chat turn failed for session {SessionId}.", req.SessionId);
        const string message = "Sorry — something went wrong on our side while answering that. Please try again in a moment.";
        long logId = 0;
        try
        {
            logId = await _log.LogAsync(req.SessionId, req.Message, RoutePath.HumanHandoff, "[error] " + message,
                (int)stopwatch.ElapsedMilliseconds, tokenCostUsd: 0m, retrievedChunkIds: Array.Empty<Guid>(), ct: ct);
        }
        catch (Exception logEx) when (logEx is not OperationCanceledException)
        {
            _logger.LogError(logEx, "Could not log the failed turn either.");
        }
        return new ChatResponse(logId, "human_handoff", message, Array.Empty<Citation>(), Array.Empty<DecisionOption>(), true, Kind: "error");
    }

    private async Task<ChatResponse> AnswerTeamRankingAsync(ChatRequest req, Stopwatch stopwatch, CancellationToken ct)
    {
        var teamName = req.Message.Trim();
        var ranking = await _teamRanking.LookupAsync(teamName, ct);

        var rankingMessage = ranking is null
            ? $"I couldn't find a team named \"{teamName}\" on arcl.org. Double-check the exact " +
              $"spelling (it needs to match how the team is registered there), then ask about team " +
              $"ranking again to retry."
            : ranking.Rank is null
                ? $"{ranking.TeamName} is registered in {ranking.Division} for {ranking.Season}, but " +
                  $"no rank has been posted yet."
                : $"{ranking.TeamName} is currently ranked #{ranking.Rank} in {ranking.Division} for " +
                  $"{ranking.Season}" + (ranking.RankDescription is null ? "." : $" ({ranking.RankDescription}).");

        var logId = await _log.LogAsync(req.SessionId, req.Message, RoutePath.Faq, rankingMessage,
            (int)stopwatch.ElapsedMilliseconds, tokenCostUsd: 0m, retrievedChunkIds: Array.Empty<Guid>(), ct: ct);
        return new ChatResponse(logId, "team_ranking", rankingMessage, Array.Empty<Citation>(), Array.Empty<DecisionOption>(), true,
            Kind: "team_ranking");
    }
}
