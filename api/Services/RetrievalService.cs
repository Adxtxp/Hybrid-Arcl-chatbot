using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Arcl.Api.Models;

namespace Arcl.Api.Services;

public sealed record RetrievedChunk(
    Guid ChunkId, string SectionNumber, string SectionTitle, string Content, string SourceUrl,
    double SemanticDistance, double? RerankScore = null);

/// <summary>Chunks is what goes to the model, best first. BestDistance is the closest cosine
/// distance anywhere in the league's corpus — the confidence gate's input (see
/// ChatOrchestrator.cs), kept separate from the final ordering, which the re-ranker decides.</summary>
public sealed record RetrievalResult(IReadOnlyList<RetrievedChunk> Chunks, double BestDistance)
{
    public static readonly RetrievalResult Empty = new(Array.Empty<RetrievedChunk>(), double.MaxValue);
}

/// <summary>
/// Hybrid retrieval, always filtered to one league and to whatever rule version was in force as
/// of the query date (the "context wall" from the blueprint — it runs on every call, not just when
/// a request happens to look ambiguous):
///
///   1. Semantic: SQL Server 2025's native VECTOR_DISTANCE, exact search (see sql/001_schema.sql
///      for why exact rather than the preview approximate index). Computed for every chunk in the
///      league — a few hundred rows at most — so every candidate has a real distance, not a
///      placeholder.
///   2. Keyword: real Okapi BM25 (Bm25Index.cs) over an in-memory snapshot of the same chunks.
///   3. Fusion: Reciprocal Rank Fusion of the two rankings. RRF only uses rank positions, which is
///      what makes cosine distance and BM25 score — two signals on unrelated scales — combinable
///      without hand-tuned weights.
///   4. Re-rank: the top fused candidates are re-scored by the cross-encoder
///      (CrossEncoderReranker.cs), which reads the question and each chunk together.
/// </summary>
public sealed class RetrievalService
{
    // Standard RRF constant from the original paper (Cormack et al. 2009); dampens the advantage
    // of being #1 vs #2 so one ranking can't dominate on its own.
    private const int RrfK = 60;

    private readonly string _connectionString;
    private readonly GeminiClient _gemini;
    private readonly CrossEncoderReranker _reranker;
    private readonly ILogger<RetrievalService> _logger;
    private readonly int _topN;
    private readonly int _candidatesPerRanking;
    private readonly int _rerankCandidates;
    private readonly TimeSpan _corpusCacheDuration;

    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private readonly Dictionary<League, CorpusSnapshot> _cache = new();

    private sealed record CorpusChunk(Guid ChunkId, string SectionNumber, string SectionTitle, string Content, string SourceUrl);

    private sealed record CorpusSnapshot(
        DateOnly AsOf, DateTime LoadedAtUtc, IReadOnlyDictionary<Guid, CorpusChunk> Chunks, Bm25Index Bm25);

    public RetrievalService(IConfiguration config, GeminiClient gemini, CrossEncoderReranker reranker, ILogger<RetrievalService> logger)
    {
        _connectionString = config.GetNonEmpty("Arcl:ConnectionString")
            ?? Environment.GetEnvironmentVariable("ARCL_DB_CONNECTION")
            ?? throw new InvalidOperationException("Set Arcl:ConnectionString (appsettings) or ARCL_DB_CONNECTION (env var).");
        _gemini = gemini;
        _reranker = reranker;
        _logger = logger;
        _topN = config.GetValue<int?>("Arcl:RetrievalTopN") ?? 5;
        _candidatesPerRanking = config.GetValue<int?>("Arcl:RetrievalCandidatesPerRanking") ?? 30;
        _rerankCandidates = config.GetValue<int?>("Arcl:Reranker:Candidates") ?? 20;
        _corpusCacheDuration = TimeSpan.FromMinutes(config.GetValue<int?>("Arcl:CorpusCacheMinutes") ?? 10);
    }

