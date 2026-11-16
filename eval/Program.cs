// ARCL Hybrid Chatbot — Phase 4 evaluation pipeline.
//
// Runs test-questions.json against a running instance of the Phase 2 API and scores each
// answer's retrieval precision/recall (where a question has an expected rule section) and route
// correctness (FAQ vs decision tree vs human handoff). Writes one CSV per run to results/.
//
// test-questions.json holds the 13 original rule questions plus 27 FAQ-page questions (faq-*,
// added 2026-11-16). The blueprint's goal is 50+ including REAL historical ARCL dispute questions
// — the FAQ set covers website/registration/app topics well but not match disputes, so the next
// additions should come from real complaints and the admin review queue. The "correctness" column in
// the output CSV is intentionally left blank for a human to fill in per the blueprint's rubric
// (correct / partially correct / wrong / refused-appropriately) — an LLM grading its own sibling
// LLM's answers is not a substitute for that human judgment call, especially for a
// dispute-resolution tool.
//
// USAGE
//   dotnet run -- --api-base http://localhost:5000
//   dotnet run -- --api-base http://localhost:5000 --min-route-accuracy 0.9 --min-recall 0.7
//
// The API base defaults to http://localhost:5000 if not given.
//
// PRE-DEPLOY GATE: with --min-route-accuracy and/or --min-recall, the process exits 1 when the
// run falls below either threshold, or when any question fails outright (HTTP error, timeout) —
// so scripts/predeploy.ps1 (or any CI step) can block a deploy on it. Without them it always
// exits 0, same as before. The human correctness column is still the real quality signal; these
// gates catch regressions in routing and retrieval, which is what code changes usually break.

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arcl.Eval;

internal sealed record TestQuestion(
    string Id, string League, string Question, string ExpectedRoute,
    List<string> ExpectedSectionNumbers, string? Notes);

