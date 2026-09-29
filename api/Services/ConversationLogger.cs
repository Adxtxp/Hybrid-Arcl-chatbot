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
        CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var insertCmd = new SqlCommand(
            """
            INSERT INTO dbo.conversation_log
                (session_id, query, route_path, response_text, latency_ms, token_cost_usd, prompt_tokens, completion_tokens)
            OUTPUT INSERTED.log_id
            VALUES (@sessionId, @query, @routePath, @responseText, @latencyMs, @tokenCostUsd, @promptTokens, @completionTokens)
            """, conn);
        insertCmd.Parameters.AddWithValue("@sessionId", sessionId);
        insertCmd.Parameters.AddWithValue("@query", query);
        insertCmd.Parameters.AddWithValue("@routePath", routePath.ToDbValue());
        insertCmd.Parameters.AddWithValue("@responseText", (object?)responseText ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@latencyMs", latencyMs);
        insertCmd.Parameters.AddWithValue("@tokenCostUsd", (object?)tokenCostUsd ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@promptTokens", (object?)promptTokens ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("@completionTokens", (object?)completionTokens ?? DBNull.Value);

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
