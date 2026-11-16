using System.Diagnostics;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Arcl.Api.Models;
using Arcl.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------- services

// League/RoutePath are serialized as strings ("Mens", not 0) so the frontend can send/receive
// them directly — see frontend/app.js, which posts { league: "Mens" } verbatim.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

builder.Services.AddHttpClient<GeminiClient>(); // typed client — provides GeminiClient's HttpClient via IHttpClientFactory
builder.Services.AddHttpClient<TeamRankingService>(); // ditto, for its arcl.org fetch
builder.Services.AddSingleton<IntentClassifier>();
builder.Services.AddSingleton<CrossEncoderReranker>();
builder.Services.AddSingleton<RetrievalService>();
builder.Services.AddSingleton<DecisionTreeService>();
builder.Services.AddSingleton<ConversationLogger>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<AdminService>();
builder.Services.AddSingleton<OutputFilter>();
builder.Services.AddSingleton<SuggestionService>();

// Phase 3: operations. The background services are registered as singletons first so admin
// endpoints can call them directly (POST /api/admin/recrawl), then also as hosted services.
builder.Services.AddSingleton<EmailSender>();
builder.Services.AddSingleton<AlertService>();
builder.Services.AddSingleton<BudgetGovernor>();
builder.Services.AddSingleton<ExceptionCounter>();
builder.Services.AddSingleton<RecrawlService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RecrawlService>());
builder.Services.AddSingleton<MonitoringService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MonitoringService>());
builder.Services.AddSingleton<HandoffNotifier>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HandoffNotifier>());
// Transient: it holds typed-HttpClient services (GeminiClient, TeamRankingService), which must
// not be captured by a singleton or they'd keep one HttpClient forever.
builder.Services.AddTransient<ChatOrchestrator>();

// Phase 5: admin surface auth. A single shared key, not real accounts — enough to keep
// conversation data and the review queue from being open to anyone who finds the URL, without
// building out a user/session system this project doesn't otherwise need. Secret, so it ships
// blank in appsettings.json on purpose (same as ConnectionString/GeminiApiKey) and must come from
// user-secrets or an env var.
var adminApiKey = builder.Configuration.GetNonEmpty("Arcl:AdminApiKey")
    ?? Environment.GetEnvironmentVariable("ARCL_ADMIN_API_KEY")
    ?? throw new InvalidOperationException("Set Arcl:AdminApiKey (appsettings) or ARCL_ADMIN_API_KEY (env var).");
var adminApiKeyBytes = Encoding.UTF8.GetBytes(adminApiKey);

var maxMessageLength = builder.Configuration.GetValue<int?>("Arcl:MaxMessageLength") ?? 1000;

// Phase 5: telemetry. Defaults to console output so this runs out of the box; set
// OpenTelemetry:OtlpEndpoint (appsettings or env var) to ship to Application Insights or a
// self-hosted collector instead — see README.md for both options.
var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
var serviceName = builder.Configuration["OpenTelemetry:ServiceName"] ?? "ArclChatbot.Api";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation();
        t.AddHttpClientInstrumentation(); // captures outbound Gemini calls' latency
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            t.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        else
            t.AddConsoleExporter();
    })
    .WithMetrics(m =>
    {
        m.AddAspNetCoreInstrumentation();
        m.AddHttpClientInstrumentation();
        m.AddRuntimeInstrumentation();
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            m.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        else
            m.AddConsoleExporter();
    });

// Phase 2: rate limiting, protects the Gemini budget from abuse on a public endpoint.
// Keyed per client IP so one bad actor can't exhaust the whole app's budget for everyone else.
var rateLimitPermits = builder.Configuration.GetValue<int?>("Arcl:RateLimitPermitsPerWindow") ?? 20;
var rateLimitWindowSeconds = builder.Configuration.GetValue<int?>("Arcl:RateLimitWindowSeconds") ?? 60;

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rateLimitPermits,
            Window = TimeSpan.FromSeconds(rateLimitWindowSeconds),
            QueueLimit = 0
        });
    });
});

