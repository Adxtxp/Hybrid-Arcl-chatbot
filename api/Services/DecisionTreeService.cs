using System.Text.Json;
using Microsoft.Data.SqlClient;
using Arcl.Api.Models;

namespace Arcl.Api.Services;

public sealed record DecisionTreeOption(string Label, Guid? NextNodeId, string? Answer);

public sealed record DecisionTreeNode(Guid NodeId, string QuestionText, IReadOnlyList<DecisionTreeOption> Options, Guid? SourceChunkId);

public sealed record DecisionTreeStepResult(
    bool IsTerminal, string Message, IReadOnlyList<DecisionTreeOption> Options, Citation? Citation);

/// <summary>
/// Deterministic navigation through decision_tree_nodes — no LLM call on this path at all,
/// which is the entire reason FAQ and decision-tree questions get routed differently in Phase 2.
/// </summary>
public sealed class DecisionTreeService
{
    private readonly string _connectionString;
    private readonly string _supportContact;

    public DecisionTreeService(IConfiguration config)
    {
        _connectionString = config.GetNonEmpty("Arcl:ConnectionString")
            ?? Environment.GetEnvironmentVariable("ARCL_DB_CONNECTION")
            ?? throw new InvalidOperationException("Set Arcl:ConnectionString (appsettings) or ARCL_DB_CONNECTION (env var).");
        _supportContact = config.GetNonEmpty("Arcl:SupportContact")
            ?? throw new InvalidOperationException("Set Arcl:SupportContact in appsettings.json.");
    }

    /// <summary>
    /// Starts a tree for the given topic and league, and records it as the session's current node.
    /// When <paramref name="rootAnswer"/> names a root option that leads to another node (the
    /// visitor's message already answered the root question), starts at that node instead.
    /// </summary>
    public async Task<DecisionTreeStepResult?> StartAsync(Guid sessionId, League league, string topic,
        string? rootAnswer = null, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var root = await LoadNodeByTopicAsync(conn, league, topic, ct);
        if (root is null)
            return null; // no tree built for this topic yet — caller falls back to human handoff

        var answered = rootAnswer is null ? null
            : root.Options.FirstOrDefault(o => string.Equals(o.Label, rootAnswer, StringComparison.OrdinalIgnoreCase));
        if (answered?.NextNodeId is Guid nextId && await LoadNodeByIdAsync(conn, nextId, ct) is { } next)
        {
            await SetSessionNodeAsync(conn, sessionId, next.NodeId, ct);
            return new DecisionTreeStepResult(IsTerminal: false, Message: next.QuestionText, Options: next.Options, Citation: null);
        }

        await SetSessionNodeAsync(conn, sessionId, root.NodeId, ct);
        return new DecisionTreeStepResult(IsTerminal: false, Message: root.QuestionText, Options: root.Options, Citation: null);
    }

