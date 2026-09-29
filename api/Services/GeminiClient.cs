using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arcl.Api.Services;

public sealed record RetrievedChunkForPrompt(string SectionNumber, string SectionTitle, string Content);

public sealed record GeminiAnswer(
    string Text, bool Confident, IReadOnlyList<string> CitedSectionNumbers, decimal EstimatedCostUsd,
    int PromptTokens, int CompletionTokens);

/// <summary>
/// Wraps the two Gemini calls this system needs: embeddings (shared with the Phase 1 ingestion
/// tool — keep the model name and dimension in sync with sql/001_schema.sql's VECTOR(768)) and
/// chat completion for FAQ answers.
///
/// VERIFY before relying on this in production: model names, pricing, and the exact request/
/// response shape for Gemini's API do change over time. This was written against Google's
/// current docs but pin down current pricing per model before trusting the cost estimates this
/// class produces — those feed conversation_log.token_cost_usd, which Phase 4 and Phase 5 both
/// use for reporting.
/// </summary>
public sealed class GeminiClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _chatModel;
    private readonly string _embeddingModel;
    private readonly int _embeddingDimensions;

    // gemini-3.5-flash-lite pricing as of the last check (Sept 2026) — confirm at
    // https://ai.google.dev/gemini-api/docs/pricing before trusting cost figures.
    private const decimal InputCostPerMillionTokens = 0.30m;
    private const decimal OutputCostPerMillionTokens = 2.50m;

    public GeminiClient(HttpClient http, IConfiguration config)
    {
        _http = http;
        _apiKey = config.GetNonEmpty("Arcl:GeminiApiKey")            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? throw new InvalidOperationException("Set Arcl:GeminiApiKey (appsettings) or GEMINI_API_KEY (env var).");
        _chatModel = config["Arcl:ChatModel"] ?? "gemini-3.5-flash-lite";
        _embeddingModel = config["Arcl:EmbeddingModel"] ?? "gemini-embedding-2";
        _embeddingDimensions = config.GetValue<int?>("Arcl:EmbeddingDimensions") ?? 768;
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_embeddingModel}:embedContent?key={_apiKey}";
        var payload = new
        {
            model = $"models/{_embeddingModel}",
            content = new { parts = new[] { new { text } } },
            outputDimensionality = _embeddingDimensions
        };

        using var resp = await _http.PostAsJsonAsync(url, payload, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var values = doc.RootElement.GetProperty("embedding").GetProperty("values");

        var result = new float[values.GetArrayLength()];
        var i = 0;
        foreach (var v in values.EnumerateArray())
            result[i++] = v.GetSingle();
        return result;
    }

    /// <summary>
    /// Answers a FAQ-routed question from the retrieved chunks only. The model is required to
    /// say which sections it actually used and to flag low confidence rather than guess — this
    /// is the citation/hallucination guardrail from the revised blueprint's Phase 2. The caller
    /// (ChatEndpoints) still applies the numeric confidence threshold from
    /// Arcl:LowConfidenceCosineDistanceThreshold on TOP of this — the two checks are independent:
    /// one is "did retrieval find anything close enough", the other is "did the model think it
    /// could actually answer from what it got".
    /// </summary>
    public async Task<GeminiAnswer> AnswerFaqAsync(string question, IReadOnlyList<RetrievedChunkForPrompt> chunks, CancellationToken ct = default)
    {
        var contextBlock = string.Join("\n\n", chunks.Select(c =>
            $"[Section {c.SectionNumber}: {c.SectionTitle}]\n{c.Content}"));

        var prompt = $"""
            You are answering a question about ARCL cricket league rules and about ARCL website/app
            how-to topics (e.g. registering a team, uploading a scorecard, CricClubs app setup), for a
            public-facing chatbot used to resolve real match disputes and help with account tasks. Only
            use the sections given below — do not use outside knowledge of cricket rules, since ARCL's
            rules deviate from standard cricket in places (e.g. no LBW, runners allowed).

            SECTIONS:
            {contextBlock}

            QUESTION: {question}

            Answer concisely and cite the section number(s) you used. If the question is asking how to
            do something (log in, register, upload a scorecard, set up the app, etc.) and the source
            material describes a sequence of actions, format the answer as a numbered list with each
            step on its own line, separated by real newline characters (e.g. "1. Do X.\n2. Do Y.") —
            one concrete action per step, not a single run-on sentence — even if the source itself isn't
            numbered or isn't split across lines. If the sections above do not contain enough
            information to answer confidently, set "confident" to false and say in "answer" that this
            needs a human — do not
            guess.
            """;

        var payload = new
        {
            contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
            generationConfig = new
            {
                responseMimeType = "application/json",
                responseSchema = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        answer = new { type = "STRING" },
                        confident = new { type = "BOOLEAN" },
                        citedSectionNumbers = new { type = "ARRAY", items = new { type = "STRING" } }
                    },
                    required = new[] { "answer", "confident", "citedSectionNumbers" }
                }
            }
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_chatModel}:generateContent?key={_apiKey}";
        using var resp = await _http.PostAsJsonAsync(url, payload, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var candidateText = root.GetProperty("candidates")[0]
            .GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString()
            ?? throw new InvalidOperationException("Gemini returned no text.");

        var parsed = JsonSerializer.Deserialize<StructuredAnswer>(candidateText)
            ?? throw new InvalidOperationException("Could not parse Gemini's structured answer.");

        decimal cost = 0m;
        int promptTokens = 0, outputTokens = 0;
        if (root.TryGetProperty("usageMetadata", out var usage))
        {
            promptTokens = usage.TryGetProperty("promptTokenCount", out var pt) ? pt.GetInt32() : 0;
            outputTokens = usage.TryGetProperty("candidatesTokenCount", out var ct2) ? ct2.GetInt32() : 0;
            cost = (promptTokens / 1_000_000m * InputCostPerMillionTokens)
                 + (outputTokens / 1_000_000m * OutputCostPerMillionTokens);
        }

        return new GeminiAnswer(parsed.Answer, parsed.Confident, parsed.CitedSectionNumbers, cost, promptTokens, outputTokens);
    }

    private sealed record StructuredAnswer(
        [property: JsonPropertyName("answer")] string Answer,
        [property: JsonPropertyName("confident")] bool Confident,
        [property: JsonPropertyName("citedSectionNumbers")] List<string> CitedSectionNumbers);
}
