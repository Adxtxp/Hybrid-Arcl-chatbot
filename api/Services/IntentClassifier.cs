using System.Text.RegularExpressions;

namespace Arcl.Api.Services;

public enum IntentRoute { Faq, DecisionTree, Ambiguous, TeamRanking }

/// <param name="RootAnswer">
/// The root node's option the message already answers, if any — e.g. "Yes" for "Was the match
/// stopped early due to weather?" when the visitor just said "our match was stopped by rain".
/// DecisionTreeService skips straight past the root so the bot doesn't ask what it was just told.
/// </param>
public sealed record IntentResult(IntentRoute Route, string? DecisionTreeTopic, string? RootAnswer = null);

/// <summary>
/// Deterministic-first classifier: a free keyword/regex pass catches the known decision-tree
/// topics before any LLM call happens. This is the whole point of the cost-saving hybrid design
/// from the blueprint — classifying every message with an LLM first would burn the budget the
/// deterministic routing exists to save.
///
/// Add a topic here + a matching root node (topic column on decision_tree_nodes, see
/// sql/001_schema.sql) whenever a new rule area needs strict step-by-step navigation instead of
/// free-text FAQ answering. Only "bad_weather" ships with a seeded decision tree right now (see
/// sql/003_seed_decision_trees.sql) — the others below are wired into the classifier but will
/// fall through to human handoff until someone builds their trees too.
/// </summary>
public sealed class IntentClassifier
{
    // Order matters: first matching topic wins. Keep patterns narrow — a false positive here
    // routes a normal FAQ question into a rigid decision tree instead of just answering it.
    private static readonly (string Topic, Func<string, bool> Matches)[] DecisionTreeTopics =
    {
        ("bad_weather", IsBadWeatherQuestion),
        ("no_show", new Regex(@"\b(no[\s-]?show|forfeit(ed)?|didn'?t (show|turn) up|team (never|didn'?t) (show|arrive))\b", RegexOptions.IgnoreCase).IsMatch),
        ("tie_breaking", new Regex(@"\b(tie[\s-]?break(er|ing)?|equal points|same points|playoff tie)\b", RegexOptions.IgnoreCase).IsMatch),
    };

    // Phrases that are about a disrupted match on their own ("rained out", "abandoned").
    private static readonly Regex BadWeatherStrong = new(
        @"\b(rain(ed)?[\s-]?out|wash(ed)?[\s-]?out|abandon(ed|ment)?|stopped? (the )?(game|match|play))\b", RegexOptions.IgnoreCase);

    // A bare weather word only counts alongside match context — "what will the weather be in
    // Seattle tomorrow?" (a real false positive from the 2026-11-16 calibration run) is a
    // forecast question, not a rain-stopped-play dispute.
    private static readonly Regex WeatherWord = new(@"\b(rain\w*|weather\w*|drizzl\w*|storm\w*|thunder\w*|lightning)\b", RegexOptions.IgnoreCase);
    private static readonly Regex MatchContext = new(
        @"\b(match(es)?|game|games|play(ed|ing)?|innings?|overs?|umpires?|result|points|called off|stop(ped)?|finish(ed)?|complete[d]?|abandon\w*|resched\w*)\b",
        RegexOptions.IgnoreCase);

    private static bool IsBadWeatherQuestion(string message) =>
        BadWeatherStrong.IsMatch(message) || (WeatherWord.IsMatch(message) && MatchContext.IsMatch(message));

    // The weather tree's root asks "Was the match stopped early due to weather?". A message that
    // names both the weather and the stoppage has already answered that. "Abandoned" alone
    // doesn't count (could be a no-show), nor does "what if it rains?" (nothing stopped yet).
    private static readonly Regex WeatherOut = new(@"\b(rain(ed)?[\s-]?out|wash(ed)?[\s-]?out)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Stoppage = new(
        @"\b(stop(ped|page|s)?|abandon\w*|called off|halt(ed)?|interrupt(ed|ion)?|cut short|suspend(ed)?|(could ?n'?o?t|did ?n'?o?t) finish)\b",
        RegexOptions.IgnoreCase);

    internal static bool StatesWeatherStoppage(string message) =>
        WeatherOut.IsMatch(message) || (WeatherWord.IsMatch(message) && Stoppage.IsMatch(message));

    // Signals a message might need a human regardless of topic — surfaced as Ambiguous so the
    // caller can offer the escape hatch rather than force a route.
    private static readonly Regex AmbiguousSignal = new(
        @"\b(dispute|complain(t)?|unfair|cheat(ed|ing)?|argu(e|ment)|appeal)\b", RegexOptions.IgnoreCase);

    // Team names on arcl.org are unconstrained free text (the site's own team directory includes
    // names like "1", "8", and "A Few Good Men" — see TeamRankingService.cs), so there's no
    // reliable way to pull a team name out of an arbitrary sentence with a regex. This only
    // detects that a ranking/standing question was asked at all; Program.cs always follows up by
    // asking which team, then treats the next message, verbatim, as the name.
    private static readonly Regex TeamRankingSignal = new(
        @"\b(rankings?|standings?|where (do|does) (we|our team|my team) stand|how (are|is) (we|our team|my team) doing)\b",
        RegexOptions.IgnoreCase);

    public IntentResult Classify(string message)
    {
        if (TeamRankingSignal.IsMatch(message))
            return new IntentResult(IntentRoute.TeamRanking, null);

        foreach (var (topic, matches) in DecisionTreeTopics)
        {
            if (matches(message))
                return new IntentResult(IntentRoute.DecisionTree, topic,
                    topic == "bad_weather" && StatesWeatherStoppage(message) ? "Yes" : null);
        }

        if (AmbiguousSignal.IsMatch(message) && message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 6)
        {
            // Short + emotionally charged ("this is unfair!") — too little to retrieve against
            // confidently. Longer messages with the same words usually contain enough detail for
            // FAQ retrieval to work, so only the short case gets treated as ambiguous.
            return new IntentResult(IntentRoute.Ambiguous, null);
        }

        return new IntentResult(IntentRoute.Faq, null);
    }
}
