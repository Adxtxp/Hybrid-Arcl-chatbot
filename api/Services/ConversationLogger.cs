using Microsoft.Data.SqlClient;
using Arcl.Api.Models;

namespace Arcl.Api.Services;

/// <summary>
/// Writes every turn to conversation_log (+ the retrieved-chunks junction table). This one table
/// is what Phase 4's evaluation pipeline and Phase 5's telemetry dashboards both read from — see
/// sql/004_dashboards.sql for the queries built on top of it.
/// </summary>
public sealed class ConversationLogger
{
    private readonly string _connectionString;

    public ConversationLogger(IConfiguration config)
    {
        _connectionString = config.GetNonEmpty("Arcl:ConnectionString")
            ?? Environment.GetEnvironmentVariable("ARCL_DB_CONNECTION")
            ?? throw new InvalidOperationException("Set Arcl:ConnectionString (appsettings) or ARCL_DB_CONNECTION (env var).");
    }

    public async Task<long> LogAsync(
        Guid sessionId,
        string query,
        RoutePath routePath,
        string? responseText,
        int latencyMs,
        decimal? tokenCostUsd,
        IReadOnlyList<Guid> retrievedChunkIds,
        int? promptTokens = null,
        int? completionTokens = null,
        double? bestDistance = null,
        string? confidenceTier = null,
        bool? outputFiltered = null,
        CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // best_distance/confidence_tier/output_filtered: sql/008_phase2_hardening.sql.
        await using var insertCmd = new SqlCommand(
            """
            INSERT INTO dbo.conversation_log
                (session_id, query, route_path, response_text, latency_ms, token_cost_usd, prompt_tokens, completion_tokens,
                 best_distance, confidence_tier, output_filtered)
            OUTPUT INSERTED.log_id
            VALUES (@sessionId, @query, @routePath, @responseText, @latencyMs, @tokenCostUsd, @promptTokens, @completionTokens,
                    @bestDistance, @confidenceTier, @outputFiltered)
            """, conn);
        insertCmd.Parameters.AddWithValue("@sessionId", sessionId);
        insertCmd.Parameters.AddWithValue("@query", query);
        insertCmd.Parameters.AddWithValue("@routePath", routePath.ToDbValue());
        insertCmd.Parameters.AddWithValue("@responseText", (object?)responseText ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@latencyMs", latencyMs);
        insertCmd.Parameters.AddWithValue("@tokenCostUsd", (object?)tokenCostUsd ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@promptTokens", (object?)promptTokens ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@completionTokens", (object?)completionTokens ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@bestDistance", (object?)bestDistance ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@confidenceTier", (object?)confidenceTier ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@outputFiltered", (object?)outputFiltered ?? DBNull.Value);

        var logId = (long)(await insertCmd.ExecuteScalarAsync(ct))!;

        foreach (var chunkId in retrievedChunkIds)
        {
            await using var linkCmd = new SqlCommand(
                "INSERT INTO dbo.conversation_log_retrieved_chunks (log_id, chunk_id) VALUES (@logId, @chunkId)", conn);
            linkCmd.Parameters.AddWithValue("@logId", logId);
            linkCmd.Parameters.AddWithValue("@chunkId", chunkId);
            await linkCmd.ExecuteNonQueryAsync(ct);
        }

        return logId;
    }

    /// <summary>
    /// The last few answered exchanges in this session, oldest first — the session's conversational
    /// memory. Read straight back out of conversation_log rather than kept in a separate store:
    /// every turn is already written there, so memory can never drift from what was actually said.
    /// Only real grounded answers count (confidence_tier high/medium — set only by the FAQ answer
    /// path): a decision-tree click ("[decision: Yes]"), a handoff message, or a "which team?"
    /// prompt (also logged as route faq) gives a follow-up question nothing useful to refer to.
    /// </summary>
    public async Task<IReadOnlyList<ConversationTurn>> GetRecentTurnsAsync(Guid sessionId, int maxTurns, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(
            """
            SELECT TOP (@maxTurns) query, response_text
            FROM dbo.conversation_log
            WHERE session_id = @sessionId AND confidence_tier IN ('high', 'medium') AND response_text IS NOT NULL
              AND token_cost_usd > 0 -- a real model answer; excludes "sections only" turns logged while the budget cap was hit
            ORDER BY log_id DESC
            """, conn);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        cmd.Parameters.AddWithValue("@maxTurns", maxTurns);

        var turns = new List<ConversationTurn>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            turns.Add(new ConversationTurn(reader.GetString(0), reader.GetString(1)));
        turns.Reverse();
        return turns;
    }

    /// <summary>The session's last few exchanges, whatever route they took, oldest first — the
    /// conversation included in a contact-form email so the admin sees what the visitor already
    /// asked and what the bot told them, without opening the admin page.</summary>
    /// <param name="beforeLogId">Only turns logged before this one — the handoff request's own row,
    /// so a retried email shows the conversation as it was, not what happened after.</param>
    public async Task<IReadOnlyList<(string Question, string? Answer)>> GetTranscriptAsync(Guid sessionId, int maxTurns,
        long beforeLogId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            """
            SELECT TOP (@maxTurns) query, response_text
            FROM dbo.conversation_log
            WHERE session_id = @sessionId AND log_id < @beforeLogId
            ORDER BY log_id DESC
            """, conn);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        cmd.Parameters.AddWithValue("@maxTurns", maxTurns);
        cmd.Parameters.AddWithValue("@beforeLogId", beforeLogId);

        var turns = new List<(string, string?)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            turns.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        turns.Reverse();
        return turns;
    }

    /// <summary>The contact-admin form submission (sql/008_phase2_hardening.sql), linked to the
    /// conversation_log row for the same request. consentText is the exact wording shown.
    /// Returns the new request_id.</summary>
    public async Task<long> LogHandoffRequestAsync(
        long logId, Guid sessionId, string? name, string email, string message, string consentText, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(
            """
            INSERT INTO dbo.handoff_requests
                (log_id, session_id, contact_name, contact_email, message, consent_text, consent_given_at)
            OUTPUT inserted.request_id
            VALUES (@logId, @sessionId, @name, @email, @message, @consentText, SYSUTCDATETIME())
            """, conn);
        cmd.Parameters.AddWithValue("@logId", logId);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        cmd.Parameters.AddWithValue("@name", (object?)name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@email", email);
        cmd.Parameters.AddWithValue("@message", message);
        cmd.Parameters.AddWithValue("@consentText", consentText);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task RecordFeedbackAsync(long logId, bool thumbsUp, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(
            "UPDATE dbo.conversation_log SET user_feedback = @feedback WHERE log_id = @logId", conn);
        cmd.Parameters.AddWithValue("@feedback", thumbsUp ? 1 : 0);
        cmd.Parameters.AddWithValue("@logId", logId);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows == 0)
            throw new KeyNotFoundException($"No conversation_log row with log_id {logId}.");
    }
}
