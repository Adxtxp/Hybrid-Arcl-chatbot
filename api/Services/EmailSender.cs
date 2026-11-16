using System.Net;
using System.Net.Mail;

namespace Arcl.Api.Services;

/// <summary>
/// Sends admin emails (alerts, contact-form notifications) over SMTP — Arcl:Email:*.
///
/// With no Arcl:Email:SmtpHost configured, messages are written as .eml files into
/// Arcl:Email:PickupDirectory (default "outbox/" under the API's folder) instead of being sent —
/// so alerting can be developed and checked locally without a mail server, and a missing SMTP
/// setup in production is visible (a growing outbox folder, plus a startup warning) rather than
/// silently dropping alerts. Open any .eml file in Outlook/Thunderbird to read it.
///
/// Gmail works with an app password: SmtpHost smtp.gmail.com, port 587, StartTls true.
/// The password is a secret — user-secrets or the ARCL_SMTP_PASSWORD env var, never appsettings.json.
/// </summary>
public sealed class EmailSender
{
    private readonly ILogger<EmailSender> _logger;
    private readonly string? _host;
    private readonly int _port;
    private readonly bool _startTls;
    private readonly string? _username;
    private readonly string? _password;
    private readonly string _from;
    private readonly string _pickupDirectory;

    public IReadOnlyList<string> AdminRecipients { get; }
    public bool IsSmtpConfigured => _host is not null;

    public EmailSender(IConfiguration config, IHostEnvironment env, ILogger<EmailSender> logger)
    {
        _logger = logger;
        _host = config.GetNonEmpty("Arcl:Email:SmtpHost");
        _port = config.GetValue<int?>("Arcl:Email:SmtpPort") ?? 587;
        _startTls = config.GetValue<bool?>("Arcl:Email:StartTls") ?? true;
        _username = config.GetNonEmpty("Arcl:Email:Username");
        _password = config.GetNonEmpty("Arcl:Email:Password") ?? Environment.GetEnvironmentVariable("ARCL_SMTP_PASSWORD");

        var supportContact = config.GetNonEmpty("Arcl:SupportContact") ?? "";
        _from = config.GetNonEmpty("Arcl:Email:From") ?? _username ?? supportContact;
        var recipients = config.GetSection("Arcl:Email:AdminRecipients").Get<string[]>();
        AdminRecipients = recipients is { Length: > 0 } ? recipients : new[] { supportContact };

        var pickup = config.GetNonEmpty("Arcl:Email:PickupDirectory") ?? "outbox";
        _pickupDirectory = Path.IsPathRooted(pickup) ? pickup : Path.Combine(env.ContentRootPath, pickup);

        if (_host is null)
            logger.LogWarning("Arcl:Email:SmtpHost is not set — admin emails will be written to {Dir} instead of sent.", _pickupDirectory);
    }

    /// <summary>Sends to the admin recipients. replyTo: e.g. the visitor's address on a contact-form
    /// notification, so the admin can just hit Reply. Throws on failure — callers decide whether
    /// that matters (AlertService records it; contact-form notifications just log it).</summary>
    public async Task SendToAdminsAsync(string subject, string body, string? replyTo = null, CancellationToken ct = default)
    {
        using var message = new MailMessage { From = new MailAddress(_from, "ARCL Rules Assistant"), Subject = subject, Body = body };
        foreach (var to in AdminRecipients) message.To.Add(to);
        if (!string.IsNullOrWhiteSpace(replyTo)) message.ReplyToList.Add(replyTo);

        using var client = new SmtpClient();
        if (_host is null)
        {
            Directory.CreateDirectory(_pickupDirectory);
            client.DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory;
            client.PickupDirectoryLocation = _pickupDirectory;
        }
        else
        {
            client.Host = _host;
            client.Port = _port;
            client.EnableSsl = _startTls;
            if (_username is not null)
                client.Credentials = new NetworkCredential(_username, _password);
        }

        await client.SendMailAsync(message, ct);
    }
}