    public async Task<RetrievalResult> HybridSearchAsync(string query, League league, DateOnly asOf, CancellationToken ct = default)
    {
        var corpus = await GetCorpusAsync(league, asOf, ct);
        if (corpus.Chunks.Count == 0) return RetrievalResult.Empty;

        // ---- 1. semantic
        var distances = await GetDistancesAsync(query, league, asOf, ct);
        if (distances.Count == 0) return RetrievalResult.Empty;
        var vectorRanking = distances.OrderBy(kv => kv.Value).Take(_candidatesPerRanking).Select(kv => kv.Key).ToList();

        // ---- 2. keyword
        var bm25Ranking = corpus.Bm25.Search(query, _candidatesPerRanking).Select(r => r.Id).ToList();

        // ---- 3. fusion
        var fused = new Dictionary<Guid, double>();
        void AddRanking(List<Guid> ranking)
        {
            for (var rank = 0; rank < ranking.Count; rank++)
                fused[ranking[rank]] = fused.GetValueOrDefault(ranking[rank]) + 1.0 / (RrfK + rank + 1);
        }
        AddRanking(vectorRanking);
        AddRanking(bm25Ranking);

        var candidates = fused
            .OrderByDescending(kv => kv.Value)
            // A chunk can be in the BM25 snapshot but not have a distance if it was re-ingested
            // between the snapshot and this query — skip rather than guess at its distance.
            .Where(kv => corpus.Chunks.ContainsKey(kv.Key) && distances.ContainsKey(kv.Key))
            .Take(_rerankCandidates)
            .Select(kv =>
            {
                var c = corpus.Chunks[kv.Key];
                return new RetrievedChunk(c.ChunkId, c.SectionNumber, c.SectionTitle, c.Content, c.SourceUrl, distances[kv.Key]);
            })
            .ToList();

        // ---- 4. re-rank
        IReadOnlyList<RetrievedChunk> ordered = candidates;
        if (_reranker.IsAvailable && candidates.Count > 1)
        {
            try
            {
                var scores = _reranker.Score(query, candidates.Select(c => $"{c.SectionTitle}. {c.Content}").ToList());
                ordered = candidates
                    .Select((c, i) => c with { RerankScore = scores[i] })
                    .OrderByDescending(c => c.RerankScore)
                    .ToList();
            }
            catch (Exception ex)
            {
                // Re-ranking is an improvement, not a dependency — fall back to the fused order
                // rather than failing the visitor's question.
                _logger.LogError(ex, "Cross-encoder re-ranking failed; using fused order.");
            }
        }

        return new RetrievalResult(ordered.Take(_topN).ToList(), distances.Values.Min());
    }

    // Cosine distance from the query to every currently-effective chunk in the league.
    private async Task<Dictionary<Guid, double>> GetDistancesAsync(string query, League league, DateOnly asOf, CancellationToken ct)
    {
        var queryEmbedding = new SqlVector<float>(await _gemini.EmbedAsync(query, ct));

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT chunk_id, VECTOR_DISTANCE('cosine', @queryEmbedding, embedding) AS semantic_distance
            FROM dbo.rule_chunks
            WHERE league = @league
              AND effective_start <= @asOf
              AND (effective_end IS NULL OR effective_end > @asOf)
              AND embedding IS NOT NULL;
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@league", league.ToDbValue());
        cmd.Parameters.AddWithValue("@asOf", asOf.ToDateTime(TimeOnly.MinValue));
        // AddWithValue infers the SQL type from the SqlVector<float> value itself — see the same
        // note in ingest/Program.cs for why this is preferred over SqlDbType.Vector directly.
        cmd.Parameters.AddWithValue("@queryEmbedding", queryEmbedding);

        var result = new Dictionary<Guid, double>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetGuid(0)] = reader.GetDouble(1);
        return result;
    }

    // ------------------------------------------------------------------ corpus snapshot (BM25)

    private async Task<CorpusSnapshot> GetCorpusAsync(League league, DateOnly asOf, CancellationToken ct)
    {
        if (TryGetFresh(league, asOf, out var cached)) return cached;

        await _cacheLock.WaitAsync(ct);
        try
        {
            if (TryGetFresh(league, asOf, out cached)) return cached; // another request just loaded it

            var chunks = await LoadCorpusAsync(league, asOf, ct);
            var snapshot = new CorpusSnapshot(asOf, DateTime.UtcNow, chunks.ToDictionary(c => c.ChunkId),
                new Bm25Index(chunks.Select(c => (c.ChunkId, c.SectionTitle, c.Content))));
            lock (_cache) _cache[league] = snapshot;
            _logger.LogInformation("Loaded {Count} chunks for {League} into the BM25 index.", chunks.Count, league);
            return snapshot;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private bool TryGetFresh(League league, DateOnly asOf, out CorpusSnapshot snapshot)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(league, out var s) && s.AsOf == asOf
                && DateTime.UtcNow - s.LoadedAtUtc < _corpusCacheDuration)
            {
                snapshot = s;
                return true;
            }
        }
        snapshot = null!;
        return false;
    }

    private async Task<List<CorpusChunk>> LoadCorpusAsync(League league, DateOnly asOf, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Same filter as the semantic query, including "embedding IS NOT NULL" — so both halves
        // rank exactly the same set (empty grouping headers like mens §35 have no embedding).
        await using var cmd = new SqlCommand(
            """
            SELECT chunk_id, section_number, section_title, content, source_url
            FROM dbo.rule_chunks
            WHERE league = @league
              AND effective_start <= @asOf
              AND (effective_end IS NULL OR effective_end > @asOf)
              AND embedding IS NOT NULL;
            """, conn);
        cmd.Parameters.AddWithValue("@league", league.ToDbValue());
        cmd.Parameters.AddWithValue("@asOf", asOf.ToDateTime(TimeOnly.MinValue));

        var result = new List<CorpusChunk>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new CorpusChunk(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return result;
    }
}
