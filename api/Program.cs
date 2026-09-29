using System.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
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
builder.Services.AddSingleton<RetrievalService>();
builder.Services.AddSingleton<DecisionTreeService>();
builder.Services.AddSingleton<ConversationLogger>();
builder.Services.AddSingleton<SessionService>();
builder.Services.AddSingleton<AdminService>();
builder.Services.AddCors();

// Phase 5: admin surface auth. A single shared key, not real accounts — enough to keep
// conversation data and the review queue from being open to anyone who finds the URL, without
// building out a user/session system this project doesn't otherwise need. Secret, so it ships
// blank in appsettings.json on purpose (same as ConnectionString/GeminiApiKey) and must come from
// user-secrets or an env var.
var adminApiKey = builder.Configuration.GetNonEmpty("Arcl:AdminApiKey")
    ?? Environment.GetEnvironmentVariable("ARCL_ADMIN_API_KEY")
    ?? throw new InvalidOperationException("Set Arcl:AdminApiKey (appsettings) or ARCL_ADMIN_API_KEY (env var).");

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

var app = builder.Build();

// Serves frontend/* (copied into wwwroot/ — see README) so the whole app is reachable from one
// origin/port: index.html, admin.html, widget-demo.html, widget.js, and every /api/* route all
// live behind this single address, which is what lets one link cover chat + admin + the embeddable
// widget for anyone testing it, with no separate static file server and no cross-origin API calls.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()); // tighten to the real frontend origin before going live
app.UseRateLimiter();