    /// <summary>Advances the session's current node using the option the user picked.</summary>
    public async Task<DecisionTreeStepResult> AdvanceAsync(Guid sessionId, string selectedLabel, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var currentNodeId = await GetSessionNodeAsync(conn, sessionId, ct)
            ?? throw new InvalidOperationException("Session has no active decision tree node.");

        var node = await LoadNodeByIdAsync(conn, currentNodeId, ct)
            ?? throw new InvalidOperationException($"Decision tree node {currentNodeId} not found.");

        var chosen = node.Options.FirstOrDefault(o => string.Equals(o.Label, selectedLabel, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"\"{selectedLabel}\" is not a valid option for this step.");

        if (chosen.NextNodeId is Guid nextId)
        {
            var next = await LoadNodeByIdAsync(conn, nextId, ct)
                ?? throw new InvalidOperationException($"Decision tree node {nextId} not found.");
            await SetSessionNodeAsync(conn, sessionId, next.NodeId, ct);
            return new DecisionTreeStepResult(IsTerminal: false, Message: next.QuestionText, Options: next.Options, Citation: null);
        }

        // Terminal: no further node, just an answer. Cite the rule it came from — same
        // auditability requirement as FAQ answers, just without an LLM in the loop.
        await ClearSessionNodeAsync(conn, sessionId, ct);
        var citation = node.SourceChunkId is Guid chunkId ? await LoadCitationAsync(conn, chunkId, ct) : null;
        // Seeded answer text uses a {{SupportContact}} placeholder (see sql/003_seed_decision_trees.sql)
        // rather than hardcoding the email directly in the SQL data, so there's one source of truth.
        var message = (chosen.Answer ?? "No further guidance is available for that answer — please contact human support.")
            .Replace("{{SupportContact}}", _supportContact);
        return new DecisionTreeStepResult(
            IsTerminal: true,
            Message: message,
            Options: Array.Empty<DecisionTreeOption>(),
            Citation: citation);
    }

    // ------------------------------------------------------------------ session state

    private static async Task<Guid?> GetSessionNodeAsync(SqlConnection conn, Guid sessionId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT current_decision_tree_node FROM dbo.session_state WHERE session_id = @sessionId", conn);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is Guid g ? g : null;
    }

    private static async Task SetSessionNodeAsync(SqlConnection conn, Guid sessionId, Guid nodeId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "UPDATE dbo.session_state SET current_decision_tree_node = @nodeId, updated_at = SYSUTCDATETIME() WHERE session_id = @sessionId", conn);
        cmd.Parameters.AddWithValue("@nodeId", nodeId);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ClearSessionNodeAsync(SqlConnection conn, Guid sessionId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "UPDATE dbo.session_state SET current_decision_tree_node = NULL, updated_at = SYSUTCDATETIME() WHERE session_id = @sessionId", conn);
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------------ node loading

    private static async Task<DecisionTreeNode?> LoadNodeByTopicAsync(SqlConnection conn, League league, string topic, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            """
            SELECT node_id, question_text, options, source_chunk_id
            FROM dbo.decision_tree_nodes
            WHERE league = @league AND topic = @topic AND is_root = 1
            """, conn);
        cmd.Parameters.AddWithValue("@league", league.ToDbValue());
        cmd.Parameters.AddWithValue("@topic", topic);
        return await ReadSingleNodeAsync(cmd, ct);
    }

    private static async Task<DecisionTreeNode?> LoadNodeByIdAsync(SqlConnection conn, Guid nodeId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT node_id, question_text, options, source_chunk_id FROM dbo.decision_tree_nodes WHERE node_id = @nodeId", conn);
        cmd.Parameters.AddWithValue("@nodeId", nodeId);
        return await ReadSingleNodeAsync(cmd, ct);
    }

    private static async Task<DecisionTreeNode?> ReadSingleNodeAsync(SqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var nodeId = reader.GetGuid(0);
        var questionText = reader.GetString(1);
        var optionsJson = reader.GetString(2);
        var sourceChunkId = reader.IsDBNull(3) ? (Guid?)null : reader.GetGuid(3);

        var rawOptions = JsonSerializer.Deserialize<List<RawOption>>(optionsJson)
            ?? throw new InvalidOperationException($"Node {nodeId} has unparseable options JSON.");
        var options = rawOptions.Select(o => new DecisionTreeOption(o.Label, o.NextNodeId, o.Answer)).ToList();

        return new DecisionTreeNode(nodeId, questionText, options, sourceChunkId);
    }

    private static async Task<Citation?> LoadCitationAsync(SqlConnection conn, Guid chunkId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT section_number, section_title, source_url FROM dbo.rule_chunks WHERE chunk_id = @chunkId", conn);
        cmd.Parameters.AddWithValue("@chunkId", chunkId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new Citation(reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    // options JSON shape: [{"label":"Yes","next_node_id":"<guid>"}, {"label":"No","answer":"..."}]
    private sealed record RawOption(
        [property: System.Text.Json.Serialization.JsonPropertyName("label")] string Label,
        [property: System.Text.Json.Serialization.JsonPropertyName("next_node_id")] Guid? NextNodeId,
        [property: System.Text.Json.Serialization.JsonPropertyName("answer")] string? Answer);
}
