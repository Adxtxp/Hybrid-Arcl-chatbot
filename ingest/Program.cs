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

namespace Arcl.Ingest;

// SkipOccurrences: how many earlier occurrences of this title to skip before accepting a match,
// for headings whose exact words also appear inline in ordinary prose earlier in the document
// (e.g. "Neutral Umpire" is both a heading AND a phrase used constantly in running rule text).
// Only applies to the real body-matching pass, never the probe pass that locates the table of
// contents (see MatchAnchors) — the probe pass needs the FIRST/TOC occurrence, not a later one.
internal sealed record SectionAnchor(string SectionNumber, string SectionTitle, int SkipOccurrences = 0);

internal sealed record ChunkResult(
    string League,
    string SectionNumber,
    string SectionTitle,
    string Content,
    string SourceUrl,
    DateOnly EffectiveStart);

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // .NET Core/5+ only ships Unicode encodings (UTF-8/16/32, ASCII) out of the box. Legacy
        // single-byte code pages like Windows-1252 — which is what this Word-exported HTML
        // declares via its charset= meta tag — need this provider registered before
        // Encoding.GetEncoding("windows-1252") will resolve instead of throwing
        // NotSupportedException. Must happen once, before DetectEncoding's first call.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

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
            var htmlBytes = await FetchHtmlBytesAsync(opts.Url);

            Console.WriteLine("Stripping to plain text and locating section anchors ...");
            (chunks, notFound) = ChunkByAnchors(htmlBytes, anchors, opts.League, opts.Url, opts.EffectiveStart);

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
            var outcome = await UpsertChunkAsync(conn, chunk, embedding);
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
        string? AnchorsPath, string? ManualContentFile, string? SectionNumber, string? SectionTitle);

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

        return new Options(league, url, effectiveStart, anchorsPath, manualContentFile, sectionNumber, sectionTitle);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage:
              dotnet run -- --league <mens|womens|youth> --url <rulebook url> --anchors <path to anchors json> --effective-start <yyyy-MM-dd>

            Manual mode (single pre-written chunk, e.g. content from a page this tool doesn't chunk):
              dotnet run -- --league <mens|womens|youth> --url <source url, for citation only> --effective-start <yyyy-MM-dd> \
                --content-file <path to a text file with the chunk's content> --section-number <id> --section-title <title>

            Env vars:
              ARCL_DB_CONNECTION   SQL Server connection string (required)
              GEMINI_API_KEY       optional — omit to skip embeddings
            """);
    }

    // ------------------------------------------------------------------ fetch + chunk

    // Fetches raw bytes rather than a pre-decoded string. This page is a Microsoft Word HTML
    // export encoded as Windows-1252 (single-byte dashes, smart quotes, bullets), but
    // HttpClient.GetStringAsync decodes using the HTTP response header's declared charset, not
    // the <meta charset> tag inside the HTML — when those disagree (confirmed directly: dashes
    // and quotes came through as literal " " replacement characters), the damage is done before
    // HtmlAgilityPack ever sees the content. Handing it raw bytes lets HtmlAgilityPack read the
    // encoding declaration out of the HTML itself and decode correctly.
    private static async Task<byte[]> FetchHtmlBytesAsync(string url)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ArclIngest", "1.0"));
        return await http.GetByteArrayAsync(url);
    }

    // Sniffs the declared charset directly out of the HTML's own <meta> tag, rather than
    // trusting HtmlAgilityPack's built-in stream-encoding auto-detection — confirmed NOT
    // reliable here: loading via Load(Stream) alone still decoded a plain Windows-1252 en-dash
    // (byte 0x96 — confirmed via a raw byte dump of the live page) as " ", even though the
    // page's own meta tag plainly declares charset=windows-1252 (it uses the older
    // http-equiv="Content-Type" form, not the HTML5 <meta charset=...> shorthand, which this
    // version of HtmlAgilityPack doesn't seem to pick up from a stream). Latin-1 decodes any
    // byte 1:1, which is enough to safely read the ASCII-only meta declaration itself before we
    // know the real encoding.
    private static Encoding DetectEncoding(byte[] htmlBytes)
    {
        var probe = Encoding.Latin1.GetString(htmlBytes, 0, Math.Min(htmlBytes.Length, 4096));
        var match = Regex.Match(probe, @"charset\s*=\s*""?([\w-]+)""?", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var name = match.Groups[1].Value;
            try
            {
                var enc = Encoding.GetEncoding(name);
                Console.WriteLine($"[diag] DetectEncoding: meta tag says charset={name}, resolved to {enc.WebName}");
                return enc;
            }
            // .NET Core doesn't ship legacy code pages (windows-1252 etc.) by default — that
            // throws NotSupportedException, not ArgumentException, if CodePagesEncodingProvider
            // hasn't been registered (see the RegisterProvider call in Main). Catching only
            // ArgumentException here silently swallowed that and fell back to UTF-8 every time,
            // which is almost certainly why the last two encoding fixes had zero effect.
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                Console.WriteLine($"[diag] DetectEncoding: meta tag says charset={name}, but Encoding.GetEncoding failed ({ex.GetType().Name}: {ex.Message}) — falling back to UTF-8");
            }
        }
        else
        {
            Console.WriteLine("[diag] DetectEncoding: no charset= found in first 4096 bytes — falling back to UTF-8");
        }
        return Encoding.UTF8;
    }

    private static (List<ChunkResult> Chunks, List<SectionAnchor> NotFound) ChunkByAnchors(
        byte[] htmlBytes, List<SectionAnchor> anchors, string league, string sourceUrl, DateOnly effectiveStart)
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        var encoding = DetectEncoding(htmlBytes);
        // Decode to a string ourselves and hand HtmlAgilityPack the string, not the stream.
        // Load(Stream, Encoding) still didn't work — HtmlAgilityPack's OptionReadEncoding is on
        // by default and re-detects (and overrides) the encoding from the stream regardless of
        // what's passed in, using the same sniffing that got this page's charset wrong in the
        // first place (confirmed: byte 0x96, a plain Windows-1252 en-dash, still came through as
        // " " even with the encoding explicitly passed). LoadHtml(string) does no encoding
        // detection at all — the string is already decoded — so this is the one path that
        // actually respects our own detection.
        doc.LoadHtml(encoding.GetString(htmlBytes));

        // HtmlAgilityPack's InnerText is a naive text-node concatenation — it does NOT know that
        // <script>/<style>/<noscript> content isn't "visible text" the way a browser does. Left
        // in, a page's inline scripts dump raw JS/CSS straight into the extracted text stream,
        // which throws off every position-based calculation downstream (the TOC-skip gap
        // heuristic, and the sequential "search from where the last title ended" matching) —
        // titles that are genuinely on the page end up looking "not found" because the search
        // position got knocked out of sync by a script block sitting between two sections.
        var noise = doc.DocumentNode.SelectNodes("//script|//style|//noscript");
        if (noise is not null)
            foreach (var node in noise)
                node.Remove();

        // One more HTML-decode pass on top of HtmlAgilityPack's own: a couple of paragraphs on
        // this page (from a recent pasted-in rule change) are double-encoded — "&amp;nbsp;" in
        // the source instead of a plain space or a single "&nbsp;". HtmlAgilityPack only decodes
        // once, so that leaves the literal 6 characters "&nbsp;" sitting in the extracted text
        // (confirmed directly: "4th&nbsp;of|July Weekend Rule" in the raw extracted text) instead
        // of an actual non-breaking-space character — which silently breaks matching for exactly
        // the sections built from that pasted content. Decoding again is harmless for text that's
        // already clean; it only matters for the double-encoded leftovers.
        var text = NormalizeWhitespace(System.Net.WebUtility.HtmlDecode(doc.DocumentNode.InnerText));

        // Dump the full extracted plain text every run. When most anchors miss (as opposed to
        // one or two wording tweaks), guessing at titles one diagnostic gap at a time doesn't
        // scale — it's far faster to read the real extracted content once and rebuild the
        // anchors list from it directly.
        var debugPath = $"{league}_extracted_debug.txt";
        File.WriteAllText(debugPath, text);
        Console.WriteLine($"[diag] Full extracted text written to {debugPath} ({text.Length} chars).");

        // These rulebook pages open with a table of contents that lists every section title
        // before the real body repeats them as actual headers. A single forward-only pass
        // (find each title, then search from there for the next) matches the TOC instead of
        // the real headers — every title sits right next to the next one there, with almost no
        // text in between. Run a throwaway probe pass first to find where that dense cluster
        // ends, then do the real pass starting just after it, so "content" between headers is
        // the actual rule text, not a blank line to the next TOC entry.
        var probe = MatchAnchors(text, anchors, 0, applySkip: false);
        var bodyStart = FindTocEnd(probe.Matches);
        var (matches, notFound) = bodyStart > 0 ? MatchAnchors(text, anchors, bodyStart, applySkip: true) : probe;

        // Diagnostic safety net: if a title is still "not found" by the sequential/ordered search,
        // check whether it exists ANYWHERE in the text regardless of position. If it does, the
        // problem is the search position getting knocked out of sync (something before it in the
        // list matched somewhere unexpected) — not a wording mismatch. This tells us which kind of
        // bug we're still chasing instead of guessing from the match count alone.
        if (notFound.Count > 0)
        {
            Console.WriteLine($"[diag] extracted text length: {text.Length}, bodyStart: {bodyStart}");
            foreach (var a in notFound)
            {
                // Use the same whitespace-tolerant matcher as the real search, searched from 0 —
                // a plain literal IndexOf here would falsely blame "wording" for what's actually
                // just a line-wrap/whitespace difference the real search already tolerates.
                var (anywhereIdx, _) = FindTitleRegex(text, a.SectionTitle, 0);
                if (anywhereIdx >= 0)
                {
                    var ctxStart = Math.Max(0, anywhereIdx - 40);
                    var ctxLen = Math.Min(120, text.Length - ctxStart);
                    var ctx = text[ctxStart..(ctxStart + ctxLen)].Replace('\n', '|');
                    Console.WriteLine($"[diag]   \"{a.SectionTitle}\" exists (whitespace-tolerant) at {anywhereIdx} — search position was already past it (ordering bug). Context: ...{ctx}...");
                    continue;
                }

                Console.WriteLine($"[diag]   \"{a.SectionTitle}\" does not match anywhere, even whitespace-tolerant.");

                // Still not found — instead of guessing at the real wording again, show the raw
                // text sitting between this anchor's nearest matched neighbors. That's exactly
                // where this section's content is currently getting silently folded into the
                // previous one, so its real heading (whatever HtmlAgilityPack actually extracted)
                // will be sitting right at the start of this window.
                var missingIndex = anchors.IndexOf(a);
                var windowStart = 0;
                for (var i = missingIndex - 1; i >= 0; i--)
                {
                    var found = matches.Find(m => m.Anchor == anchors[i]);
                    if (found.Anchor is not null) { windowStart = found.HeaderLineEnd; break; }
                }
                var windowEnd = text.Length;
                for (var i = missingIndex + 1; i < anchors.Count; i++)
                {
                    var found = matches.Find(m => m.Anchor == anchors[i]);
                    if (found.Anchor is not null) { windowEnd = found.Start; break; }
                }
                var window = text[windowStart..windowEnd];
                var shown = window.Length > 700 ? window[^700..] : window;
                Console.WriteLine($"[diag]     Raw text in that gap: ...{shown.Replace('\n', '|')}...");
            }
        }

        var chunks = new List<ChunkResult>();
        for (var i = 0; i < matches.Count; i++)
        {
            var (anchor, start, headerLineEnd) = matches[i];
            var contentStart = headerLineEnd;
            var contentEnd = i + 1 < matches.Count ? matches[i + 1].Start : text.Length;
            var content = text[contentStart..contentEnd].Trim();

            // Prefer the literal header line as captured from the page; fall back to the
            // configured anchor title if that line looks unusably short (e.g. mid-sentence match).
            // .Replace('\n', ' '): a heading that itself wraps across lines (see the lineEnd fix
            // above) captures with an internal raw newline otherwise — harmless for matching, but
            // ugly to store as a section_title.
            var capturedTitle = text[start..headerLineEnd].Trim().Replace('\n', ' ');
            var title = capturedTitle.Length >= anchor.SectionTitle.Length ? capturedTitle : anchor.SectionTitle;

            chunks.Add(new ChunkResult(league, anchor.SectionNumber, title, content, sourceUrl, effectiveStart));
        }

        // Always-on sanity check, independent of the "not found" list: a title can match
        // successfully at the WRONG position (an inline mention of the same words elsewhere in
        // the prose, rather than the real heading) without ever showing up as "not found" — see
        // SectionAnchor.SkipOccurrences. That kind of bug silently truncates one chunk and
        // corrupts the next, so print a preview of where every chunk actually starts and let a
        // human eyeball it: each preview should read like the natural start of that section, not
        // a fragment mid-sentence.
        Console.WriteLine();
        Console.WriteLine("[diag] Chunk previews (confirm each one starts a real section, not mid-sentence):");
        foreach (var c in chunks)
        {
            var preview = c.Content.Length > 90 ? c.Content[..90] : c.Content;
            Console.WriteLine($"[diag]   [{c.SectionNumber}] \"{c.SectionTitle}\" -> {preview.Replace('\n', ' ')}...");
        }

        return (chunks, notFound);
    }

    // One forward-only pass: for each anchor in order, find its title starting from
    // `searchFrom`, then keep searching after that match for the next anchor. An anchor that
    // isn't found is skipped (reported separately) without disturbing the search position for
    // the rest, so one missing/misspelled title doesn't cascade into failing everything after it.
    private static (List<(SectionAnchor Anchor, int Start, int HeaderLineEnd)> Matches, List<SectionAnchor> NotFound) MatchAnchors(
        string text, List<SectionAnchor> anchors, int searchFrom, bool applySkip)
    {
        var matches = new List<(SectionAnchor Anchor, int Start, int HeaderLineEnd)>();
        var notFound = new List<SectionAnchor>();

        foreach (var anchor in anchors)
        {
            // SkipOccurrences only makes sense once we're searching the real body (applySkip:
            // true) — during the probe pass that locates the TOC, every anchor's FIRST occurrence
            // is exactly what we want (that's the TOC line itself).
            var skip = applySkip ? anchor.SkipOccurrences : 0;
            var (idx, matchedLength) = FindTitle(text, anchor.SectionTitle, searchFrom, skip);
            if (idx < 0)
            {
                notFound.Add(anchor);
                continue;
            }

            // Search for the line break AFTER the whole match, not from its start. A heading
            // that itself wraps across two lines (Word line-wrap again — "Bad\nWeather",
            // "Common\nTerms", "Appendix K\n(Steps to review/sign/accept the policies)") has an
            // internal \n inside the match itself; searching from `idx` finds that internal break
            // and leaves the back half of the heading ("Weather", "Terms", "(Steps to review...)")
            // sitting as a stray prefix on the section's content instead of being skipped as part
            // of the header line.
            var lineEnd = text.IndexOf('\n', idx + matchedLength);
            if (lineEnd < 0) lineEnd = text.Length;

            matches.Add((anchor, idx, lineEnd));
            searchFrom = idx + matchedLength;
        }

        return (matches, notFound);
    }

    // Finds a title, falling back to a shorter prefix (cut at the first '/', '(' or ':') if the
    // full title doesn't appear verbatim. The table of contents on these pages sometimes spells
    // a section out more fully than its actual heading — e.g. the TOC says "Bad Weather / Game
    // End time rule" but the real heading is just "Bad Weather", and "In Case of a Tie in
    // Playoffs (Quarter Finals, Semis or Finals)" loses its parenthetical in the body. Trading a
    // little title precision for not silently losing the whole section is the right trade here.
    private static (int Idx, int MatchedLength) FindTitle(string text, string title, int searchFrom, int skip = 0)
    {
        var idx = FindTitleRegex(text, title, searchFrom, skip);
        if (idx.Idx >= 0) return idx;

        var cut = title.IndexOfAny(new[] { '/', '(', ':' });
        if (cut > 3) // keep at least a few characters — don't fall back to something too short/generic
        {
            var shortTitle = title[..cut].TrimEnd();
            idx = FindTitleRegex(text, shortTitle, searchFrom, skip);
            if (idx.Idx >= 0) return idx;
        }

        return (-1, 0);
    }

    // Matches a title allowing ANY run of whitespace between its words, not a literal single
    // space. These rulebook pages are Microsoft Word HTML exports ("MsoNormal" classes
    // everywhere), and Word's export line-wraps text at a fixed column width — so a heading
    // that reads as one line in a browser can have a literal newline sitting mid-phrase in the
    // source HTML (verified directly against the live page: "Gloves for\nFielders", "Playing
    // for\nMultiple Teams", "Re-scheduling\nOptions:", etc.). A plain exact-substring search
    // silently drops those sections even though the text is genuinely there; this doesn't.
    //
    // Word boundaries (\b) are anchored at the start/end of the pattern (only where the title
    // itself starts/ends on a word character — a title ending in ":" or "/" can't take a \b
    // there, since \b requires a word/non-word transition). Without this, a title like "Game"
    // would also match as a bare substring inside "Games" everywhere that word appears.
    //
    // `skip` accepts the (skip+1)th occurrence instead of the first — needed for headings whose
    // exact words also show up inline in ordinary prose earlier in the document (see
    // SectionAnchor.SkipOccurrences). Each rejected earlier occurrence still advances the search
    // position, exactly like a real accepted match would, so this stays a single forward pass.
    private static (int Idx, int MatchedLength) FindTitleRegex(string text, string title, int searchFrom, int skip = 0)
    {
        if (searchFrom >= text.Length || title.Length == 0) return (-1, 0);

        var words = title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return (-1, 0);

        var pattern = string.Join(@"\s+", words.Select(Regex.Escape));
        if (char.IsLetterOrDigit(title[0])) pattern = @"\b" + pattern;
        if (char.IsLetterOrDigit(title[^1])) pattern += @"\b";

        var pos = searchFrom;
        for (var i = 0; i <= skip; i++)
        {
            var match = Regex.Match(text[pos..], pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
            if (!match.Success) return (-1, 0);
            if (i == skip) return (pos + match.Index, match.Length);
            pos += match.Index + match.Length;
        }

        return (-1, 0);
    }

    // A run of consecutive matches with almost nothing between them (just the next title, no
    // real content) is the table of contents, not the rulebook body. Walk forward from the
    // first match while gaps stay tiny, and return the position right after the last one in
    // that run — 0 if there's no such run (i.e. this page has no TOC to skip).
    //
    // A single oversized gap doesn't necessarily mean the TOC ended here — one long TOC entry
    // (or one entry that failed to match at all and got skipped, see MatchAnchors) can look like
    // a big jump on its own. Only treat it as the real end once the gap AFTER it is also large;
    // otherwise treat it as a one-off and keep extending through the TOC.
    private static int FindTocEnd(List<(SectionAnchor Anchor, int Start, int HeaderLineEnd)> matches)
    {
        const int TocGapThreshold = 50; // chars; real section bodies run far longer than this

        var lastTocIndex = -1;
        var i = 0;
        while (i < matches.Count - 1)
        {
            var gap = matches[i + 1].Start - matches[i].HeaderLineEnd;
            if (gap <= TocGapThreshold)
            {
                lastTocIndex = i + 1;
                i++;
                continue;
            }

            var nextGapAlsoLarge = i + 2 >= matches.Count
                || matches[i + 2].Start - matches[i + 1].HeaderLineEnd > TocGapThreshold;
            if (!nextGapAlsoLarge)
            {
                // one-off — the entry right after this gap is still followed by a tight gap,
                // so we're still inside the TOC; fold it in and keep going.
                lastTocIndex = i + 1;
                i++;
                continue;
            }

            break; // sustained large gaps from here — this is the real end of the TOC
        }

        return lastTocIndex >= 0 ? matches[lastTocIndex].HeaderLineEnd : 0;
    }

    private static string NormalizeWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        var lastWasNewline = false;
        var lastWasSpace = false;
        foreach (var c in s.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (c == '\n')
            {
                if (!lastWasNewline) sb.Append('\n');
                lastWasNewline = true;
                lastWasSpace = false;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && !lastWasNewline) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(c);
                lastWasNewline = false;
                lastWasSpace = false;
            }
        }
        return sb.ToString().Trim();
    }

    // ------------------------------------------------------------------ db upsert

    private enum UpsertOutcome { Inserted, Updated, Unchanged }

    private static async Task<UpsertOutcome> UpsertChunkAsync(SqlConnection conn, ChunkResult chunk, SqlVector<float>? embedding)
    {
        await using var findCmd = new SqlCommand(
            """
            SELECT chunk_id, content
            FROM dbo.rule_chunks
            WHERE league = @league AND section_number = @sectionNumber AND effective_end IS NULL
            """, conn);
        findCmd.Parameters.AddWithValue("@league", chunk.League);
        findCmd.Parameters.AddWithValue("@sectionNumber", chunk.SectionNumber);

        Guid? existingId = null;
        string? existingContent = null;
        await using (var reader = await findCmd.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                existingId = reader.GetGuid(0);
                existingContent = reader.GetString(1);
            }
        }

        if (existingId is not null && existingContent == chunk.Content)
            return UpsertOutcome.Unchanged;

        if (existingId is not null)
        {
            await using var closeCmd = new SqlCommand(
                "UPDATE dbo.rule_chunks SET effective_end = @effectiveStart WHERE chunk_id = @id", conn);
            closeCmd.Parameters.AddWithValue("@effectiveStart", chunk.EffectiveStart.ToDateTime(TimeOnly.MinValue));
            closeCmd.Parameters.AddWithValue("@id", existingId.Value);
            await closeCmd.ExecuteNonQueryAsync();
        }

        await using var insertCmd = new SqlCommand(
            """
            INSERT INTO dbo.rule_chunks
                (league, section_number, section_title, content, source_url, effective_start, embedding)
            VALUES
                (@league, @sectionNumber, @sectionTitle, @content, @sourceUrl, @effectiveStart, @embedding)
            """, conn);
        insertCmd.Parameters.AddWithValue("@league", chunk.League);
        insertCmd.Parameters.AddWithValue("@sectionNumber", chunk.SectionNumber);
        insertCmd.Parameters.AddWithValue("@sectionTitle", chunk.SectionTitle);
        insertCmd.Parameters.AddWithValue("@content", chunk.Content);
        insertCmd.Parameters.AddWithValue("@sourceUrl", chunk.SourceUrl);
        insertCmd.Parameters.AddWithValue("@effectiveStart", chunk.EffectiveStart.ToDateTime(TimeOnly.MinValue));

        // AddWithValue infers the SQL type from the SqlVector<float> value itself — simpler and
        // more reliable across client versions than constructing a SqlDbType.Vector parameter by
        // hand (that enum member's exact name/location shifted during this feature's rollout).
        insertCmd.Parameters.AddWithValue("@embedding", (object?)embedding ?? DBNull.Value);

        await insertCmd.ExecuteNonQueryAsync();

        return existingId is null ? UpsertOutcome.Inserted : UpsertOutcome.Updated;
    }
}

// ---------------------------------------------------------------------- Gemini embeddings

/// <summary>
/// Thin wrapper around Gemini's embedding endpoint. VERIFY the model name and request shape
/// against the current Gemini API docs before relying on this — embedding model names and
/// endpoints do change, and this was written from a snapshot that may be stale by the time
/// you run it.
/// </summary>
internal sealed class GeminiEmbedder
{
    private readonly HttpClient _http = new();
    private readonly string _apiKey;
    private const string Model = "gemini-embedding-2"; // matches Api/Services/GeminiClient.cs and sql/001_schema.sql
    private const int OutputDimensionality = 768;       // must match the VECTOR(768) column — gemini-embedding-2 supports 128-3072

    public GeminiEmbedder(string apiKey) => _apiKey = apiKey;

    public async Task<float[]> EmbedAsync(string text)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:embedContent?key={_apiKey}";
        var payload = new
        {
            model = $"models/{Model}",
            content = new { parts = new[] { new { text } } },
            outputDimensionality = OutputDimensionality
        };

        using var resp = await _http.PostAsJsonAsync(url, payload);
        if (!resp.IsSuccessStatusCode)
        {
            // EnsureSuccessStatusCode() throws without ever reading the response body, which is
            // exactly where Gemini puts the actual reason (input token count exceeded, bad key,
            // model disabled for this project, etc.) — read and surface it instead of guessing
            // from a bare status code.
            var body = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Gemini embedContent failed: {(int)resp.StatusCode} {resp.StatusCode}. " +
                $"Input was {text.Length} chars. Response body: {body}");
        }

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var values = doc.RootElement.GetProperty("embedding").GetProperty("values");

        var result = new float[values.GetArrayLength()];
        var i = 0;
        foreach (var v in values.EnumerateArray())
            result[i++] = v.GetSingle();
        return result;
    }
}