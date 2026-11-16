using Microsoft.Data.SqlClient;

namespace Arcl.Api.Services;

/// <summary>Retrieval-quality numbers for one time window, over FAQ turns that went through the
/// confidence gate (the ones with a best_distance).</summary>
public sealed record WindowStats(int Turns, double? AvgBestDistance, double LowShare);

/// <summary>
/// Similarity-score drift: compares the last 7 days against the 28 days before them. Two signals:
///   - average best_distance rising — questions landing further from the rule text than they used
///     to (new topics people ask about, or content that went missing/stale);
///   - the share of turns ending low-confidence/out-of-scope rising — more visitors getting no answer.
/// Needs a minimum sample in both windows; with less traffic than that, noise would dominate.
/// Pure logic, so it's unit tested; MonitoringService feeds it numbers from conversation_log.
/// </summary>
public static class DriftCheck
{
    internal const int MinTurns = 30;
    internal const double DistanceRise = 0.03;
    internal const double LowShareRise = 0.15;

    /// <returns>A human-readable reason when drift is detected, otherwise null.</returns>
    public static string? Evaluate(WindowStats recent, WindowStats baseline)
    {
        if (recent.Turns < MinTurns || baseline.Turns < MinTurns) return null;

        var reasons = new List<string>();
        if (recent.AvgBestDistance is double r && baseline.AvgBestDistance is double b && r - b >= DistanceRise)
            reasons.Add($"average best distance rose from {b:F3} to {r:F3}");
        if (recent.LowShare - baseline.LowShare >= LowShareRise)
            reasons.Add($"turns with no confident answer rose from {baseline.LowShare:P0} to {recent.LowShare:P0}");

        return reasons.Count == 0 ? null : string.Join("; ", reasons);
    }
}

/// <summary>Counts unhandled request exceptions (the middleware in Program.cs increments it) so
/// MonitoringService can alert on crashes that never reach conversation_log.</summary>
public sealed class ExceptionCounter
{
    private int _count;
    public void Increment() => Interlocked.Increment(ref _count);
    public int TakeCount() => Interlocked.Exchange(ref _count, 0);
}

/// <summary>
/// Error logging + alerting, drift monitoring, budget warnings and stale-content checks. Runs every
/// Arcl:Monitoring:IntervalMinutes (15) and raises alerts through AlertService, whose per-key
/// cooldowns keep an ongoing problem down to one email:
///
///   errors   — failed chat turns ("[error]" rows) and unhandled exceptions in the last hour
///   drift    — DriftCheck over best_distance (daily at most)
///   budget   — month-to-date Gemini spend at WarnAtPercent, and at 100% (once each per month)
///   stale    — a crawled arcl.org page not successfully checked for Arcl:Recrawl:StaleAfterDays
/// </summary>
public sealed class MonitoringService : BackgroundService
{
    private readonly string _connectionString;
    private readonly AlertService _alerts;
    private readonly BudgetGovernor _budget;
    private readonly ExceptionCounter _exceptions;
    private readonly ILogger<MonitoringService> _logger;
    private readonly TimeSpan _interval;
    private readonly int _errorThreshold;
    private readonly int _staleAfterDays;
    private readonly bool _recrawlEnabled;

    private int _unhandledInWindow;

