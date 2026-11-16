// ARCL Hybrid Chatbot — Phase 1 ingestion tool.
//
// Fetches one rulebook page (Men's / Women's / Youth), splits it into chunks by
// section header, and upserts those chunks into dbo.rule_chunks (see ../sql/001_schema.sql).
//
// Targets SQL Server 2025 and its native VECTOR type via Microsoft.Data.SqlClient's
// SqlVector<float> (requires client v10+).
//
// USAGE
//   dotnet run -- --league mens   --url https://arcl.org/Docs/Mens_League_Rules.htm   --anchors SectionAnchors/mens.json   --effective-start 2024-01-01
//   dotnet run -- --league womens --url https://arcl.org/Docs/Womens_League_Rules.htm --anchors SectionAnchors/womens.json --effective-start 2017-02-08
//   dotnet run -- --league youth  --url https://arcl.org/Docs/Kids_League_Rules.htm    --anchors SectionAnchors/youth.json  --effective-start 2024-02-24
//
// ENVIRONMENT
//   ARCL_DB_CONNECTION  SQL Server connection string (required), e.g.
//                        "Server=localhost;Database=arcl;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;"
//   GEMINI_API_KEY       Gemini API key (optional — omit to ingest chunks without embeddings,
//                         e.g. to sanity-check chunking before wiring up Phase 2)
//
// IMPORTANT — READ BEFORE RUNNING
//   The section anchors in SectionAnchors/*.json were built from an AI-assisted read of the
//   live pages, not from their raw HTML (these old .htm exports don't expose clean semantic
//   markup, so this tool matches on header TEXT rather than HTML tags). That means the anchor
//   strings are close paraphrases, not guaranteed verbatim substrings. On first run, check the
//   "anchors not found" summary printed at the end — any miss means that section's content
//   silently folds into the previous section's chunk, which is a correctness bug, not just a
//   missing row. Fix the anchor text in the JSON and re-run until the summary is empty.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;

using Arcl.Ingestion;