// ---------------------------------------------------------------------- security (Phase 2)
// See README "Production hardening" for what's covered here vs. at deploy time (WAF, Key Vault).

// Behind a reverse proxy / load balancer (App Service, IIS, nginx) the connection's remote IP is
// the proxy's and the scheme is http — which would put every visitor in ONE rate-limit bucket
// and break the HTTPS redirect. Only trust X-Forwarded-* when explicitly told a proxy is in front;
// trusting them otherwise lets any client spoof its IP past the rate limiter.
var trustForwardedHeaders = builder.Configuration.GetValue<bool?>("Arcl:Security:TrustForwardedHeaders") ?? false;
if (trustForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownIPNetworks.Clear(); // the proxy's address isn't known ahead of time on most hosts
        o.KnownProxies.Clear();
    });
}

var allowedOrigins = builder.Configuration.GetSection("Arcl:Security:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
// Pages allowed to embed the chat in an <iframe> (widget.js on arcl.org) — CSP frame-ancestors.
var frameAncestors = builder.Configuration["Arcl:Security:FrameAncestors"] ?? "https://arcl.org https://*.arcl.org";
var requireHttps = builder.Configuration.GetValue<bool?>("Arcl:Security:RequireHttps") ?? true;

if (allowedOrigins.Length > 0)
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(allowedOrigins).AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

if (trustForwardedHeaders) app.UseForwardedHeaders();

// Unhandled exceptions are counted for MonitoringService's error alert (a crash that never
// reaches conversation_log would otherwise be invisible to it), then rethrown as normal.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        context.RequestServices.GetRequiredService<ExceptionCounter>().Increment();
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Unhandled")
            .LogError(ex, "Unhandled exception on {Method} {Path}.", context.Request.Method, context.Request.Path);
        throw;
    }
});

if (requireHttps && !app.Environment.IsDevelopment())
{
    app.UseHsts();
    // No-op (with a startup warning) when no HTTPS port is bound, e.g. plain `dotnet run` on
    // http://localhost:5000 — so local runs and the eval keep working unchanged.
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    var h = context.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    // Every script is a same-origin file (no inline <script>, no CDN). 'unsafe-inline' for styles
    // only, because admin.html and charts.js set a few inline style attributes.
    h["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        $"connect-src 'self'; frame-ancestors 'self' {frameAncestors}; base-uri 'self'; form-action 'self'; object-src 'none'";
    await next();
});

// Serves frontend/* (copied into wwwroot/ — see README) so the whole app is reachable from one
// origin/port: index.html, admin.html, widget-demo.html, widget.js, and every /api/* route all
// live behind this single address, which is what lets one link cover chat + admin + the embeddable
// widget for anyone testing it, with no separate static file server and no cross-origin API calls.
app.UseDefaultFiles();
// no-cache = "revalidate every time" (a cheap 304 when unchanged), not "don't cache". Without it,
// browsers keep a stale widget.js/app.js for hours after an update — and the file names never change.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

// Same-origin is all the bundled frontend needs (widget.js embeds via an iframe, not cross-origin
// fetches) — so CORS is off unless Arcl:Security:AllowedOrigins lists a separately-hosted frontend.
if (allowedOrigins.Length > 0) app.UseCors();
app.UseRateLimiter();

// Every /api/admin/* route needs a valid X-Admin-Key header. Checked here, once, before any
// admin endpoint runs, rather than per-endpoint — a new admin route added later gets this for
// free just by living under the path prefix. Constant-time comparison, so response timing can't
// be used to guess the key a character at a time.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/admin"))
    {
        var providedKey = context.Request.Headers["X-Admin-Key"].FirstOrDefault();
        if (providedKey is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(providedKey), adminApiKeyBytes))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid X-Admin-Key header." });
            return;
        }
    }
    await next();
});

// ---------------------------------------------------------------------- endpoints

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/config/public", (IConfiguration config) =>
    Results.Ok(new PublicConfigResponse(SupportContact(config), HandoffConsentText(config))));

