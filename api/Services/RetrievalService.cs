using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Arcl.Api.Models;

namespace Arcl.Api.Services;

public sealed record RetrievedChunk(
    Guid ChunkId, string SectionNumber, string SectionTitle, string Content, string SourceUrl, double SemanticDistance);

/// <summary>
/// Hybrid retrieval: semantic similarity (SQL Server 2025's native VECTOR_DISTANCE, exact
/// search — see sql/001_schema.sql for why exact rather than the still-preview approximate
/// index) combined with keyword matching (LIKE — see the fallback note below), always filtered
/// to one league and to whatever rule version was in force as of the query date. That filter is
/// the "context wall" from the blueprint: it runs on every call, not just when a request happens
/// to look ambiguous.
/// </summary>
public sealed class RetrievalService
{
    private readonly string _connectionString;
    private readonly GeminiClient _gemini;
    private readonly int _topN;

    public RetrievalService(IConfiguration config, GeminiClient gemini)
    {
        _connectionString = config.GetNonEmpty("Arcl:ConnectionString")
            ?? Environment.GetEnvironmentVariable("ARCL_DB_CONNECTION")
            ?? throw new InvalidOperationException("Set Arcl:ConnectionString (appsettings) or ARCL_DB_CONNECTION (env var).");
        _gemini = gemini;
        _topN = config.GetValue<int?>("Arcl:RetrievalTopN") ?? 5;
    }

    public async Task<IReadOnlyList<RetrievedChunk>> HybridSearchAsync(string query, League league, DateOnly asOf, CancellationToken ct = default)
    {
        var queryEmbedding = new SqlVector<float>(await _gemini.EmbedAsync(query, ct));

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Semantic half: exact nearest-neighbor (VECTOR_DISTANCE) over the league-scoped,
        // currently-effective corpus. Keyword half (LIKE-based, below) runs as a second query
        // against the same table with the same filter and is combined in C# rather than one
        // UNIONed SQL statement, since the two produce different, not directly comparable,
        // ranking signals (cosine distance vs. keyword match count).
        const string sql = """
            SELECT TOP (@topN)
                chunk_id, section_number, section_title, content, source_url,
                VECTOR_DISTANCE('cosine', @queryEmbedding, embedding) AS semantic_distance
            FROM dbo.rule_chunks
            WHERE league = @league
              AND effective_start <= @asOf
              AND (effective_end IS NULL OR effective_end > @asOf)
              AND embedding IS NOT NULL
            ORDER BY semantic_distance ASC;
            """;

        var results = new Dictionary<Guid, RetrievedChunk>();

        await using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("@league", league.ToDbValue());
            cmd.Parameters.AddWithValue("@asOf", asOf.ToDateTime(TimeOnly.MinValue));
            // AddWithValue infers the SQL type from the SqlVector<float> value itself — see the
            // same note in ingest/Program.cs for why this is preferred over SqlDbType.Vector directly.
            cmd.Parameters.AddWithValue("@queryEmbedding", queryEmbedding);
            cmd.Parameters.AddWithValue("@topN", _topN);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var chunk = ReadChunk(reader, semanticDistanceOrdinal: 5);
                results[chunk.ChunkId] = chunk;
            }
        }

        // Keyword pass, scoped to the same league/date filter, merged in (a chunk found by both
        // keeps its semantic distance — that's what ordering downstream uses).
        //
        // Full-text search isn't installed on this SQL Server instance yet (see the note in
        // sql/001_schema.sql — CONTAINSTABLE throws error 7601, "not full-text indexed", without
        // it), so this uses a LIKE-based fallback ranked by how many distinct keywords each chunk
        // matches, as a stand-in for full-text RANK. Once full-text is installed and
        // sql/002_fulltext_optional.sql has been run, this can switch back to CONTAINSTABLE.
        var keywords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (keywords.Count > 0)
        {
            await using var cmd = new SqlCommand { Connection = conn };
            var matchExprs = new List<string>();
            var whereExprs = new List<string>();
            for (var i = 0; i < keywords.Count; i++)
            {
                var p = $"@kw{i}";
                cmd.Parameters.AddWithValue(p, $"%{keywords[i]}%");
                matchExprs.Add($"(CASE WHEN rc.content LIKE {p} OR rc.section_title LIKE {p} THEN 1 ELSE 0 END)");
                whereExprs.Add($"rc.content LIKE {p} OR rc.section_title LIKE {p}");
            }
            cmd.Parameters.AddWithValue("@league", league.ToDbValue());
            cmd.Parameters.AddWithValue("@asOf", asOf.ToDateTime(TimeOnly.MinValue));

            cmd.CommandText = $"""
                SELECT rc.chunk_id, rc.section_number, rc.section_title, rc.content, rc.source_url,
                    ({string.Join(" + ", matchExprs)}) AS match_score
                FROM dbo.rule_chunks rc
                WHERE rc.league = @league
                  AND rc.effective_start <= @asOf
                  AND (rc.effective_end IS NULL OR rc.effective_end > @asOf)
                  AND ({string.Join(" OR ", whereExprs)})
                ORDER BY match_score DESC;
                """;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct) && results.Count < _topN * 2)
            {
                var chunk = ReadChunk(reader, semanticDistanceOrdinal: null);
                if (!results.ContainsKey(chunk.ChunkId))
                    results[chunk.ChunkId] = chunk;
            }
        }

        return results.Values.OrderBy(r => r.SemanticDistance).Take(_topN).ToList();
    }

    private static RetrievedChunk ReadChunk(SqlDataReader reader, int? semanticDistanceOrdinal)
    {
        return new RetrievedChunk(
            ChunkId: reader.GetGuid(0),
            SectionNumber: reader.GetString(1),
            SectionTitle: reader.GetString(2),
            Content: reader.GetString(3),
            SourceUrl: reader.GetString(4),
            // Keyword-only hits have no semantic distance to sort by — treat as "close enough to
            // include but not to prioritize" so semantic matches still lead the merged ranking.
            SemanticDistance: semanticDistanceOrdinal is int ord ? reader.GetDouble(ord) : 0.5);
    }
}
