using Microsoft.Data.SqlClient;
using Arcl.Api.Models;

namespace Arcl.Api.Services;

public sealed class SessionService
{
    private readonly string _connectionString;

    public SessionService(IConfiguration config)
    {
        _connectionString = config.GetNonEmpty("Arcl:ConnectionString")
            ?? Environment.GetEnvironmentVariable("ARCL_DB_CONNECTION")
            ?? throw new InvalidOperationException("Set Arcl:ConnectionString (appsettings) or ARCL_DB_CONNECTION (env var).");
    }

    public async Task<Guid> CreateAsync(League? league, CancellationToken ct = default)
    {
        var sessionId = Guid.NewGuid();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(
            "INSERT INTO dbo.session_state (session_id, selected_league) VALUES (@sessionId, @league)", conn);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        cmd.Parameters.AddWithValue("@league", (object?)league?.ToDbValue() ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);

        return sessionId;
    }

    public async Task SetLeagueAsync(Guid sessionId, League league, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // "Change League" (the frontend's escape hatch) also clears any in-progress decision
        // tree or pending team-ranking clarification — continuing either under a different
        // league/topic than the one it started under would just be confusing.
        await using var cmd = new SqlCommand(
            """
            UPDATE dbo.session_state
            SET selected_league = @league, current_decision_tree_node = NULL, pending_team_ranking = 0,
                updated_at = SYSUTCDATETIME()
            WHERE session_id = @sessionId
            """, conn);
        cmd.Parameters.AddWithValue("@league", league.ToDbValue());
        cmd.Parameters.AddWithValue("@sessionId", sessionId);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows == 0)
            throw new KeyNotFoundException($"No session {sessionId}.");
    }

    public async Task<League?> GetLeagueAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(
            "SELECT selected_league FROM dbo.session_state WHERE session_id = @sessionId", conn);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null or DBNull)
            return null;
        return LeagueExtensions.FromDbValue((string)result);
    }

    // ------------------------------------------------------------------ team ranking clarification
    // "Which team?" is a real multi-turn exchange (see sql/006_team_ranking.sql) — this flag is
    // what tells /api/chat to treat the visitor's NEXT message as a literal team name instead of
    // running it through the normal intent classifier.

    public async Task<bool> GetPendingTeamRankingAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(
            "SELECT pending_team_ranking FROM dbo.session_state WHERE session_id = @sessionId", conn);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is bool b && b;
    }

    public async Task SetPendingTeamRankingAsync(Guid sessionId, bool pending, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(
            "UPDATE dbo.session_state SET pending_team_ranking = @pending, updated_at = SYSUTCDATETIME() WHERE session_id = @sessionId",
            conn);
        cmd.Parameters.AddWithValue("@pending", pending);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
