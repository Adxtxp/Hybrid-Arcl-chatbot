using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Arcl.Api.Models;
using Arcl.Ingestion;

namespace Arcl.Api.Services;

public sealed record RecrawlSource(string League, string Url, string Anchors);

public enum RecrawlDecision { Unchanged, Apply, NeedsReview }

public sealed record RecrawlPlan(RecrawlDecision Decision, IReadOnlyList<ChunkResult> Changed, string Reason);

public sealed record RecrawlResult(string League, string Url, string Status, string Message, int SectionsMatched);

/// <summary>
/// Pure decision logic for one re-crawled page, kept separate from RecrawlService so it's unit
/// tested: given the freshly chunked page and what's currently in rule_chunks, apply the changes
/// automatically, or stop and ask a human.
///
/// It refuses to auto-apply exactly when chunking is most likely wrong rather than the rules
/// having changed — the failure modes this project has actually hit:
///   - a heading that can no longer be found (its content would silently fold into the previous section);
///   - a section that suddenly shrinks to a fraction of its size (the "Players"/"Points"/"Runs"
///     false-heading-match bug, which cut Caught Out down to 4 characters);
///   - most of the page changing at once (a site redesign, an error page, a different document).
/// </summary>
public static class RecrawlPlanner
{
    internal const double MaxChangedShare = 0.5;
    internal const double MinShrinkRatio = 0.25;
    internal const int ShrinkCheckMinChars = 200;

    public static RecrawlPlan Plan(
        IReadOnlyList<ChunkResult> chunks, IReadOnlyList<SectionAnchor> notFound, IReadOnlyDictionary<string, string> currentContent)
    {
        if (chunks.Count == 0)
            return Review("No sections could be matched on the page at all — it may be down, moved, or redesigned.");

        if (notFound.Count > 0)
            return Review($"{notFound.Count} section heading(s) could not be found: " +
                          string.Join(", ", notFound.Select(a => $"[{a.SectionNumber}] \"{a.SectionTitle}\"")) +
                          ". Their text would fold into the previous section, so nothing was updated.");

        var shrunk = chunks
            .Where(c => currentContent.TryGetValue(c.SectionNumber, out var old)
                        && old.Length >= ShrinkCheckMinChars && c.Content.Length < old.Length * MinShrinkRatio)
            .Select(c => $"[{c.SectionNumber}] {currentContent[c.SectionNumber].Length} → {c.Content.Length} chars")
            .ToList();
        if (shrunk.Count > 0)
            return Review("Section(s) shrank sharply, which usually means a heading matched in the wrong place: " +
                          string.Join("; ", shrunk) + ".");

        var changed = chunks
            .Where(c => !currentContent.TryGetValue(c.SectionNumber, out var old) || old != c.Content)
            .ToList();
        if (changed.Count == 0)
            return new RecrawlPlan(RecrawlDecision.Unchanged, changed, "No changes.");

        if (chunks.Count >= 5 && changed.Count > chunks.Count * MaxChangedShare)
            return Review($"{changed.Count} of {chunks.Count} sections changed at once — too many to apply automatically.");

        return new RecrawlPlan(RecrawlDecision.Apply, changed,
            $"{changed.Count} section(s) changed: " + string.Join(", ", changed.Select(c => $"[{c.SectionNumber}] {c.SectionTitle}")));
    }

    private static RecrawlPlan Review(string reason) => new(RecrawlDecision.NeedsReview, Array.Empty<ChunkResult>(), reason);
}

/// <summary>
/// Scheduled re-crawl with staleness detection. Once a night (Arcl:Recrawl:HourLocal, server time)
/// it re-fetches every configured arcl.org page, chunks it with the same code as the ingest tool
/// (shared/Arcl.Ingestion), and compares against rule_chunks:
///   - unchanged → just records the check;
///   - changed, and RecrawlPlanner trusts it → re-embeds ONLY the changed sections and versions
///     them (old row closed, new row effective today), then emails admins what changed — including
///     any decision tree built on a changed section, since those are hand-written and won't update
///     themselves;
///   - anything suspicious → changes nothing and emails admins to check with `ingest --dry-run`.
/// Every outcome is recorded in dbo.source_pages; MonitoringService alerts when a page hasn't been
/// successfully checked for Arcl:Recrawl:StaleAfterDays.
///
/// If the API was down at the scheduled hour, it catches up shortly after starting. Admins can
/// also trigger a run from the admin page (POST /api/admin/recrawl).
/// </summary>
public sealed class RecrawlService : BackgroundService
{
    private readonly string _connectionString;
    private readonly IServiceScopeFactory _scopes;
    private readonly AlertService _alerts;
    private readonly RetrievalService _retrieval;
    private readonly ILogger<RecrawlService> _logger;
    private readonly bool _enabled;
    private readonly int _hourLocal;
    private readonly IReadOnlyList<RecrawlSource> _sources;
    private readonly string _anchorsDirectory;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public RecrawlService(IConfiguration config, IServiceScopeFactory scopes, AlertService alerts, RetrievalService retrieval,
        ILogger<RecrawlService> logger)
    {
        _connectionString = config.GetNonEmpty("Arcl:ConnectionString")
            ?? Environment.GetEnvironmentVariable("ARCL_DB_CONNECTION")
            ?? throw new InvalidOperationException("Set Arcl:ConnectionString (appsettings) or ARCL_DB_CONNECTION (env var).");
        _scopes = scopes;
        _alerts = alerts;
        _retrieval = retrieval;
        _logger = logger;
        _enabled = config.GetValue<bool?>("Arcl:Recrawl:Enabled") ?? true;
        _hourLocal = config.GetValue<int?>("Arcl:Recrawl:HourLocal") ?? 3;
        _sources = config.GetSection("Arcl:Recrawl:Sources").Get<List<RecrawlSource>>() ?? new List<RecrawlSource>();
        // ingest/SectionAnchors/*.json are linked into the API's output (see ArclChatbot.Api.csproj).
        _anchorsDirectory = Path.Combine(AppContext.BaseDirectory, "SectionAnchors");
    }

