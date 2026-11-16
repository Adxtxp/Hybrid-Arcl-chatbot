using Microsoft.Data.SqlClient;

namespace Arcl.Api.Services;

public sealed record BudgetState(decimal SpentUsd, decimal CapUsd, int WarnPercent)
{
    public double PercentUsed => CapUsd <= 0 ? 0 : (double)(SpentUsd / CapUsd * 100m);
    /// <summary>Cap reached: stop calling the chat model until the month rolls over.
    /// A cap of 0 or less means "no cap".</summary>
    public bool Exhausted => CapUsd > 0 && SpentUsd >= CapUsd;
    public bool Warning => CapUsd > 0 && PercentUsed >= WarnPercent;
}

/// <summary>
/// The cost governor: a monthly cap on Gemini chat spend (Arcl:Budget:MonthlyUsd). Once
/// month-to-date spend reaches it, ChatOrchestrator stops calling the chat model and answers with
/// the best-matching rule sections directly — still useful, just not summarized — until the
/// calendar month (UTC) rolls over. MonitoringService emails admins at WarnAtPercent and at 100%.
///
/// Spend is read from conversation_log.token_cost_usd, which is GeminiClient's estimate from its
/// configured per-token prices — keep those prices current or the cap is only as good as they are.
/// Embedding calls (retrieval + re-crawl) aren't counted; at this corpus size they're a rounding
/// error next to chat tokens. Cached for a minute so it isn't a DB query on every chat turn.
/// </summary>
public sealed class BudgetGovernor
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    private readonly string _connectionString;
    private readonly decimal _capUsd;
    private readonly int _warnPercent;

    private BudgetState? _cached;
    private DateTime _cachedAt;

    public BudgetGovernor(IConfiguration config)
    {
        _connectionString = config.GetNonEmpty("Arcl:ConnectionString")
            ?? Environment.GetEnvironmentVariable("ARCL_DB_CONNECTION")
            ?? throw new InvalidOperationException("Set Arcl:ConnectionString (appsettings) or ARCL_DB_CONNECTION (env var).");
        _capUsd = config.GetValue<decimal?>("Arcl:Budget:MonthlyUsd") ?? 10m;
        _warnPercent = config.GetValue<int?>("Arcl:Budget:WarnAtPercent") ?? 80;
    }

    public async Task<BudgetState> GetStateAsync(CancellationToken ct = default)
    {
        var cached = _cached;
        if (cached is not null && DateTime.UtcNow - _cachedAt < CacheFor) return cached;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            """
            SELECT ISNULL(SUM(token_cost_usd), 0)
            FROM dbo.conversation_log
            WHERE created_at >= DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1)
            """, conn);
        var spent = Convert.ToDecimal(await cmd.ExecuteScalarAsync(ct));

        var state = new BudgetState(spent, _capUsd, _warnPercent);
        _cached = state;
        _cachedAt = DateTime.UtcNow;
        return state;
    }
}
