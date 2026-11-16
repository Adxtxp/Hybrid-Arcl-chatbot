using Microsoft.Data.SqlClient;
using Arcl.Api.Models;

namespace Arcl.Api.Services;

/// <summary>
/// Read (and one write) side of the admin surface — wraps the same queries as
/// sql/004_dashboards.sql, plus marking a review-queue row as looked at (sql/
/// 005_admin_review_tracking.sql). No new tables: conversation_log is still the one place both
/// the eval pipeline (Phase 4) and this admin view (Phase 5) read/write, same as ConversationLogger.
/// </summary>
public sealed class AdminService
{
    private readonly string _connectionString;

    public AdminService(IConfiguration config)
    {
        _connectionString = config.GetNonEmpty("Arcl:ConnectionString")
            ?? Environment.GetEnvironmentVariable("ARCL_DB_CONNECTION")
            ?? throw new InvalidOperationException("Set Arcl:ConnectionString (appsettings) or ARCL_DB_CONNECTION (env var).");
    }

    public async Task<AdminSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // sql/004_dashboards.sql query #6 — weekly totals, extended with prompt/completion token
        // sums (sql/007_token_tracking.sql).
        int totalQueries = 0, faqQueries = 0, decisionTreeQueries = 0, humanHandoffQueries = 0,
            thumbsUpCount = 0, thumbsDownCount = 0;
        decimal? totalCostUsd = null;
        double? avgLatencyMs = null;
        long? totalPromptTokens = null, totalCompletionTokens = null;

        await using (var cmd = new SqlCommand(
            """
            SELECT
                COUNT(*) AS total_queries,
                SUM(CASE WHEN route_path = 'faq' THEN 1 ELSE 0 END) AS faq_queries,
                SUM(CASE WHEN route_path = 'decision_tree' THEN 1 ELSE 0 END) AS decision_tree_queries,
                SUM(CASE WHEN route_path = 'human_handoff' THEN 1 ELSE 0 END) AS human_handoff_queries,
                SUM(CASE WHEN user_feedback = 0 THEN 1 ELSE 0 END) AS thumbs_down_count,
                SUM(CASE WHEN user_feedback = 1 THEN 1 ELSE 0 END) AS thumbs_up_count,
                SUM(token_cost_usd) AS total_cost_usd,
                AVG(CAST(latency_ms AS FLOAT)) AS avg_latency_ms,
                SUM(CAST(prompt_tokens AS BIGINT)) AS total_prompt_tokens,
                SUM(CAST(completion_tokens AS BIGINT)) AS total_completion_tokens
            FROM dbo.conversation_log
            WHERE created_at >= DATEADD(DAY, -7, SYSUTCDATETIME())
            """, conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                totalQueries = reader.GetInt32(0);
                faqQueries = reader.IsDBNull(1) ? 0 : (int)reader.GetInt32(1);
                decisionTreeQueries = reader.IsDBNull(2) ? 0 : (int)reader.GetInt32(2);
                humanHandoffQueries = reader.IsDBNull(3) ? 0 : (int)reader.GetInt32(3);
                thumbsDownCount = reader.IsDBNull(4) ? 0 : (int)reader.GetInt32(4);
                thumbsUpCount = reader.IsDBNull(5) ? 0 : (int)reader.GetInt32(5);
                totalCostUsd = reader.IsDBNull(6) ? null : reader.GetDecimal(6);
                avgLatencyMs = reader.IsDBNull(7) ? null : reader.GetDouble(7);
                totalPromptTokens = reader.IsDBNull(8) ? null : reader.GetInt64(8);
                totalCompletionTokens = reader.IsDBNull(9) ? null : reader.GetInt64(9);
            }
        }