namespace Arcl.Ingest;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // The chunking itself lives in shared/Arcl.Ingestion (also used by the API's nightly
        // re-crawl). This tool is the hands-on one: print every "[diag]" line and dump each page's
        // extracted text next to it, so anchor problems are visible.
        RulebookChunker.Diagnostics = Console.Out;
        RulebookChunker.DebugTextDirectory = Directory.GetCurrentDirectory();

        var opts = ParseArgs(args);
        if (opts is null)
        {
            PrintUsage();
            return 1;
        }

        List<ChunkResult> chunks;
        List<SectionAnchor> notFound;

        if (opts.ManualContentFile is not null)
        {
            // Manual mode: for content that doesn't live on one of the three anchor-chunked
            // rulebook pages at all (e.g. a separate FAQ page ARCL publishes, or any other
            // one-off addition), where the content is already known/written rather than needing
            // to be scraped and sliced by heading. Skips the fetch/anchor-matching pipeline
            // entirely but still goes through the same embedding + upsert path as the normal
            // flow below, so this is a single hand-authored chunk, not a parallel code path.
            var manualContent = (await File.ReadAllTextAsync(opts.ManualContentFile)).Trim();
            chunks = new List<ChunkResult>
            {
                new ChunkResult(opts.League, opts.SectionNumber!, opts.SectionTitle!, manualContent, opts.Url, opts.EffectiveStart)
            };
            notFound = new List<SectionAnchor>();
            Console.WriteLine($"Manual content mode: 1 chunk [{opts.SectionNumber}] \"{opts.SectionTitle}\" ({manualContent.Length} chars) for league={opts.League}.");
        }
        else
        {
            var anchors = JsonSerializer.Deserialize<List<SectionAnchor>>(
                await File.ReadAllTextAsync(opts.AnchorsPath!),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (anchors is null || anchors.Count == 0)
            {
                Console.Error.WriteLine($"No anchors loaded from {opts.AnchorsPath}");
                return 1;
            }

            Console.WriteLine($"Fetching {opts.Url} ...");
            var htmlBytes = await RulebookChunker.FetchHtmlBytesAsync(opts.Url);

            Console.WriteLine("Stripping to plain text and locating section anchors ...");
            (chunks, notFound) = RulebookChunker.ChunkByAnchors(htmlBytes, anchors, opts.League, opts.Url, opts.EffectiveStart);

            Console.WriteLine($"Matched {chunks.Count} of {anchors.Count} anchors.");
            if (notFound.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("ANCHORS NOT FOUND (their content silently folded into the prior section — fix and re-run):");
                foreach (var a in notFound)
                    Console.WriteLine($"  [{a.SectionNumber}] \"{a.SectionTitle}\"");
                Console.WriteLine();
            }
        }

        // --dry-run: stop after chunking (the [diag] previews above are the whole point) — no
        // embedding calls, no DB writes. Use this to iterate on SectionAnchors/*.json safely.
        if (opts.DryRun)
        {
            Console.WriteLine();
            Console.WriteLine($"Dry run — {chunks.Count} chunk(s) built, nothing embedded or written.");
            return notFound.Count > 0 ? 2 : 0;
        }

        var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        var embedder = geminiKey is null ? null : new GeminiEmbedder(geminiKey);
        if (embedder is null)
            Console.WriteLine("GEMINI_API_KEY not set — chunks will be inserted with a NULL embedding.");

        var connString = Environment.GetEnvironmentVariable("ARCL_DB_CONNECTION")
            ?? throw new InvalidOperationException("Set ARCL_DB_CONNECTION before running.");

        await using var conn = new SqlConnection(connString);
        await conn.OpenAsync();

        int inserted = 0, updated = 0, unchanged = 0;
        foreach (var chunk in chunks)
        {
            // Print which chunk we're about to embed *before* the call. If the Gemini API
            // rejects one (e.g. content too long for the model's input token limit, or any
            // other reason), the exception tells us the HTTP status but not which chunk
            // triggered it — this line is what actually pinpoints the culprit when a run
            // crashes partway through.
            //
            // A chunk with empty/whitespace-only content is a real, legitimate case — a
            // section-grouping header with no body text of its own (e.g. mens.json's "ARCL
            // Cricket Rules", which just introduces the LBW/Byes/Wide/etc. subsections that
            // follow it) — not a bug. Gemini's embedContent API rejects an empty Part outright
            // (confirmed: "EmbedContentRequest.content contains an empty Part."), and there's
            // nothing meaningful to embed there anyway, so skip the API call and store NULL,
            // same as the no-embedder path.
            var hasContent = !string.IsNullOrWhiteSpace(chunk.Content);
            if (embedder is not null)
            {
                Console.WriteLine(hasContent
                    ? $"[diag] Embedding [{chunk.SectionNumber}] \"{chunk.SectionTitle}\" ({chunk.Content.Length} chars) ..."
                    : $"[diag] Skipping embedding for [{chunk.SectionNumber}] \"{chunk.SectionTitle}\" — content is empty (section-grouping header, no body text); inserting with NULL embedding.");
            }

            SqlVector<float>? embedding = embedder is null || !hasContent
                ? null
                : new SqlVector<float>(await embedder.EmbedAsync(chunk.Content));
            var outcome = await ChunkStore.UpsertChunkAsync(conn, chunk, embedding);
            switch (outcome)
            {
                case UpsertOutcome.Inserted: inserted++; break;
                case UpsertOutcome.Updated: updated++; break;
                case UpsertOutcome.Unchanged: unchanged++; break;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Done. Inserted: {inserted}, updated (content changed, old row closed out): {updated}, unchanged: {unchanged}.");
        if (notFound.Count > 0)
            Console.WriteLine($"WARNING: {notFound.Count} anchor(s) were not found — see list above.");

        return 0;
    }

    // ------------------------------------------------------------------ args

    private sealed record Options(
        string League, string Url, DateOnly EffectiveStart,
        string? AnchorsPath, string? ManualContentFile, string? SectionNumber, string? SectionTitle,
        bool DryRun);

    private static Options? ParseArgs(string[] args)
    {
        string? league = null, url = null, anchorsPath = null, effectiveStartRaw = null;
        string? manualContentFile = null, sectionNumber = null, sectionTitle = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--league": league = args[++i]; break;
                case "--url": url = args[++i]; break;
                case "--anchors": anchorsPath = args[++i]; break;
                case "--effective-start": effectiveStartRaw = args[++i]; break;
                case "--content-file": manualContentFile = args[++i]; break;
                case "--section-number": sectionNumber = args[++i]; break;
                case "--section-title": sectionTitle = args[++i]; break;
            }
        }

        if (league is null || url is null || effectiveStartRaw is null)
            return null;

        // Either the normal anchor-chunked HTML flow, or manual mode (single pre-written chunk,
        // e.g. content pulled from a page this tool doesn't otherwise know how to chunk) — not both.
        if (manualContentFile is not null)
        {
            if (sectionNumber is null || sectionTitle is null)
            {
                Console.Error.WriteLine("--content-file requires --section-number and --section-title.");
                return null;
            }
        }
        else if (anchorsPath is null)
        {
            return null;
        }

        if (league is not ("mens" or "womens" or "youth"))
        {
            Console.Error.WriteLine("--league must be one of: mens, womens, youth");
            return null;
        }

        if (!DateOnly.TryParse(effectiveStartRaw, out var effectiveStart))
        {
            Console.Error.WriteLine("--effective-start must be a date like 2024-01-01");
            return null;
        }

        // Flag, not a key/value pair, so it's checked outside the loop above (which only looks at
        // args that have a value after them — a trailing --dry-run would never be seen there).
        var dryRun = args.Contains("--dry-run");

        return new Options(league, url, effectiveStart, anchorsPath, manualContentFile, sectionNumber, sectionTitle, dryRun);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage:
              dotnet run -- --league <mens|womens|youth> --url <rulebook url> --anchors <path to anchors json> --effective-start <yyyy-MM-dd> [--dry-run]

            --dry-run   chunk and print previews only — no embeddings, no DB writes

            Manual mode (single pre-written chunk, e.g. content from a page this tool doesn't chunk):
              dotnet run -- --league <mens|womens|youth> --url <source url, for citation only> --effective-start <yyyy-MM-dd> \
                --content-file <path to a text file with the chunk's content> --section-number <id> --section-title <title>

            Env vars:
              ARCL_DB_CONNECTION   SQL Server connection string (required)
              GEMINI_API_KEY       optional — omit to skip embeddings
            """);
    }
}