internal sealed record EvalResult(
    string Id, string League, string Question,
    string ExpectedRoute, string ActualRoute, bool RouteMatch,
    double? Precision, double? Recall,
    long LatencyMs, string ResponseText, string RetrievedSections, string Confidence);

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var apiBase = "http://localhost:5000";
        double? minRouteAccuracy = null, minRecall = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--api-base": apiBase = args[i + 1]; break;
                case "--min-route-accuracy": minRouteAccuracy = double.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--min-recall": minRecall = double.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture); break;
            }
        }

        var questionsPath = Path.Combine(AppContext.BaseDirectory, "test-questions.json");
        var questions = JsonSerializer.Deserialize<List<TestQuestion>>(
            await File.ReadAllTextAsync(questionsPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("No test questions loaded.");

        Console.WriteLine($"Running {questions.Count} questions against {apiBase} ...");
        if (questions.Count < 50)
            Console.WriteLine($"NOTE: {questions.Count} questions — the goal is 50+, including real historical dispute questions. Keep expanding test-questions.json.\n");

        using var http = new HttpClient { BaseAddress = new Uri(apiBase) };
        var results = new List<EvalResult>();
        var failures = 0;

        foreach (var q in questions)
        {
            try
            {
                results.Add(await RunOneAsync(http, q));
                Console.WriteLine($"  [{q.Id}] route {(results[^1].RouteMatch ? "OK" : "MISMATCH")} ({results[^1].ActualRoute}, {results[^1].Confidence}), {results[^1].LatencyMs}ms");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"  [{q.Id}] FAILED: {ex.Message}");
            }
        }

        WriteCsv(results);
        var (routeAccuracy, avgRecall) = PrintSummary(results);

        if (minRouteAccuracy is null && minRecall is null) return 0;

        var gateFailures = new List<string>();
        if (failures > 0) gateFailures.Add($"{failures} question(s) failed outright");
        if (minRouteAccuracy is double minRoute && routeAccuracy < minRoute)
            gateFailures.Add($"route accuracy {routeAccuracy:P0} < {minRoute:P0}");
        if (minRecall is double minRc && (avgRecall ?? 0) < minRc)
            gateFailures.Add($"avg. recall {(avgRecall ?? 0):P0} < {minRc:P0}");

        Console.WriteLine();
        if (gateFailures.Count == 0)
        {
            Console.WriteLine("GATE PASSED.");
            return 0;
        }
        Console.WriteLine("GATE FAILED: " + string.Join("; ", gateFailures));
        return 1;
    }

    private static async Task<EvalResult> RunOneAsync(HttpClient http, TestQuestion q)
    {
        var league = q.League; // must match the API's League enum member names (Mens/Womens/Youth)

        var sessionResp = await http.PostAsJsonAsync("/api/session", new { league });
        sessionResp.EnsureSuccessStatusCode();
        var session = await sessionResp.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("sessionId").GetGuid();

        var stopwatch = Stopwatch.StartNew();
        var chatResp = await http.PostAsJsonAsync("/api/chat", new { sessionId, message = q.Question });
        stopwatch.Stop();
        chatResp.EnsureSuccessStatusCode();

        var chat = await chatResp.Content.ReadFromJsonAsync<JsonElement>();
        var actualRoute = chat.GetProperty("routePath").GetString() ?? "";
        var responseText = chat.GetProperty("message").GetString() ?? "";

        var retrievedSections = new List<string>();
        if (chat.TryGetProperty("citations", out var citations))
            foreach (var c in citations.EnumerateArray())
                retrievedSections.Add(c.GetProperty("sectionNumber").GetString() ?? "");

        double? precision = null, recall = null;
        if (q.ExpectedSectionNumbers.Count > 0)
        {
            var hits = retrievedSections.Count(s => q.ExpectedSectionNumbers.Contains(s));
            precision = retrievedSections.Count == 0 ? 0 : (double)hits / retrievedSections.Count;
            recall = (double)hits / q.ExpectedSectionNumbers.Count;
        }

        var confidence = chat.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.String
            ? conf.GetString() ?? "" : "";

        return new EvalResult(
            q.Id, q.League, q.Question,
            q.ExpectedRoute, actualRoute, actualRoute == q.ExpectedRoute,
            precision, recall,
            stopwatch.ElapsedMilliseconds, responseText, string.Join(";", retrievedSections), confidence);
    }

    private static void WriteCsv(List<EvalResult> results)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "results");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"eval-run-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");

        var sb = new StringBuilder();
        sb.AppendLine("id,league,question,expected_route,actual_route,confidence,route_match,precision,recall,latency_ms,retrieved_sections,response_text,correctness,notes");
        foreach (var r in results)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                Csv(r.Id), Csv(r.League), Csv(r.Question), Csv(r.ExpectedRoute), Csv(r.ActualRoute), Csv(r.Confidence),
                r.RouteMatch.ToString(), r.Precision?.ToString("F2") ?? "", r.Recall?.ToString("F2") ?? "",
                r.LatencyMs.ToString(), Csv(r.RetrievedSections), Csv(r.ResponseText),
                "", "" // correctness, notes — left blank for the human review pass (Phase 4 rubric)
            }));
        }

        File.WriteAllText(path, sb.ToString());
        Console.WriteLine($"\nWrote {path}");
    }

    private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"").Replace("\n", " ") + "\"";

    private static (double RouteAccuracy, double? AvgRecall) PrintSummary(List<EvalResult> results)
    {
        if (results.Count == 0) return (0, null);

        var routeAccuracy = results.Count(r => r.RouteMatch) / (double)results.Count;
        var scored = results.Where(r => r.Precision is not null).ToList();
        var avgPrecision = scored.Count > 0 ? scored.Average(r => r.Precision!.Value) : (double?)null;
        var avgRecall = scored.Count > 0 ? scored.Average(r => r.Recall!.Value) : (double?)null;
        var avgLatency = results.Average(r => r.LatencyMs);

        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        Console.WriteLine($"Route accuracy:     {routeAccuracy:P0} ({results.Count(r => r.RouteMatch)}/{results.Count})");
        Console.WriteLine($"Avg. precision:     {(avgPrecision is double p ? p.ToString("P0") : "n/a")} (over {scored.Count} questions with an expected section)");
        Console.WriteLine($"Avg. recall:        {(avgRecall is double rc ? rc.ToString("P0") : "n/a")}");
        Console.WriteLine($"Avg. latency:       {avgLatency:F0} ms");
        Console.WriteLine();
        Console.WriteLine("Cost and correctness-rubric scoring are NOT computed here: cost lives in conversation_log.token_cost_usd");
        Console.WriteLine("(query it directly — see sql/004_dashboards.sql), and correctness needs the human pass described above.");
        return (routeAccuracy, avgRecall);
    }
}
