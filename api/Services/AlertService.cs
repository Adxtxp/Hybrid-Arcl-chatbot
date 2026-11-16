using Microsoft.Data.SqlClient;

namespace Arcl.Api.Services;

public sealed record AlertItem(long AlertId, string AlertKey, string Severity, string Subject, string Body, bool Emailed,
    string? EmailError, DateTime CreatedAt);

/// <summary>
/// Raises alerts: records each one in dbo.alerts (sql/009), then emails the admins. Recorded
/// first, emailed second — if the email fails, the alert still shows on the admin page banner
/// with the error next to it, so a broken mail setup can't hide a real problem.
///
/// Every alert has a key and a cooldown: the same key won't fire again until its cooldown has
/// passed (checked against the last row for that key), so an ongoing problem produces one email,
/// not one per monitoring cycle.
/// </summary>
public sealed class AlertService
{
    private readonly string _connectionString;
    private readonly EmailSender _email;
    private readonly ILogger<AlertService> _logger;

    public AlertService(IConfiguration config, EmailSender email, ILogger<AlertService> logger)
    {
        _connectionString = config.GetNonEmpty("Arcl:ConnectionString")
            ?? Environment.GetEnvironmentVariable("ARCL_DB_CONNECTION")
            ?? throw new InvalidOperationException("Set Arcl:ConnectionString (appsettings) or ARCL_DB_CONNECTION (env var).");
        _email = email;
        _logger = logger;
    }

    /// <returns>true if the alert was raised; false if it was suppressed by its cooldown.</returns>
    public async Task<bool> RaiseAsync(string key, string severity, string subject, string body, TimeSpan cooldown, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using (var check = new SqlCommand(
            "SELECT TOP 1 created_at FROM dbo.alerts WHERE alert_key = @key ORDER BY created_at DESC", conn))
        {
            check.Parameters.AddWithValue("@key", key);
            if (await check.ExecuteScalarAsync(ct) is DateTime last && DateTime.UtcNow - last < cooldown)
                return false;
        }

        long alertId;
        await using (var insert = new SqlCommand(
            """
            INSERT INTO dbo.alerts (alert_key, severity, subject, body)
            OUTPUT INSERTED.alert_id
            VALUES (@key, @severity, @subject, @body)
            """, conn))
        {
            insert.Parameters.AddWithValue("@key", key);
            insert.Parameters.AddWithValue("@severity", severity);
            insert.Parameters.AddWithValue("@subject", subject);
            insert.Parameters.AddWithValue("@body", body);
            alertId = (long)(await insert.ExecuteScalarAsync(ct))!;
        }

        _logger.LogWarning("ALERT [{Severity}] {Subject}", severity, subject);

        string? error = null;
        try
        {
            await _email.SendToAdminsAsync($"[ARCL bot · {severity}] {subject}", body + Footer, ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
            _logger.LogError(ex, "Could not email alert {AlertId}.", alertId);
        }

        await using var update = new SqlCommand(
            "UPDATE dbo.alerts SET emailed = @emailed, email_error = @error WHERE alert_id = @id", conn);
        update.Parameters.AddWithValue("@emailed", error is null);
        update.Parameters.AddWithValue("@error", (object?)(error is { Length: > 1000 } ? error[..1000] : error) ?? DBNull.Value);
        update.Parameters.AddWithValue("@id", alertId);
        await update.ExecuteNonQueryAsync(ct);
        return true;
    }

    /// <summary>Unacknowledged alerts from the last 14 days — the admin page banner.</summary>
    public async Task<IReadOnlyList<AlertItem>> GetOpenAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            """
            SELECT alert_id, alert_key, severity, subject, body, emailed, email_error, created_at
            FROM dbo.alerts
            WHERE acknowledged_at IS NULL AND created_at >= DATEADD(DAY, -14, SYSUTCDATETIME())
            ORDER BY created_at DESC
            """, conn);

        var items = new List<AlertItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(new AlertItem(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetBoolean(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetDateTime(7)));
        return items;
    }

    public async Task AcknowledgeAsync(long alertId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "UPDATE dbo.alerts SET acknowledged_at = SYSUTCDATETIME() WHERE alert_id = @id AND acknowledged_at IS NULL", conn);
        cmd.Parameters.AddWithValue("@id", alertId);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0)
            throw new KeyNotFoundException($"No open alert {alertId}.");
    }

    private const string Footer = "\n\n—\nSent by the ARCL Rules Assistant. Open alerts are also listed on the admin page (admin.html).";
}
