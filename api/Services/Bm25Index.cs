using System.Text.RegularExpressions;

namespace Arcl.Api.Services;

/// <summary>
/// Okapi BM25 over one league's rule chunks, held in memory. This is the keyword half of hybrid
/// retrieval — it replaces the LIKE-count fallback that stood in for full-text search, which was
/// never installed on this SQL Server instance (see sql/001_schema.sql). At this corpus size (a
/// few hundred chunks across all three leagues) an in-process index is both simpler and faster
/// than a round trip to CONTAINSTABLE, and it doesn't depend on an optional SQL Server feature.
///
/// Built from a snapshot by RetrievalService, which rebuilds it when its cache expires — so a
/// re-ingest shows up here within Arcl:CorpusCacheMinutes without restarting the API.
/// </summary>
public sealed class Bm25Index
{
    // Standard Okapi defaults. k1 controls term-frequency saturation, b how strongly long chunks
    // are penalized — these rulebook sections range from ~30 to ~7,500 chars, so length
    // normalization genuinely matters here.
    private const double K1 = 1.2;
    private const double B = 0.75;

    // Section titles are short and highly informative ("No-ball", "Bad Weather") — counting their
    // terms more than once lets a title hit outrank the same word buried in a long body.
    private const int TitleWeight = 3;

    private readonly List<(Guid Id, Dictionary<string, int> TermFreqs, int Length)> _docs = new();
    private readonly Dictionary<string, int> _docFreqs = new();
    private readonly double _avgDocLength;

    public Bm25Index(IEnumerable<(Guid Id, string Title, string Content)> docs)
    {
        foreach (var (id, title, content) in docs)
        {
            var terms = new List<string>();
            var titleTerms = Tokenize(title);
            for (var i = 0; i < TitleWeight; i++) terms.AddRange(titleTerms);
            terms.AddRange(Tokenize(content));

            var tf = new Dictionary<string, int>();
            foreach (var t in terms) tf[t] = tf.GetValueOrDefault(t) + 1;
            foreach (var t in tf.Keys) _docFreqs[t] = _docFreqs.GetValueOrDefault(t) + 1;

            _docs.Add((id, tf, terms.Count));
        }

        _avgDocLength = _docs.Count == 0 ? 0 : _docs.Average(d => d.Length);
    }

    public int Count => _docs.Count;

    public IReadOnlyList<(Guid Id, double Score)> Search(string query, int top)
    {
        var queryTerms = Tokenize(query).Distinct().ToList();
        if (queryTerms.Count == 0 || _docs.Count == 0) return Array.Empty<(Guid, double)>();

        var n = _docs.Count;
        var scored = new List<(Guid Id, double Score)>();
        foreach (var (id, tf, length) in _docs)
        {
            double score = 0;
            foreach (var term in queryTerms)
            {
                if (!tf.TryGetValue(term, out var freq)) continue;
                var df = _docFreqs[term];
                // BM25+ style IDF (always positive), so a term that appears in most chunks still
                // contributes a little instead of going negative and penalizing a match.
                var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                score += idf * (freq * (K1 + 1)) / (freq + K1 * (1 - B + B * length / _avgDocLength));
            }
            if (score > 0) scored.Add((id, score));
        }

        return scored.OrderByDescending(s => s.Score).Take(top).ToList();
    }

    // ------------------------------------------------------------------ tokenization

    // Words, keeping internal hyphens so "no-ball" / "1-D" survive as units (and are then also
    // split — see Tokenize).
    private static readonly Regex WordPattern = new(@"[\p{L}\p{N}]+(?:-[\p{L}\p{N}]+)*", RegexOptions.Compiled);

    // Deliberately small. "no", "not" and "out" are NOT here: "no-ball", "no-show", "not out" and
    // "run out" are exactly the phrases people ask about.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "of", "to", "in", "on", "at", "for", "by", "with", "is", "are",
        "was", "were", "be", "been", "it", "its", "this", "that", "these", "those", "as", "if", "can",
        "do", "does", "did", "what", "which", "who", "whom", "how", "when", "where", "why", "i", "we",
        "you", "my", "our", "your", "me", "us", "they", "them", "their", "will", "would", "should",
        "there", "here", "from", "about", "into", "than", "then", "so", "any", "all", "has", "have", "had",
    };

    /// <summary>Lowercased, stop-word-filtered, lightly stemmed terms. A hyphenated word yields its
    /// parts plus the joined form, so "no-ball", "no ball" and "noball" all meet in the middle.</summary>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        var result = new List<string>();
        foreach (Match m in WordPattern.Matches(text.ToLowerInvariant()))
        {
            var word = m.Value;
            if (word.Contains('-'))
            {
                foreach (var part in word.Split('-')) AddTerm(result, part);
                AddTerm(result, word.Replace("-", ""));
            }
            else
            {
                AddTerm(result, word);
            }
        }
        return result;
    }

    private static void AddTerm(List<string> into, string word)
    {
        if (word.Length == 0 || StopWords.Contains(word)) return;
        into.Add(Stem(word));
    }

    // Suffix stripping, not a real stemmer — just enough that "players"/"player", "rules"/"rule",
    // "abandoned"/"abandon" and "batting"/"batt"/"bat"-ish forms collide. Applied identically to
    // queries and documents, so consistency matters more than linguistic accuracy.
    internal static string Stem(string w)
    {
        if (w.Length > 4 && w.EndsWith("ies")) return w[..^3] + "y";
        if (w.Length > 5 && w.EndsWith("ing")) return w[..^3];
        if (w.Length > 4 && w.EndsWith("ed")) return w[..^2];
        if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss") && !w.EndsWith("us") && !w.EndsWith("is")) return w[..^1];
        return w;
    }
}