    public IReadOnlyList<RecrawlSource> Sources => _sources;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled || _sources.Count == 0)
        {
            _logger.LogInformation("Scheduled re-crawl is disabled (Arcl:Recrawl).");
            return;
        }

        try
        {
            // Catch up if the last check is overdue (e.g. the server was off at the scheduled hour).
            // A short delay first so startup itself isn't slowed down.
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            if (await IsOverdueAsync(stoppingToken))
                await RunAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(UntilNextRun(DateTime.Now), stoppingToken);
                await RunAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    internal TimeSpan UntilNextRun(DateTime nowLocal)
    {
        var next = nowLocal.Date.AddHours(_hourLocal);
        if (next <= nowLocal) next = next.AddDays(1);
        return next - nowLocal;
    }

    /// <summary>Runs every source once. Concurrent calls (scheduled + admin-triggered) don't overlap:
    /// the second one waits for the first.</summary>
    public async Task<IReadOnlyList<RecrawlResult>> RunAsync(CancellationToken ct)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            var results = new List<RecrawlResult>();
            foreach (var source in _sources)
            {
                RecrawlResult result;
                try
                {
                    result = await RecrawlOneAsync(source, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Re-crawl failed for {League} {Url}.", source.League, source.Url);
                    result = new RecrawlResult(source.League, source.Url, "fetch_failed", ex.Message, 0);
                }
                await RecordAsync(result, ct);
                results.Add(result);
            }

            if (results.Any(r => r.Status == "updated")) _retrieval.InvalidateCache();
            _logger.LogInformation("Re-crawl finished: {Summary}.",
                string.Join(", ", results.GroupBy(r => r.Status).Select(g => $"{g.Count()} {g.Key}")));
            return results;
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<RecrawlResult> RecrawlOneAsync(RecrawlSource source, CancellationToken ct)
    {
        var anchors = JsonSerializer.Deserialize<List<SectionAnchor>>(
            await File.ReadAllTextAsync(Path.Combine(_anchorsDirectory, source.Anchors), ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"No anchors in {source.Anchors}.");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var html = await RulebookChunker.FetchHtmlBytesAsync(source.Url);
        var (chunks, notFound) = RulebookChunker.ChunkByAnchors(html, anchors, source.League, source.Url, today);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var current = await LoadCurrentAsync(conn, source, ct);

        var plan = RecrawlPlanner.Plan(chunks, notFound, current.ToDictionary(kv => kv.Key, kv => kv.Value.Content));
        var leagueName = LeagueExtensions.FromDbValue(source.League).ToDisplayName();

        switch (plan.Decision)
        {
            case RecrawlDecision.Unchanged:
                return new RecrawlResult(source.League, source.Url, "unchanged", plan.Reason, chunks.Count);

            case RecrawlDecision.NeedsReview:
                await _alerts.RaiseAsync($"recrawl-review-{source.League}-{source.Url}", "warning",
                    $"{leagueName}: arcl.org page changed but couldn't be updated automatically",
                    $"Page: {source.Url}\nLeague: {leagueName}\n\n{plan.Reason}\n\n" +
                    "Nothing in the chatbot's rule text was changed. To review, run from the ingest folder:\n" +
                    $"  dotnet run -- --league {source.League} --url {source.Url} --anchors SectionAnchors/{source.Anchors} " +
                    $"--effective-start {today:yyyy-MM-dd} --dry-run\n" +
                    "fix SectionAnchors if needed, then re-run without --dry-run.",
                    TimeSpan.FromHours(24), ct);
                return new RecrawlResult(source.League, source.Url, "needs_review", plan.Reason, chunks.Count);
        }

        // ---- apply: embed + version only the changed sections
        using var scope = _scopes.CreateScope();
        var gemini = scope.ServiceProvider.GetRequiredService<GeminiClient>(); // same embedding model as retrieval
        foreach (var chunk in plan.Changed)
        {
            SqlVector<float>? embedding = string.IsNullOrWhiteSpace(chunk.Content)
                ? null
                : new SqlVector<float>(await gemini.EmbedAsync(chunk.Content, ct));
            await ChunkStore.UpsertChunkAsync(conn, chunk, embedding);
        }

        var replacedIds = plan.Changed
            .Where(c => current.ContainsKey(c.SectionNumber))
            .Select(c => current[c.SectionNumber].ChunkId)
            .ToList();
        var affectedTrees = await GetDecisionTreesUsingAsync(conn, replacedIds, ct);

        var body = $"Page: {source.Url}\nLeague: {leagueName}\n\n{plan.Reason}\n\n" +
                   "The chatbot now answers from the new text (old versions are kept, closed out as of today).";
        if (affectedTrees.Count > 0)
            body += "\n\nDECISION TREES TO REVIEW — these step-by-step flows were written from a section that just " +
                    "changed and do not update themselves:\n" + string.Join("\n", affectedTrees.Select(t => "  - " + t));

        await _alerts.RaiseAsync($"recrawl-updated-{source.League}-{source.Url}-{today:yyyyMMdd}",
            affectedTrees.Count > 0 ? "warning" : "info",
            $"{leagueName}: rulebook updated ({plan.Changed.Count} section(s))", body, TimeSpan.FromHours(1), ct);

        return new RecrawlResult(source.League, source.Url, "updated", plan.Reason, chunks.Count);
    }

    private static async Task<Dictionary<string, (Guid ChunkId, string Content)>> LoadCurrentAsync(
        SqlConnection conn, RecrawlSource source, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            """
            SELECT section_number, chunk_id, content
            FROM dbo.rule_chunks
            WHERE league = @league AND source_url = @url AND effective_end IS NULL
            """, conn);
        cmd.Parameters.AddWithValue("@league", source.League);
        cmd.Parameters.AddWithValue("@url", source.Url);

        var result = new Dictionary<string, (Guid, string)>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)] = (reader.GetGuid(1), reader.GetString(2));
        return result;
    }

    private static async Task<List<string>> GetDecisionTreesUsingAsync(SqlConnection conn, IReadOnlyList<Guid> chunkIds, CancellationToken ct)
    {
        if (chunkIds.Count == 0) return new List<string>();

        await using var cmd = new SqlCommand { Connection = conn };
        var names = new List<string>();
        for (var i = 0; i < chunkIds.Count; i++)
        {
            cmd.Parameters.AddWithValue($"@c{i}", chunkIds[i]);
            names.Add($"@c{i}");
        }
        cmd.CommandText = $"""
            SELECT DISTINCT league, ISNULL(topic, '(step)') AS topic, question_text
            FROM dbo.decision_tree_nodes
            WHERE source_chunk_id IN ({string.Join(", ", names)})
            """;

        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add($"{reader.GetString(0)} / {reader.GetString(1)}: \"{reader.GetString(2)}\"");
        return result;
    }

    private async Task RecordAsync(RecrawlResult r, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            """
            MERGE dbo.source_pages AS t
            USING (SELECT @league AS league, @url AS url) AS s ON t.league = s.league AND t.url = s.url
            WHEN NOT MATCHED THEN INSERT (league, url) VALUES (s.league, s.url);

            UPDATE dbo.source_pages SET
                last_checked_at  = SYSUTCDATETIME(),
                last_success_at  = CASE WHEN @status IN ('unchanged', 'updated') THEN SYSUTCDATETIME() ELSE last_success_at END,
                last_changed_at  = CASE WHEN @status = 'updated' THEN SYSUTCDATETIME() ELSE last_changed_at END,
                last_status      = @status,
                last_message     = @message,
                sections_matched = @matched
            WHERE league = @league AND url = @url;
            """, conn);
        cmd.Parameters.AddWithValue("@league", r.League);
        cmd.Parameters.AddWithValue("@url", r.Url);
        cmd.Parameters.AddWithValue("@status", r.Status);
        cmd.Parameters.AddWithValue("@message", r.Message.Length > 2000 ? r.Message[..2000] : r.Message);
        cmd.Parameters.AddWithValue("@matched", r.SectionsMatched);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<bool> IsOverdueAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT COUNT(*), MIN(last_checked_at) FROM dbo.source_pages", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var count = reader.GetInt32(0);
        var oldest = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1);
        return count < _sources.Count || oldest is null || DateTime.UtcNow - oldest.Value > TimeSpan.FromHours(26);
    }
}
