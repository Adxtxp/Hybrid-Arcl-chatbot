namespace Arcl.Api.Models;

public enum League { Mens, Womens, Youth }

public static class LeagueExtensions
{
    public static string ToDbValue(this League league) => league switch
    {
        League.Mens => "mens",
        League.Womens => "womens",
        League.Youth => "youth",
        _ => throw new ArgumentOutOfRangeException(nameof(league))
    };

    public static League FromDbValue(string value) => value switch
    {
        "mens" => League.Mens,
        "womens" => League.Womens,
        "youth" => League.Youth,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown league value")
    };

    // Matches frontend/app.js's leagueLabel() — kept in sync by hand since the two aren't shared
    // code; used in user-facing message text (e.g. the FAQ route's out-of-scope response).
    public static string ToDisplayName(this League league) => league switch
    {
        League.Mens => "Men's",
        League.Womens => "Women's",
        League.Youth => "Youth",
        _ => throw new ArgumentOutOfRangeException(nameof(league))
    };
}

public enum RoutePath { Faq, DecisionTree, HumanHandoff }

public static class RoutePathExtensions
{
    public static string ToDbValue(this RoutePath route) => route switch
    {
        RoutePath.Faq => "faq",
        RoutePath.DecisionTree => "decision_tree",
        RoutePath.HumanHandoff => "human_handoff",
        _ => throw new ArgumentOutOfRangeException(nameof(route))
    };
}

// ---------------------------------------------------------------------- requests

public sealed record CreateSessionRequest(League? League);

public sealed record SetLeagueRequest(League League);

/// <summary>Regenerate = the visitor asked for a fresh answer to the same question (the ↻ button):
/// the previous attempt is left out of conversation memory and the model runs warmer.</summary>
public sealed record ChatRequest(Guid SessionId, string Message, bool Regenerate = false);

/// <summary>The contact-admin form. Email + ConsentGiven are required together — a submission
/// without an email (the pre-form "Contact Human Support" behavior) is still accepted and just
/// logged, so older embedded copies of the widget keep working.</summary>
public sealed record HumanHandoffRequest(Guid SessionId, string Message, string? Name = null, string? Email = null, bool ConsentGiven = false);

public sealed record DecisionAnswerRequest(Guid SessionId, string SelectedOptionLabel);

public sealed record FeedbackRequest(long LogId, bool ThumbsUp);

public sealed record MarkReviewedRequest(string? Note);

// ---------------------------------------------------------------------- responses

public sealed record SessionResponse(Guid SessionId, League? League);

public sealed record Citation(string SectionNumber, string SectionTitle, string SourceUrl);

public sealed record DecisionOption(string Label);

/// <summary>
/// What every chat turn returns, whatever route it took. Exactly one of
/// (Citations, DecisionOptions) is meaningfully populated depending on RoutePath;
/// LogId lets the frontend submit feedback for this specific turn.
///
/// Kind is what the frontend styles by — each one is a visually distinct state:
///   "answer"         a grounded answer (Confidence says "high" or "medium"; medium gets a caveat)
///   "low_confidence" on-topic, but nothing close enough to answer from — RelatedLinks + contact admin
///   "out_of_scope"   not an ARCL question at all
///   "error"          something failed on our side (Gemini/DB) — retry, not a content problem
///   "handoff"        routed straight to a human (ambiguous/complaint, or no decision tree built)
///   "decision"       a decision-tree step
///   "team_ranking"   the live team-ranking lookup
/// Message text is mostly inline-cited ("... [§4]") for answers — see GeminiClient's prompt.
/// </summary>
public sealed record ChatResponse(
    long LogId,
    string RoutePath,          // "faq" | "decision_tree" | "human_handoff" | "team_ranking"
    string Message,
    IReadOnlyList<Citation> Citations,
    IReadOnlyList<DecisionOption> DecisionOptions,
    bool IsTerminal,           // true when a decision tree has reached an answer (no further options)
    string Kind = "answer",
    string? Confidence = null, // "high" | "medium" | "low" | "out_of_scope" — FAQ route only
    IReadOnlyList<Citation>? RelatedLinks = null,
    IReadOnlyList<string>? FollowUps = null);

/// <summary>Non-secret settings the chat page needs to render (the contact form's consent wording
/// in particular — served from config so the text shown is exactly the text stored with each
/// submission).</summary>
public sealed record PublicConfigResponse(string SupportContact, string HandoffConsentText);

public sealed record SuggestionsResponse(IReadOnlyList<string> Questions);

// ---------------------------------------------------------------------- admin (Phase 5)

/// <summary>Wraps sql/004_dashboards.sql query #6 (weekly totals, extended with
/// sql/007_token_tracking.sql's prompt/completion token sums) and #1 (query volume by league) —
/// the numbers for GET /api/admin/summary's cards/tables. DailyActivity is a richer version of
/// query #4 (adds tokens and thumbs up/down per day, not just cost) written directly in
/// AdminService.cs since no single numbered dashboard query combines all of that.</summary>
public sealed record AdminSummary(
    int TotalQueries, int FaqQueries, int DecisionTreeQueries, int HumanHandoffQueries,
    int ThumbsUpCount, int ThumbsDownCount, decimal? TotalCostUsd, double? AvgLatencyMs,
    long? TotalPromptTokens, long? TotalCompletionTokens,
    IReadOnlyList<DailyActivity> DailyActivity,
    IReadOnlyList<LeagueQueryVolume> QueryVolumeByLeague);

public sealed record DailyActivity(
    DateOnly Date, int QueryCount, decimal? TotalCostUsd, decimal? AvgCostPerQueryUsd,
    long? PromptTokens, long? CompletionTokens, int ThumbsUpCount, int ThumbsDownCount);

public sealed record LeagueQueryVolume(string? League, DateOnly Date, int QueryCount);

/// <summary>One row of sql/004_dashboards.sql query #2 (every human_handoff + every thumbs-down),
/// plus the reviewed_at/admin_note tracking added by sql/005_admin_review_tracking.sql.</summary>
public sealed record ReviewQueueItem(
    long LogId, DateTime CreatedAt, string? League, string Query, string? ResponseText,
    string RoutePath, int? UserFeedback, DateTime? ReviewedAt, string? AdminNote,
    string? ContactName = null, string? ContactEmail = null); // from handoff_requests, when the visitor filled in the contact form

/// <summary>sql/004_dashboards.sql query #3 — sessions that entered a decision tree and have sat
/// idle mid-tree for over an hour. Each one is a real user who didn't get an answer.</summary>
public sealed record DecisionTreeDropoffItem(Guid SessionId, string? League, string StuckAtQuestion, DateTime UpdatedAt);

/// <summary>sql/004_dashboards.sql query #5 — the rule sections retrieval actually pulls most
/// often (last 30 days), i.e. what people are really asking about.</summary>
public sealed record TopChunkItem(string League, string SectionNumber, string SectionTitle, int TimesRetrieved);

/// <summary>The same question text asked more than once and never answered (route_path =
/// human_handoff every time) — not in any of the 6 original dashboard queries, since none of
/// them group by question. The literal "what does the bot keep failing on" list.</summary>
public sealed record TopUnansweredItem(string Query, int TimesAsked, DateTime LastAsked);