        // Richer than sql/004_dashboards.sql query #4: adds tokens and thumbs up/down per day
        // (that query only has cost) since none of the 6 numbered queries combine all of those,
        // and the admin page wants one activity table, not three near-duplicates. Capped to 14
        // days (vs. that query's unbounded history) since this feeds a small page table, not a
        // full report. LEFT JOIN + a date spine (not just GROUP BY created_at) so a day with
        // decision-tree/handoff-only traffic (token_cost_usd IS NULL for those) still shows its
        // query count and feedback instead of silently disappearing from the trend.
        var dailyActivity = new List<DailyActivity>();
        await using (var cmd = new SqlCommand(
            """
            SELECT TOP (14)
                CAST(created_at AS DATE) AS [date],
                COUNT(*) AS query_count,
                SUM(token_cost_usd) AS total_cost_usd,
                AVG(token_cost_usd) AS avg_cost_per_query_usd,
                SUM(CAST(prompt_tokens AS BIGINT)) AS total_prompt_tokens,
                SUM(CAST(completion_tokens AS BIGINT)) AS total_completion_tokens,
                SUM(CASE WHEN user_feedback = 1 THEN 1 ELSE 0 END) AS thumbs_up_count,
                SUM(CASE WHEN user_feedback = 0 THEN 1 ELSE 0 END) AS thumbs_down_count
            FROM dbo.conversation_log
            GROUP BY CAST(created_at AS DATE)
            ORDER BY [date] DESC
            """, conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                dailyActivity.Add(new DailyActivity(
                    DateOnly.FromDateTime(reader.GetDateTime(0)),
                    reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                    reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7)));
            }
        }

        // sql/004_dashboards.sql query #1 — query volume by league, last 7 days.
        var queryVolumeByLeague = new List<LeagueQueryVolume>();
        await using (var cmd = new SqlCommand(
            """
            SELECT
                s.selected_league AS league,
                CAST(cl.created_at AS DATE) AS [date],
                COUNT(*) AS query_count
            FROM dbo.conversation_log cl
            JOIN dbo.session_state s ON s.session_id = cl.session_id
            WHERE cl.created_at >= DATEADD(DAY, -7, SYSUTCDATETIME())
            GROUP BY s.selected_league, CAST(cl.created_at AS DATE)
            ORDER BY [date] DESC, league
            """, conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                queryVolumeByLeague.Add(new LeagueQueryVolume(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    DateOnly.FromDateTime(reader.GetDateTime(1)),
                    reader.GetInt32(2)));
            }
        }

        return new AdminSummary(
            totalQueries, faqQueries, decisionTreeQueries, humanHandoffQueries,
            thumbsUpCount, thumbsDownCount, totalCostUsd, avgLatencyMs,
            totalPromptTokens, totalCompletionTokens,
            dailyActivity, queryVolumeByLeague);
    }

    // sql/004_dashboards.sql query #2 — every human_handoff route plus every thumbs-down, joined
    // with the reviewed_at/admin_note tracking columns. Not windowed to the last 7 days like the
    // raw dashboard query — the admin page is the ongoing ticket queue, not a weekly snapshot, so
    // an unreviewed item from three weeks ago should still show up.
    public async Task<IReadOnlyList<ReviewQueueItem>> GetReviewQueueAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(
            """
            SELECT
                cl.log_id, cl.created_at, s.selected_league AS league, cl.query, cl.response_text,
                cl.route_path, cl.user_feedback, cl.reviewed_at, cl.admin_note,
                hr.contact_name, hr.contact_email
            FROM dbo.conversation_log cl
            JOIN dbo.session_state s ON s.session_id = cl.session_id
            LEFT JOIN dbo.handoff_requests hr ON hr.log_id = cl.log_id -- sql/008: contact-form submissions
            WHERE cl.route_path = 'human_handoff' OR cl.user_feedback = 0
            ORDER BY cl.created_at DESC
            """, conn);

        var items = new List<ReviewQueueItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new ReviewQueueItem(
                reader.GetInt64(0),
                reader.GetDateTime(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetByte(6),
                reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }
        return items;
    }

    public async Task MarkReviewedAsync(long logId, string? note, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(
            "UPDATE dbo.conversation_log SET reviewed_at = SYSUTCDATETIME(), admin_note = @note WHERE log_id = @logId",
            conn);
        cmd.Parameters.AddWithValue("@note", (object?)note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@logId", logId);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows == 0)
            throw new KeyNotFoundException($"No conversation_log row with log_id {logId}.");
    }

    // sql/004_dashboards.sql query #3 — sessions that entered a decision tree and have sat idle
    // mid-tree for over an hour: a real visitor who asked a rain/no-show question and never got
    // an answer, not just an abstract number.
    public async Task<IReadOnlyList<DecisionTreeDropoffItem>> GetDecisionTreeDropoffAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(
            """
            SELECT
                s.session_id, s.selected_league AS league, dtn.question_text AS stuck_at_question, s.updated_at
            FROM dbo.session_state s
            JOIN dbo.decision_tree_nodes dtn ON dtn.node_id = s.current_decision_tree_node
            WHERE s.current_decision_tree_node IS NOT NULL
              AND s.updated_at < DATEADD(HOUR, -1, SYSUTCDATETIME())
            ORDER BY s.updated_at DESC
            """, conn);

        var items = new List<DecisionTreeDropoffItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new DecisionTreeDropoffItem(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetDateTime(3)));
        }
        return items;
    }

    // sql/004_dashboards.sql query #5 — the rule sections retrieval pulls most often (last 30
    // days): what people are actually asking about, useful for prioritizing which topics need a
    // decision tree, a clearer rulebook section, or (per the FAQ-Fees/mens-23 work) a content fix.
    public async Task<IReadOnlyList<TopChunkItem>> GetTopChunksAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(
            """
            SELECT TOP (20)
                rc.league, rc.section_number, rc.section_title, COUNT(*) AS times_retrieved
            FROM dbo.conversation_log_retrieved_chunks clrc
            JOIN dbo.rule_chunks rc ON rc.chunk_id = clrc.chunk_id
            JOIN dbo.conversation_log cl ON cl.log_id = clrc.log_id
            WHERE cl.created_at >= DATEADD(DAY, -30, SYSUTCDATETIME())
            GROUP BY rc.league, rc.section_number, rc.section_title
            ORDER BY times_retrieved DESC
            """, conn);

        var items = new List<TopChunkItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new TopChunkItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3)));
        }
        return items;
    }

    // Content freshness: what the nightly re-crawl (RecrawlService.cs) found for each page.
    public async Task<IReadOnlyList<SourcePageItem>> GetSourcesAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            """
            SELECT league, url, last_checked_at, last_success_at, last_changed_at, last_status, last_message, sections_matched
            FROM dbo.source_pages
            ORDER BY league, url
            """, conn);

        var items = new List<SourcePageItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new SourcePageItem(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7)));
        }
        return items;
    }

    // Retrieval quality per week (last 8 weeks) — the trend behind the drift alert (DriftCheck).
    // Weeks start on Monday; only turns that went through the confidence gate have a best_distance.
    public async Task<IReadOnlyList<DriftWeek>> GetDriftTrendAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            """
            SELECT DATEADD(DAY, -((DATEPART(WEEKDAY, created_at) + @@DATEFIRST - 2) % 7), CAST(created_at AS DATE)) AS week_start,
                   COUNT(*),
                   AVG(best_distance),
                   AVG(CASE WHEN confidence_tier IN ('low', 'out_of_scope') THEN 1.0 ELSE 0.0 END)
            FROM dbo.conversation_log
            WHERE best_distance IS NOT NULL AND created_at >= DATEADD(WEEK, -8, SYSUTCDATETIME())
            GROUP BY DATEADD(DAY, -((DATEPART(WEEKDAY, created_at) + @@DATEFIRST - 2) % 7), CAST(created_at AS DATE))
            ORDER BY week_start
            """, conn);

        var weeks = new List<DriftWeek>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            weeks.Add(new DriftWeek(DateOnly.FromDateTime(reader.GetDateTime(0)), reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetDouble(2), (double)reader.GetDecimal(3)));
        }
        return weeks;
    }

    // The same question, asked more than once, that the bot has NEVER answered (every occurrence
    // routed to human_handoff — including the out-of-scope/low-confidence FAQ variants, which are
    // logged as human_handoff too, see Program.cs). Grouped by exact query text — real repeat
    // askers and eval/test runs both produce identical text, so this reliably surfaces the same
    // question recurring; a rephrased version of the same question is a different row by design,
    // since silently fuzzy-matching questions risks merging two actually-different things.
    public async Task<IReadOnlyList<TopUnansweredItem>> GetTopUnansweredAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(
            """
            SELECT TOP (15) query, COUNT(*) AS times_asked, MAX(created_at) AS last_asked
            FROM dbo.conversation_log
            WHERE route_path = 'human_handoff'
            GROUP BY query
            HAVING COUNT(*) > 1
            ORDER BY times_asked DESC, last_asked DESC
            """, conn);

        var items = new List<TopUnansweredItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new TopUnansweredItem(reader.GetString(0), reader.GetInt32(1), reader.GetDateTime(2)));
        }
        return items;
    }
}
