using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Arcl.Api.Models;

namespace Arcl.Api.Services;

/// <summary>
/// Starter prompts for the chat's first screen, built per league rather than one static list:
///   1. a seasonal lead question (rain during the season, registration in the off-season),
///   2. the sections this league's visitors have actually been asking about in the last 30 days
///      (conversation_log_retrieved_chunks — same data as the admin "top chunks" table),
///   3. a hand-picked set per league, verified against that league's real rulebook sections,
///   4. the live team-ranking lookup.
/// Deliberately never shows other visitors' raw question text — those can contain names or team
/// details — only section titles phrased as questions.
///
/// ToSuggestion() is also used by ChatOrchestrator for the "related" follow-ups after an answer.
/// </summary>
public sealed class SuggestionService
{
    private const int MaxSuggestions = 5;

    private static readonly IReadOnlyDictionary<League, string[]> Curated = new Dictionary<League, string[]>
    {
        [League.Mens] = new[]
        {
            "What are the rules for a no-ball?",
            "Is LBW a valid dismissal in ARCL?",
            "How many overs can one bowler bowl?",
            "How do I upload my team's scorecard?",
        },
        [League.Womens] = new[]
        {
            "How many players does each team play with?",
            "How are match points calculated?",
            "What are the re-scheduling options?",
            "How do I add players to my team?",
        },
        [League.Youth] = new[]
        {
            "Which age division does my team play in?",
            "How many overs can a bowler bowl?",
            "What equipment is mandatory for youth players?",
            "How do I add players to my team?",
        },
    };

    private const string TeamRankingQuestion = "What's my team's current ranking?";

    private readonly string _connectionString;
    private readonly ILogger<SuggestionService> _logger;

    public SuggestionService(IConfiguration config, ILogger<SuggestionService> logger)
    {
        _connectionString = config.GetNonEmpty("Arcl:ConnectionString")
            ?? Environment.GetEnvironmentVariable("ARCL_DB_CONNECTION")
            ?? throw new InvalidOperationException("Set Arcl:ConnectionString (appsettings) or ARCL_DB_CONNECTION (env var).");
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> GetStartersAsync(League league, DateOnly today, CancellationToken ct = default)
    {
        var result = new List<string> { SeasonalQuestion(today) };

        try
        {
            foreach (var (number, title) in await GetPopularSectionsAsync(league, ct))
                if (ToSuggestion(number, title) is { } s) result.Add(s);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Popular sections are a nice-to-have; the curated list alone is a fine fallback.
            _logger.LogWarning(ex, "Couldn't load popular sections for {League} suggestions.", league);
        }

        result.AddRange(Curated[league]);

        var distinct = result.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxSuggestions - 1).ToList();
        distinct.Add(TeamRankingQuestion);
        return distinct;
    }

    // ARCL's summer season runs roughly mid-March to mid-November (see the Men's §1 League Overview).
    internal static string SeasonalQuestion(DateOnly today) =>
        today.Month is >= 3 and <= 11
            ? "What happens if a match is stopped early due to rain?"
            : "How do I register a new team?";

    private async Task<List<(string Number, string Title)>> GetPopularSectionsAsync(League league, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(
            """
            SELECT TOP (2) rc.section_number, rc.section_title
            FROM dbo.conversation_log_retrieved_chunks clrc
            JOIN dbo.conversation_log cl ON cl.log_id = clrc.log_id
            JOIN dbo.rule_chunks rc ON rc.chunk_id = clrc.chunk_id
            WHERE rc.league = @league
              AND rc.effective_end IS NULL
              AND cl.created_at >= DATEADD(DAY, -30, SYSUTCDATETIME())
              AND (cl.user_feedback IS NULL OR cl.user_feedback = 1) -- don't promote what people said was wrong
            GROUP BY rc.section_number, rc.section_title
            HAVING COUNT(*) >= 2
            ORDER BY COUNT(*) DESC
            """, conn);
        cmd.Parameters.AddWithValue("@league", league.ToDbValue());

        var rows = new List<(string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    /// <summary>
    /// A section title phrased as something a visitor would click. FAQ titles already are
    /// questions ("3. How can I update my player's name...?") — strip the numbering. Rule and
    /// appendix titles become "What does the rulebook say about X?". Returns null for titles that
    /// don't make a sensible question (empty grouping headers, generic overview titles).
    /// </summary>
    public static string? ToSuggestion(string sectionNumber, string title)
    {
        var t = Regex.Replace(title.Trim(), @"\s+", " ");
        if (t.Length == 0) return null;

        if (sectionNumber.StartsWith("FAQ", StringComparison.OrdinalIgnoreCase))
        {
            t = Regex.Replace(t, @"^\d+\.\s*", "");
            return t.EndsWith('?') ? t : t + "?";
        }

        // "Appendix B (Net Run-Rate)" -> "Net Run-Rate"; "Appendix L - Number of teams in a season" -> the part after the dash.
        var appendix = Regex.Match(t, @"^Appendix\s+\w+\s*(?:\((?<inner>[^)]*)\)|[-–—]+\s*(?<inner>.+))", RegexOptions.IgnoreCase);
        if (appendix.Success) t = appendix.Groups["inner"].Value.Trim();
        else if (t.StartsWith("Appendix", StringComparison.OrdinalIgnoreCase)) return null; // bare "Appendix L"

        t = t.TrimEnd(':', ' ');
        if (t.Length < 3 || Regex.IsMatch(t, @"^(League Overview|ARCL Cricket Rules|League Rules\b.*|Other League Exceptions)$", RegexOptions.IgnoreCase))
            return null;

        return $"What does the rulebook say about {t}?";
    }
}