    public MonitoringService(IConfiguration config, AlertService alerts, BudgetGovernor budget, ExceptionCounter exceptions,
        ILogger<MonitoringService> logger)
    {
        _connectionString = config.GetNonEmpty("Arcl:ConnectionString")
            ?? Environment.GetEnvironmentVariable("ARCL_DB_CONNECTION")
            ?? throw new InvalidOperationException("Set Arcl:ConnectionString (appsettings) or ARCL_DB_CONNECTION (env var).");
        _alerts = alerts;
        _budget = budget;
        _exceptions = exceptions;
        _logger = logger;
        _interval = TimeSpan.FromMinutes(config.GetValue<int?>("Arcl:Monitoring:IntervalMinutes") ?? 15);
        _errorThreshold = config.GetValue<int?>("Arcl:Monitoring:ErrorsPerHourThreshold") ?? 5;
        _staleAfterDays = config.GetValue<int?>("Arcl:Recrawl:StaleAfterDays") ?? 3;
        _recrawlEnabled = config.GetValue<bool?>("Arcl:Recrawl:Enabled") ?? true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                await RunChecksAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    /// <summary>One pass of every check. Each check is isolated — one failing (e.g. the DB is
    /// briefly unreachable) doesn't stop the others.</summary>
    public async Task RunChecksAsync(CancellationToken ct)
    {
        await Guard("errors", () => CheckErrorsAsync(ct));
        await Guard("budget", () => CheckBudgetAsync(ct));
        await Guard("drift", () => CheckDriftAsync(ct));
        if (_recrawlEnabled) await Guard("stale content", () => CheckStaleAsync(ct));

        async Task Guard(string name, Func<Task> check)
        {
            try { await check(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Monitoring check '{Check}' failed.", name);
            }
        }
    }

    private async Task CheckErrorsAsync(CancellationToken ct)
    {
        // Unhandled exceptions accumulate across cycles within the hour-long cooldown window.
        _unhandledInWindow += _exceptions.TakeCount();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            """
            SELECT COUNT(*),
                   ISNULL(SUM(CASE WHEN response_text LIKE '[[]error]%' THEN 1 ELSE 0 END), 0)
            FROM dbo.conversation_log
            WHERE created_at >= DATEADD(HOUR, -1, SYSUTCDATETIME())
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var total = reader.GetInt32(0);
        var failed = reader.GetInt32(1);

        var tooMany = failed >= _errorThreshold || (total >= 10 && failed >= total * 0.2) || _unhandledInWindow >= _errorThreshold;
        if (!tooMany) return;

        var raised = await _alerts.RaiseAsync("errors", "critical", "Chat errors spiking",
            $"In the last hour: {failed} of {total} chat turns failed on our side (visitors saw \"something went wrong\"), " +
            $"and {_unhandledInWindow} request(s) crashed with an unhandled exception.\n\n" +
            "Most likely causes: the Gemini API is down or the key/quota is exhausted, or the database is unreachable. " +
            "The API log has the exception details (search for \"Chat turn failed\").",
            TimeSpan.FromHours(2), ct);
        if (raised) _unhandledInWindow = 0;
    }

    private async Task CheckBudgetAsync(CancellationToken ct)
    {
        var state = await _budget.GetStateAsync(ct);
        var month = DateTime.UtcNow.ToString("yyyy-MM");
        var spent = $"${state.SpentUsd:F2} of ${state.CapUsd:F2} ({state.PercentUsed:F0}%)";

        if (state.Exhausted)
        {
            await _alerts.RaiseAsync($"budget-100-{month}", "critical", "Monthly Gemini budget reached — written answers paused",
                $"Month-to-date spend: {spent}.\n\nUntil the month rolls over (UTC), the bot answers with the best-matching " +
                "rule sections instead of written answers. To resume now, raise Arcl:Budget:MonthlyUsd and restart the API.",
                TimeSpan.FromDays(40), ct);
        }
        else if (state.Warning)
        {
            await _alerts.RaiseAsync($"budget-{state.WarnPercent}-{month}", "warning", $"Gemini budget {state.PercentUsed:F0}% used",
                $"Month-to-date spend: {spent}.\n\nAt 100% the bot switches to showing rule sections instead of written answers " +
                "until next month. Raise Arcl:Budget:MonthlyUsd if that's not what you want.",
                TimeSpan.FromDays(40), ct);
        }
    }

    private async Task CheckDriftAsync(CancellationToken ct)
    {
        var recent = await GetWindowStatsAsync(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow, ct);
        var baseline = await GetWindowStatsAsync(DateTime.UtcNow.AddDays(-35), DateTime.UtcNow.AddDays(-7), ct);
        var reason = DriftCheck.Evaluate(recent, baseline);
        if (reason is null) return;

        await _alerts.RaiseAsync("drift", "warning", "Answer quality drifting",
            $"Compared with the previous four weeks, over the last 7 days {reason} " +
            $"({recent.Turns} recent turns vs {baseline.Turns} baseline).\n\n" +
            "Usually this means people are asking about something the rulebook text doesn't cover (check \"Top unanswered\" " +
            "and the review queue on the admin page), or a crawled page lost content (check \"Content freshness\").",
            TimeSpan.FromDays(1), ct);
    }

    internal async Task<WindowStats> GetWindowStatsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            """
            SELECT COUNT(*),
                   AVG(best_distance),
                   ISNULL(AVG(CASE WHEN confidence_tier IN ('low', 'out_of_scope') THEN 1.0 ELSE 0.0 END), 0)
            FROM dbo.conversation_log
            WHERE best_distance IS NOT NULL AND created_at >= @from AND created_at < @to
            """, conn);
        cmd.Parameters.AddWithValue("@from", fromUtc);
        cmd.Parameters.AddWithValue("@to", toUtc);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new WindowStats(reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetDouble(1), (double)reader.GetDecimal(2));
    }

    private async Task CheckStaleAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            """
            SELECT league, url, last_success_at, last_status, last_message
            FROM dbo.source_pages
            WHERE last_checked_at IS NOT NULL
              AND (last_success_at IS NULL OR last_success_at < DATEADD(DAY, -@days, SYSUTCDATETIME()))
            """, conn);
        cmd.Parameters.AddWithValue("@days", _staleAfterDays);

        var stale = new List<(string League, string Url, DateTime? LastSuccess, string? Status, string? Message)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                stale.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        foreach (var s in stale)
        {
            await _alerts.RaiseAsync($"stale-{s.League}-{s.Url}", "warning", $"Rule content may be stale: {s.League}",
                $"{s.Url} hasn't been successfully re-checked " +
                (s.LastSuccess is DateTime d ? $"since {d:yyyy-MM-dd HH:mm} UTC" : "since it was first tracked") +
                $" (more than {_staleAfterDays} days). Last result: {s.Status ?? "unknown"} — {s.Message}\n\n" +
                "The bot keeps answering from the last good copy, but if the rulebook changed in the meantime its answers may be out of date.",
                TimeSpan.FromDays(1), ct);
        }
    }
}