app.MapPost("/api/session", async (CreateSessionRequest? req, SessionService sessions, CancellationToken ct) =>
{
    var sessionId = await sessions.CreateAsync(req?.League, ct);
    return Results.Ok(new SessionResponse(sessionId, req?.League));
});

app.MapPost("/api/session/{sessionId:guid}/league", async (Guid sessionId, SetLeagueRequest req, SessionService sessions, CancellationToken ct) =>
{
    try
    {
        await sessions.SetLeagueAsync(sessionId, req.League, ct);
        return Results.Ok(new SessionResponse(sessionId, req.League));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound(new { error = "Unknown session." });
    }
});

// Starter prompts for the session's league — see SuggestionService.cs.
app.MapGet("/api/suggestions", async (Guid sessionId, SessionService sessions, SuggestionService suggestions, CancellationToken ct) =>
{
    var league = await sessions.GetLeagueAsync(sessionId, ct);
    if (league is null) return Results.BadRequest(new { error = "Select a league for this session first." });
    return Results.Ok(new SuggestionsResponse(await suggestions.GetStartersAsync(league.Value, DateOnly.FromDateTime(DateTime.UtcNow), ct)));
});

// Plain JSON: waits for the whole turn. Kept for the eval harness and any non-browser client —
// same ChatOrchestrator, so both endpoints can never drift apart.
app.MapPost("/api/chat", async (ChatRequest req, SessionService sessions, ChatOrchestrator orchestrator, CancellationToken ct) =>
{
    if (ValidateMessage(req.Message) is { } invalid) return invalid;
    var league = await sessions.GetLeagueAsync(req.SessionId, ct);
    if (league is null)
        return Results.BadRequest(new { error = "Select a league for this session first (POST /api/session/{id}/league)." });

    ChatResponse? final = null;
    await foreach (var ev in orchestrator.RunAsync(req, league.Value, ct))
        if (ev.Type == "done") final = (ChatResponse)ev.Data;
    return Results.Ok(final);
}); // global rate limiter (registered above) already covers every endpoint — no per-route policy needed