// Every /api/admin/* route needs a valid X-Admin-Key header. Checked here, once, before any
// admin endpoint runs, rather than per-endpoint — a new admin route added later gets this for
// free just by living under the path prefix.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/admin"))
    {
        var providedKey = context.Request.Headers["X-Admin-Key"].FirstOrDefault();
        if (providedKey is null || providedKey != adminApiKey)
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

app.MapPost("/api/chat", async (
    ChatRequest req,
    SessionService sessions,
    IntentClassifier classifier,
    RetrievalService retrieval,
    DecisionTreeService decisionTrees,
    GeminiClient gemini,
    TeamRankingService teamRanking,
    ConversationLogger logger,
    IConfiguration config,
    CancellationToken ct) =>
{
    var league = await sessions.GetLeagueAsync(req.SessionId, ct);
    if (league is null)
        return Results.BadRequest(new { error = "Select a league for this session first (POST /api/session/{id}/league)." });

    var supportContact = config.GetNonEmpty("Arcl:SupportContact")
        ?? throw new InvalidOperationException("Set Arcl:SupportContact in appsettings.json.");

    var stopwatch = Stopwatch.StartNew();

    ChatResponse response;

    // A prior turn asked "which team?" — this message IS the team name, verbatim, regardless of
    // what it looks like. Cleared immediately so a lookup failure can't leave the session stuck
    // waiting forever. See sql/006_team_ranking.sql for why this can't be parsed out of the
    // original question instead.
    if (await sessions.GetPendingTeamRankingAsync(req.SessionId, ct))
    {
        await sessions.SetPendingTeamRankingAsync(req.SessionId, false, ct);
        var teamName = req.Message.Trim();
        var ranking = await teamRanking.LookupAsync(teamName, ct);

        var rankingMessage = ranking is null
            ? $"I couldn't find a team named \"{teamName}\" on arcl.org. Double-check the exact " +
              $"spelling (it needs to match how the team is registered there), then ask about team " +
              $"ranking again to retry."
            : ranking.Rank is null
                ? $"{ranking.TeamName} is registered in {ranking.Division} for {ranking.Season}, but " +
                  $"no rank has been posted yet."
                : $"{ranking.TeamName} is currently ranked #{ranking.Rank} in {ranking.Division} for " +
                  $"{ranking.Season}" + (ranking.RankDescription is null ? "." : $" ({ranking.RankDescription}).");

        var rankingLogId = await logger.LogAsync(req.SessionId, req.Message, RoutePath.Faq, rankingMessage,
            (int)stopwatch.ElapsedMilliseconds, tokenCostUsd: 0m, retrievedChunkIds: Array.Empty<Guid>(), ct: ct);
        return Results.Ok(new ChatResponse(rankingLogId, "team_ranking", rankingMessage,
            Array.Empty<Citation>(), Array.Empty<DecisionOption>(), true));
    }

    var intent = classifier.Classify(req.Message);

    switch (intent.Route)
    {
        case IntentRoute.TeamRanking:
        {
            await sessions.SetPendingTeamRankingAsync(req.SessionId, true, ct);
            const string askMessage = "Which team would you like the ranking for? Please reply with just " +
                "the team name, exactly as it's registered on arcl.org.";
            var logId = await logger.LogAsync(req.SessionId, req.Message, RoutePath.Faq, askMessage,
                (int)stopwatch.ElapsedMilliseconds, tokenCostUsd: 0m, retrievedChunkIds: Array.Empty<Guid>(), ct: ct);
            response = new ChatResponse(logId, "team_ranking", askMessage, Array.Empty<Citation>(), Array.Empty<DecisionOption>(), false);
            break;
        }

        case IntentRoute.DecisionTree:
        {
            var step = await decisionTrees.StartAsync(req.SessionId, league.Value, intent.DecisionTreeTopic!, ct);
            if (step is null)
            {
                // Classifier recognized the topic but nobody's built that tree yet — hand off
                // rather than silently doing nothing.
                response = await BuildHumanHandoffAsync(req, logger, stopwatch, supportContact, ct);
                break;
            }
            var logId = await logger.LogAsync(req.SessionId, req.Message, RoutePath.DecisionTree, step.Message,
                (int)stopwatch.ElapsedMilliseconds, tokenCostUsd: 0m, retrievedChunkIds: Array.Empty<Guid>(), ct: ct);
            response = new ChatResponse(logId, "decision_tree", step.Message, Array.Empty<Citation>(),
                step.Options.Select(o => new DecisionOption(o.Label)).ToList(), step.IsTerminal);
            break;
        }

        case IntentRoute.Ambiguous:
        {
            response = await BuildHumanHandoffAsync(req, logger, stopwatch, supportContact, ct);
            break;
        }

        default: // Faq
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var chunks = await retrieval.HybridSearchAsync(req.Message, league.Value, today, ct);

            if (chunks.Count == 0)
            {
                response = await BuildHumanHandoffAsync(req, logger, stopwatch, supportContact, ct);
                break;
            }

            var lowConfidenceThreshold = config.GetValue<double?>("Arcl:LowConfidenceCosineDistanceThreshold") ?? 0.35;
            var outOfScopeThreshold = config.GetValue<double?>("Arcl:OutOfScopeCosineDistanceThreshold") ?? 0.55;
            var bestDistance = chunks.Min(c => c.SemanticDistance);

            // Distinguish "essentially unrelated to the corpus" from "on-topic but not confident
            // enough" — the two need different framing (a flat "not confident" reads as if the
            // question WAS about rules but retrieval came up short, which is misleading for
            // something like "what's the capital of France?"). This is checked on bestDistance
            // alone, before the Gemini call, both because a clearly-unrelated question doesn't
            // need the model's judgment to classify, and to skip paying for that call entirely.
            if (bestDistance > outOfScopeThreshold)
            {
                var outOfScopeText =
                    $"That doesn't look like a question about ARCL rules. I can help with rules, scheduling, " +
                    $"and results for the {league.Value.ToDisplayName()} league. If this is rules-related, try " +
                    $"rephrasing it, or reach a league admin directly at {supportContact}.";
                var outOfScopeLogId = await logger.LogAsync(req.SessionId, req.Message, RoutePath.HumanHandoff, outOfScopeText,
                    (int)stopwatch.ElapsedMilliseconds, tokenCostUsd: 0m, retrievedChunkIds: Array.Empty<Guid>(), ct: ct);
                response = new ChatResponse(outOfScopeLogId, "human_handoff", outOfScopeText,
                    Array.Empty<Citation>(), Array.Empty<DecisionOption>(), true);
                break;
            }

            var answer = await gemini.AnswerFaqAsync(req.Message,
                chunks.Select(c => new RetrievedChunkForPrompt(c.SectionNumber, c.SectionTitle, c.Content)).ToList(), ct);

            var confident = answer.Confident && bestDistance <= lowConfidenceThreshold;

            // Gemini's citedSectionNumbers sometimes come back as "Section 4" rather than the bare
            // "4" that section_number actually stores (see sql/001_schema.sql) — normalize away that
            // prefix before matching, rather than exact-string-comparing, so a real, correctly
            // identified citation isn't discarded as if the model hadn't cited cleanly.
            var citedChunks = chunks.Where(c => answer.CitedSectionNumbers
                    .Any(cited => string.Equals(NormalizeSectionRef(cited), c.SectionNumber, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (citedChunks.Count == 0)
                citedChunks = chunks.Take(1).ToList(); // model didn't cite cleanly — still show what was retrieved

            var messageText = confident
                ? answer.Text
                : $"I'm not confident enough in the rule text I found to answer that precisely. " +
                  $"Please use \"Contact Human Support\" so a league admin can confirm, or email {supportContact} directly.";

            var logId = await logger.LogAsync(
                req.SessionId, req.Message, confident ? RoutePath.Faq : RoutePath.HumanHandoff, messageText,
                (int)stopwatch.ElapsedMilliseconds, answer.EstimatedCostUsd,
                citedChunks.Select(c => c.ChunkId).ToList(),
                promptTokens: answer.PromptTokens, completionTokens: answer.CompletionTokens, ct: ct);

            response = new ChatResponse(
                logId,
                confident ? "faq" : "human_handoff",
                messageText,
                confident ? citedChunks.Select(c => new Citation(c.SectionNumber, c.SectionTitle, c.SourceUrl)).ToList() : Array.Empty<Citation>(),
                Array.Empty<DecisionOption>(),
                IsTerminal: true);
            break;
        }
    }

    return Results.Ok(response);
}); // global rate limiter (registered above) already covers every endpoint — no per-route policy needed

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
            step.Options.Select(o => new DecisionOption(o.Label)).ToList(), step.IsTerminal));
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

app.MapPost("/api/human-handoff", async (ChatRequest req, ConversationLogger logger, IConfiguration config, CancellationToken ct) =>
{
    var supportContact = config.GetNonEmpty("Arcl:SupportContact")
        ?? throw new InvalidOperationException("Set Arcl:SupportContact in appsettings.json.");

    // The frontend's explicit "Contact Human Support" button — always logged the same way as an
    // automatic handoff, so Phase 5's admin review queue sees both kinds in one place. Returns the
    // message text (rather than just logId) so the frontend renders the real contact info instead
    // of keeping its own separate hardcoded copy.
    var message = $"A league admin will follow up on this. In the meantime, you can email {supportContact} directly if it's urgent.";
    var logId = await logger.LogAsync(req.SessionId, req.Message, RoutePath.HumanHandoff,
        message, latencyMs: 0, tokenCostUsd: 0m, retrievedChunkIds: Array.Empty<Guid>(), ct: ct);
    return Results.Ok(new { logId, message });
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

static async Task<ChatResponse> BuildHumanHandoffAsync(ChatRequest req, ConversationLogger logger, Stopwatch stopwatch, string supportContact, CancellationToken ct)
{
    var message = $"I don't have a confident answer for that from the rulebook. " +
                  $"Please use \"Contact Human Support\" so a league admin can help, or email {supportContact} directly.";
    var logId = await logger.LogAsync(req.SessionId, req.Message, RoutePath.HumanHandoff, message,
        (int)stopwatch.ElapsedMilliseconds, tokenCostUsd: 0m, retrievedChunkIds: Array.Empty<Guid>(), ct: ct);
    return new ChatResponse(logId, "human_handoff", message, Array.Empty<Citation>(), Array.Empty<DecisionOption>(), true);
}

// Strips a "Section "/"Sec. " prefix Gemini sometimes adds to citedSectionNumbers, so a citation
// like "Section 4" matches the bare "4" that rule_chunks.section_number actually stores instead of
// silently falling through to the chunks.Take(1) fallback as if the model hadn't cited cleanly.
static string NormalizeSectionRef(string s)
{
    var trimmed = System.Text.RegularExpressions.Regex
        .Replace(s.Trim(), @"^(?:section|sec\.?)\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        .Trim();
    // Gemini sometimes echoes the whole "<number>: <title>" label from the prompt's own
    // "[Section X: Title]" context block (see GeminiClient.cs) instead of just the bare number —
    // cut at the first colon so e.g. "3: Bowling:" still matches the bare "3" that
    // rule_chunks.section_number actually stores. No real section_number contains a colon.
    var colonIdx = trimmed.IndexOf(':');
    return (colonIdx >= 0 ? trimmed[..colonIdx] : trimmed).Trim();
}
