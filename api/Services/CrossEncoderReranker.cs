using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Arcl.Api.Services;

/// <summary>
/// Cross-encoder re-ranking: scores each (question, chunk) pair jointly with
/// cross-encoder/ms-marco-MiniLM-L6-v2, run locally through ONNX Runtime. Retrieval (vector +
/// BM25, see RetrievalService.cs) is good at recall — finding the right section somewhere in its
/// top 20 — but ranks with signals that never look at the question and the chunk together. The
/// cross-encoder does, which is what moves the actually-relevant section to the top of what
/// Gemini sees.
///
/// Runs on the API's own CPU: no per-query cost, roughly tens of milliseconds for 20 candidates.
/// The model files are NOT committed (~90 MB) — run scripts/download-reranker.ps1 once. If they're
/// missing the API still starts and simply skips re-ranking (logged once at startup), so a fresh
/// clone isn't broken by a missing download.
/// </summary>
public sealed class CrossEncoderReranker : IDisposable
{
    private readonly InferenceSession? _session;
    private readonly WordPieceTokenizer? _tokenizer;
    private readonly int _maxLength;
    private readonly bool _hasTokenTypeIds;

    public bool IsAvailable => _session is not null;

    public CrossEncoderReranker(IConfiguration config, IHostEnvironment env, ILogger<CrossEncoderReranker> logger)
    {
        if (config.GetValue<bool?>("Arcl:Reranker:Enabled") == false)
        {
            logger.LogInformation("Cross-encoder re-ranking disabled (Arcl:Reranker:Enabled = false).");
            return;
        }

        var dir = config["Arcl:Reranker:ModelDirectory"] ?? "models/ms-marco-MiniLM-L6-v2";
        if (!Path.IsPathRooted(dir)) dir = Path.Combine(env.ContentRootPath, dir);
        var modelPath = Path.Combine(dir, "model.onnx");
        var vocabPath = Path.Combine(dir, "vocab.txt");
        _maxLength = config.GetValue<int?>("Arcl:Reranker:MaxSequenceLength") ?? 384;

        if (!File.Exists(modelPath) || !File.Exists(vocabPath))
        {
            logger.LogWarning(
                "Cross-encoder model not found at {Dir} — retrieval will run WITHOUT re-ranking. " +
                "Run scripts/download-reranker.ps1 to fetch it.", dir);
            return;
        }

        var options = new Microsoft.ML.OnnxRuntime.SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(modelPath, options);
        _tokenizer = WordPieceTokenizer.FromFile(vocabPath);
        // Some ONNX exports of BERT-family models drop token_type_ids — only feed it if declared.
        _hasTokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids");
        logger.LogInformation("Cross-encoder re-ranker loaded from {Dir}.", dir);
    }

    /// <summary>One relevance logit per document (higher = more relevant), in input order.</summary>
    public float[] Score(string query, IReadOnlyList<string> documents)
    {
        if (_session is null || _tokenizer is null)
            throw new InvalidOperationException("Re-ranker is not available — check IsAvailable first.");
        if (documents.Count == 0) return Array.Empty<float>();

        var encoded = documents.Select(d => _tokenizer.EncodePair(query, d, _maxLength)).ToList();
        var batch = encoded.Count;
        var seq = encoded.Max(e => e.InputIds.Length);

        // One padded batch rather than N separate runs — a single Run call is far cheaper.
        var ids = new DenseTensor<long>(new[] { batch, seq });
        var mask = new DenseTensor<long>(new[] { batch, seq });
        var types = new DenseTensor<long>(new[] { batch, seq });
        for (var b = 0; b < batch; b++)
        {
            var (inputIds, typeIds) = encoded[b];
            for (var s = 0; s < seq; s++)
            {
                var real = s < inputIds.Length;
                ids[b, s] = real ? inputIds[s] : _tokenizer.PadId;
                mask[b, s] = real ? 1 : 0;
                types[b, s] = real ? typeIds[s] : 0;
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", ids),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        };
        if (_hasTokenTypeIds) inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", types));

        using var results = _session.Run(inputs);
        var logits = results.First().AsTensor<float>(); // shape [batch, 1]
        var scores = new float[batch];
        for (var b = 0; b < batch; b++) scores[b] = logits[b, 0];
        return scores;
    }

    public void Dispose() => _session?.Dispose();
}