// Server-Sent Events: the answer arrives token by token. POST (not EventSource's GET) because the
// message goes in the body — the frontend reads the stream with fetch(). Event types are
// documented on ChatEvent in ChatOrchestrator.cs; "done" is always the last event.
app.MapPost("/api/chat/stream", async (
    ChatRequest req, SessionService sessions, ChatOrchestrator orchestrator, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
    ILoggerFactory loggerFactory, HttpContext http, CancellationToken ct) =>
{
    if (ValidateMessage(req.Message) is { } invalid) return invalid;
    var league = await sessions.GetLeagueAsync(req.SessionId, ct);
    if (league is null)
        return Results.BadRequest(new { error = "Select a league for this session first (POST /api/session/{id}/league)." });

    http.Response.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers["X-Accel-Buffering"] = "no"; // stop nginx-style proxies from buffering the whole stream
    http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

    var json = jsonOptions.Value.SerializerOptions;
    async Task WriteEventAsync(string type, object data)
    {
        await http.Response.WriteAsync($"event: {type}\ndata: {JsonSerializer.Serialize(data, json)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    try
    {
        await foreach (var ev in orchestrator.RunAsync(req, league.Value, ct))
            await WriteEventAsync(ev.Type, ev.Data);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // Headers are already sent, so a 500 is no longer possible — finish the stream with an
        // error-kind "done" instead, which the frontend renders as a retryable error.
        loggerFactory.CreateLogger("ChatStream").LogError(ex, "Streamed chat turn failed for session {SessionId}.", req.SessionId);
        await WriteEventAsync("done", new ChatResponse(0, "human_handoff",
            "Sorry — something went wrong on our side while answering that. Please try again in a moment.",
            Array.Empty<Citation>(), Array.Empty<DecisionOption>(), true, Kind: "error"));
    }
    return Results.Empty;
});

app.MapPost("/api/chat/decision-answer", async (
    DecisionAnswerRequest req, DecisionTreeService decisionTrees, ConversationLogger logger, CancellationToken ct) =>
{
    var stopwatch = Stopwatch.StartNew();
    try
    {
        var step = await decisionTrees.AdvanceAsync(req.SessionId, req.SelectedOptionLabel, ct);

        var citations = step.Citation is Citation c ? new[] { c } : Array.Empty<Citation>();
        var logId = await logger.LogAsync(req.SessionId, $"[decision: {req.SelectedOptionLabel}]", RoutePath.DecisionTree,
            step.Message, (int)stopwatch.ElapsedMilliseconds, tokenCostUsd: 0m, retrievedChunkIds: Array.Empty<Guid>(), ct: ct);

        return Results.Ok(new ChatResponse(logId, "decision_tree", step.Message, citations,
            step.Options.Select(o => new DecisionOption(o.Label)).ToList(), step.IsTerminal, Kind: "decision"));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/feedback", async (FeedbackRequest req, ConversationLogger logger, CancellationToken ct) =>
{
    try
    {
        await logger.RecordFeedbackAsync(req.LogId, req.ThumbsUp, ct);
        return Results.NoContent();
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

// The contact-admin form. Always logged the same way as an automatic handoff, so the admin review
// queue sees both kinds in one place; when the visitor leaves an email (with consent), that's
// stored in handoff_requests and shown next to the question in the queue. Returns the message
// text so the frontend renders the real contact info instead of keeping its own hardcoded copy.
app.MapPost("/api/human-handoff", async (
    HumanHandoffRequest req, ConversationLogger logger, HandoffNotifier notifier, IConfiguration config, CancellationToken ct) =>
{
    var supportContact = SupportContact(config);
    var message = (req.Message ?? "").Trim();
    if (message.Length == 0) message = "User requested human support directly.";
    if (message.Length > 2000) return Results.BadRequest(new { error = "Message is too long (2,000 characters max)." });

    var email = req.Email?.Trim();
    var name = string.IsNullOrWhiteSpace(req.Name) ? null : req.Name.Trim();
    if (!string.IsNullOrEmpty(email))
    {
        if (!req.ConsentGiven)
            return Results.BadRequest(new { error = "Please tick the consent box so we can use your details to reply." });
        if (email.Length > 320 || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || !parsed.Host.Contains('.'))
            return Results.BadRequest(new { error = "That email address doesn't look right." });
        if (name is { Length: > 200 }) name = name[..200];
    }

    var reply = string.IsNullOrEmpty(email)
        ? $"A league admin will follow up on this. In the meantime, you can email {supportContact} directly if it's urgent."
        : $"Thanks{(name is null ? "" : ", " + name)} — your request has been sent. A league admin will reply to {email}. " +
          $"If it's urgent, you can also email {supportContact} directly.";

    var logId = await logger.LogAsync(req.SessionId, message, RoutePath.HumanHandoff,
        reply, latencyMs: 0, tokenCostUsd: 0m, retrievedChunkIds: Array.Empty<Guid>(), ct: ct);
    if (!string.IsNullOrEmpty(email))
    {
        var requestId = await logger.LogHandoffRequestAsync(logId, req.SessionId, name, email, message, HandoffConsentText(config), ct);

        // Phase 3 handoff: email the admins right away, with the conversation so far and the
        // visitor as Reply-To — replying to the email answers the visitor directly. Sent in the
        // background so a slow mail server never delays the visitor's confirmation. Delivery is
        // tracked per request: if this send fails or the API restarts mid-send, HandoffNotifier's
        // sweep retries it within a couple of minutes.
        _ = Task.Run(() => notifier.SendAsync(requestId));
    }

    return Results.Ok(new { logId, message = reply });
});

// ---------------------------------------------------------------------- admin (Phase 5)
// Read-only telemetry + an actionable review queue, wrapping sql/004_dashboards.sql's queries —
// see frontend/admin.html. All routes require X-Admin-Key (checked by the middleware above).

app.MapGet("/api/admin/summary", async (AdminService admin, CancellationToken ct) =>
    Results.Ok(await admin.GetSummaryAsync(ct)));

app.MapGet("/api/admin/review-queue", async (AdminService admin, CancellationToken ct) =>
    Results.Ok(await admin.GetReviewQueueAsync(ct)));

app.MapGet("/api/admin/decision-tree-dropoff", async (AdminService admin, CancellationToken ct) =>
    Results.Ok(await admin.GetDecisionTreeDropoffAsync(ct)));

app.MapGet("/api/admin/top-chunks", async (AdminService admin, CancellationToken ct) =>
    Results.Ok(await admin.GetTopChunksAsync(ct)));

app.MapGet("/api/admin/top-unanswered", async (AdminService admin, CancellationToken ct) =>
    Results.Ok(await admin.GetTopUnansweredAsync(ct)));

// ---- operations (Phase 3): alerts, budget, content freshness, drift

app.MapGet("/api/admin/health", async (AdminService admin, AlertService alerts, BudgetGovernor budget, EmailSender email, CancellationToken ct) =>
{
    var b = await budget.GetStateAsync(ct);
    return Results.Ok(new AdminHealth(
        await alerts.GetOpenAsync(ct),
        new BudgetSummary(b.SpentUsd, b.CapUsd, b.PercentUsed, b.Exhausted),
        await admin.GetSourcesAsync(ct),
        await admin.GetDriftTrendAsync(ct),
        email.IsSmtpConfigured));
});

app.MapPost("/api/admin/alerts/{alertId:long}/acknowledge", async (long alertId, AlertService alerts, CancellationToken ct) =>
{
    try
    {
        await alerts.AcknowledgeAsync(alertId, ct);
        return Results.NoContent();
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

// Re-crawl every configured page now instead of waiting for tonight. Takes a minute or two (it
// fetches each page and re-embeds anything that changed); the response lists what happened.
app.MapPost("/api/admin/recrawl", async (RecrawlService recrawl, CancellationToken ct) =>
    Results.Ok(await recrawl.RunAsync(ct)));

// Sends one test email to the admin recipients, to check the SMTP settings.
app.MapPost("/api/admin/test-email", async (EmailSender email, CancellationToken ct) =>
{
    try
    {
        await email.SendToAdminsAsync("Test email from the ARCL Rules Assistant",
            "If you're reading this, alert and help-request emails will reach you.", ct: ct);
        return Results.Ok(new { sent = true, smtp = email.IsSmtpConfigured, recipients = email.AdminRecipients });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { sent = false, smtp = email.IsSmtpConfigured, error = ex.Message });
    }
});

app.MapPost("/api/admin/review-queue/{logId:long}/review", async (long logId, MarkReviewedRequest req, AdminService admin, CancellationToken ct) =>
{
    try
    {
        await admin.MarkReviewedAsync(logId, req.Note, ct);
        return Results.NoContent();
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

app.Run();

IResult? ValidateMessage(string? message)
{
    if (string.IsNullOrWhiteSpace(message)) return Results.BadRequest(new { error = "Message is empty." });
    if (message.Length > maxMessageLength)
        return Results.BadRequest(new { error = $"Please keep questions under {maxMessageLength:N0} characters." });
    return null;
}

static string SupportContact(IConfiguration config) =>
    config.GetNonEmpty("Arcl:SupportContact") ?? throw new InvalidOperationException("Set Arcl:SupportContact in appsettings.json.");

// Served to the frontend (GET /api/config/public) AND stored with each submission, so the record
// always holds the exact wording that person saw.
static string HandoffConsentText(IConfiguration config) =>
    (config.GetNonEmpty("Arcl:HandoffConsentText")
        ?? "By submitting this form, you consent to ARCL collecting and using your name, email address and message " +
           "for the sole purpose of responding to this request, in accordance with the Personal Data Protection Act (PDPA). " +
           "You may withdraw consent, or ask to access or correct your data, at any time by emailing {SupportContact}.")
    .Replace("{SupportContact}", SupportContact(config));
