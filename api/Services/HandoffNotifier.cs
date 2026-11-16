using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Arcl.Api.Models;

namespace Arcl.Api.Services;

/// <summary>
/// Emails the admins about each contact-admin request (sql/010_handoff_email_delivery.sql).
/// The endpoint calls <see cref="SendAsync"/> right after saving a request; this service also
/// sweeps every minute for requests still unsent — the API restarted mid-send, Gmail was briefly
/// down — so an email is only ever delayed, never lost. A request is "claimed" before sending so
/// the immediate send and the sweep can't both send it.
/// </summary>
public sealed class HandoffNotifier : BackgroundService
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
    // A claim older than this belongs to a sender that died (e.g. the restart that lost the email).
    private const int StaleClaimMinutes = 2;

    private readonly string _connectionString;
    private readonly EmailSender _email;
    private readonly ConversationLogger _log;
    private readonly SessionService _sessions;
    private readonly AlertService _alerts;
    private readonly ILogger<HandoffNotifier> _logger;

    public HandoffNotifier(IConfiguration config, EmailSender email, ConversationLogger log, SessionService sessions,
        AlertService alerts, ILogger<HandoffNotifier> logger)
    {
        _connectionString = config.GetNonEmpty("Arcl:ConnectionString")
            ?? Environment.GetEnvironmentVariable("ARCL_DB_CONNECTION")
            ?? throw new InvalidOperationException("Set Arcl:ConnectionString (appsettings) or ARCL_DB_CONNECTION (env var).");
        _email = email;
        _log = log;
        _sessions = sessions;
        _alerts = alerts;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            do
            {
                try { await SendUnsentAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Handoff email sweep failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down — anything unsent is picked up by the next start's first sweep
        }
    }

    private async Task SendUnsentAsync(CancellationToken ct)
    {
        var pending = new List<long>();
        await using (var conn = new SqlConnection(_connectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(
                """
                SELECT request_id FROM dbo.handoff_requests
                WHERE admin_emailed_at IS NULL AND email_attempts < @max
                  AND (email_claimed_at IS NULL OR email_claimed_at < DATEADD(MINUTE, -@stale, SYSUTCDATETIME()))
                ORDER BY created_at
                """, conn);
            cmd.Parameters.AddWithValue("@max", MaxAttempts);
            cmd.Parameters.AddWithValue("@stale", StaleClaimMinutes);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) pending.Add(reader.GetInt64(0));
        }
        foreach (var id in pending)
            await SendAsync(id, ct);
    }

    /// <summary>Sends the admin email for one request, unless it's already sent or another sender
    /// holds it. Never throws for a send failure — that's recorded and retried by the sweep.</summary>
    public async Task SendAsync(long requestId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Claim it and read what we need in one statement.
        await using var claim = new SqlCommand(
            """
            UPDATE dbo.handoff_requests
            SET email_claimed_at = SYSUTCDATETIME(), email_attempts = email_attempts + 1
            OUTPUT inserted.log_id, inserted.session_id, inserted.contact_name, inserted.contact_email,
                   inserted.message, inserted.email_attempts
            WHERE request_id = @id AND admin_emailed_at IS NULL AND email_attempts < @max
              AND (email_claimed_at IS NULL OR email_claimed_at < DATEADD(MINUTE, -@stale, SYSUTCDATETIME()))
            """, conn);
        claim.Parameters.AddWithValue("@id", requestId);
        claim.Parameters.AddWithValue("@max", MaxAttempts);
        claim.Parameters.AddWithValue("@stale", StaleClaimMinutes);

        long logId; Guid sessionId; string? name; string email, message; int attempt;
        await using (var reader = await claim.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return; // already sent, or someone else is on it
            logId = reader.GetInt64(0);
            sessionId = reader.GetGuid(1);
            name = reader.IsDBNull(2) ? null : reader.GetString(2);
            email = reader.GetString(3);
            message = reader.GetString(4);
            attempt = reader.GetInt32(5);
        }

        try
        {
            var transcript = await _log.GetTranscriptAsync(sessionId, 8, beforeLogId: logId, ct);
            var league = await _sessions.GetLeagueAsync(sessionId, ct);
            await _email.SendToAdminsAsync($"Help request from {name ?? email}",
                BuildBody(name, email, message, league, transcript), replyTo: email, ct);

            await using var done = new SqlCommand(
                "UPDATE dbo.handoff_requests SET admin_emailed_at = SYSUTCDATETIME(), email_claimed_at = NULL, email_error = NULL WHERE request_id = @id",
                conn);
            done.Parameters.AddWithValue("@id", requestId);
            await done.ExecuteNonQueryAsync(CancellationToken.None);
            _logger.LogInformation("Emailed admins about handoff request {RequestId}.", requestId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Couldn't email admins about handoff request {RequestId} (attempt {Attempt}/{Max}).",
                requestId, attempt, MaxAttempts);
            await using var failed = new SqlCommand(
                "UPDATE dbo.handoff_requests SET email_claimed_at = NULL, email_error = @error WHERE request_id = @id", conn);
            failed.Parameters.AddWithValue("@id", requestId);
            failed.Parameters.AddWithValue("@error", ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message);
            await failed.ExecuteNonQueryAsync(CancellationToken.None);

            if (attempt >= MaxAttempts)
                await _alerts.RaiseAsync($"handoff-email-{requestId}", "critical",
                    $"Couldn't email admins about a help request from {email}",
                    $"Gave up after {MaxAttempts} attempts. Last error: {ex.Message}\n\nThe request is still in the admin " +
                    "page's review queue — reply to the visitor from there.", TimeSpan.FromDays(1), CancellationToken.None);
        }
    }

    internal static string BuildBody(string? name, string email, string message, League? league,
        IReadOnlyList<(string Question, string? Answer)> transcript)
    {
        var body = new StringBuilder()
            .AppendLine($"{name ?? "A visitor"} <{email}> asked for help from a league admin" +
                        (league is null ? "." : $" ({league.Value.ToDisplayName()} league)."))
            .AppendLine()
            .AppendLine("THEIR MESSAGE")
            .AppendLine(message)
            .AppendLine()
            .AppendLine("CONVERSATION WITH THE BOT (oldest first)");
        foreach (var (q, a) in transcript)
        {
            // "[§4]" is the chat UI's link syntax — in plain-text email, "(§4)" reads better.
            var answer = a is null ? "—" : Regex.Replace(a, @"\[§([^\]]+)\]", " (§$1)");
            body.AppendLine($"  Visitor: {q}").AppendLine($"  Bot:     {answer}").AppendLine();
        }
        body.AppendLine("Reply to this email to answer them directly. It's also in the admin page's review queue — mark it")
            .AppendLine("reviewed there once handled.");
        return body.ToString();
    }
}
