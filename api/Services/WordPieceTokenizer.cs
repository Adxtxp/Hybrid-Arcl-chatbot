using System.Globalization;
using System.Text;

namespace Arcl.Api.Services;

/// <summary>
/// BERT-uncased WordPiece tokenizer — the exact preprocessing the ms-marco-MiniLM cross-encoder
/// (CrossEncoderReranker.cs) was trained with: lowercase, strip accents, split off punctuation,
/// then greedy longest-match against vocab.txt with "##" continuation pieces. Written out here
/// (~100 lines) rather than pulled from a tokenizer package so the reranker has no dependency
/// beyond ONNX Runtime itself, and so its behavior can be unit-tested directly.
/// </summary>
public sealed class WordPieceTokenizer
{
    private const int MaxCharsPerWord = 100; // same cutoff as the reference BERT implementation

    private readonly Dictionary<string, int> _vocab;

    public int ClsId { get; }
    public int SepId { get; }
    public int PadId { get; }
    public int UnkId { get; }

    public WordPieceTokenizer(IEnumerable<string> vocabLines)
    {
        _vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        var id = 0;
        foreach (var line in vocabLines)
            _vocab.TryAdd(line.TrimEnd('\r', '\n'), id++);

        ClsId = _vocab["[CLS]"];
        SepId = _vocab["[SEP]"];
        PadId = _vocab["[PAD]"];
        UnkId = _vocab["[UNK]"];
    }

    public static WordPieceTokenizer FromFile(string vocabPath) => new(File.ReadLines(vocabPath));

    /// <summary>
    /// Encodes a (query, document) pair as [CLS] query [SEP] document [SEP], truncated to
    /// maxLength. The query is capped first (it's short in practice) and the document gets the
    /// rest — for a long rule section that means the reranker scores its opening, which is where
    /// these sections state the rule itself.
    /// </summary>
    public (long[] InputIds, long[] TokenTypeIds) EncodePair(string query, string document, int maxLength)
    {
        var q = Tokenize(query);
        var d = Tokenize(document);

        var maxQuery = Math.Min(q.Count, Math.Max(8, maxLength / 4));
        if (q.Count > maxQuery) q = q.GetRange(0, maxQuery);
        var maxDoc = Math.Max(0, maxLength - 3 - q.Count);
        if (d.Count > maxDoc) d = d.GetRange(0, maxDoc);

        var ids = new long[q.Count + d.Count + 3];
        var types = new long[ids.Length];
        var i = 0;
        ids[i++] = ClsId;
        foreach (var t in q) ids[i++] = t;
        ids[i++] = SepId;
        var docStart = i;
        foreach (var t in d) ids[i++] = t;
        ids[i++] = SepId;
        for (var j = docStart; j < ids.Length; j++) types[j] = 1;

        return (ids, types);
    }

    public List<int> Tokenize(string text)
    {
        var ids = new List<int>();
        foreach (var word in BasicTokenize(text))
            WordPiece(word, ids);
        return ids;
    }

    // ------------------------------------------------------------------ basic tokenization

    internal static IEnumerable<string> BasicTokenize(string text)
    {
        var sb = new StringBuilder();
        // Lowercase + strip accents: NFD splits "é" into "e" + combining mark, then marks are dropped.
        foreach (var c in text.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c == 0 || c == 0xFFFD || (char.IsControl(c) && !char.IsWhiteSpace(c))) continue;

            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            }
            else if (IsPunctuation(c))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                yield return c.ToString();
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    // BERT treats every non-alphanumeric ASCII symbol as punctuation (e.g. "$", "^", "`"), not
    // just Unicode's punctuation categories.
    private static bool IsPunctuation(char c) =>
        (c >= 33 && c <= 47) || (c >= 58 && c <= 64) || (c >= 91 && c <= 96) || (c >= 123 && c <= 126)
        || char.IsPunctuation(c) || char.IsSymbol(c);

    // ------------------------------------------------------------------ wordpiece

    private void WordPiece(string word, List<int> into)
    {
        if (word.Length > MaxCharsPerWord) { into.Add(UnkId); return; }

        var pieces = new List<int>();
        var start = 0;
        while (start < word.Length)
        {
            var end = word.Length;
            var found = -1;
            while (start < end)
            {
                var sub = word[start..end];
                if (start > 0) sub = "##" + sub;
                if (_vocab.TryGetValue(sub, out var id)) { found = id; break; }
                end--;
            }
            if (found < 0) { into.Add(UnkId); return; } // any unmatchable piece makes the whole word [UNK]
            pieces.Add(found);
            start = end;
        }
        into.AddRange(pieces);
    }
}
