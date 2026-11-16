using Arcl.Api.Services;

namespace Arcl.Api.Tests;

public class Bm25IndexTests
{
    private static readonly Guid NoBall = Guid.NewGuid(), Wide = Guid.NewGuid(), Weather = Guid.NewGuid(), Players = Guid.NewGuid();

    private static Bm25Index BuildIndex() => new(new[]
    {
        (NoBall, "No-ball", "A no-ball results in one extra run and a free hit. Hit-wicket is not out on a no-ball."),
        (Wide, "Wide", "A ball passing outside the wide line is called a wide and one run is added."),
        (Weather, "Bad Weather", "Games will go on in a typical Seattle drizzle. Only the neutral umpire can call off a game for rain."),
        (Players, "Players", "Each team will play the game with at most 8 players. Substitutes must be declared."),
    });

    [Fact]
    public void Title_match_ranks_first()
    {
        var results = BuildIndex().Search("what happens on a no ball", 4);
        Assert.Equal(NoBall, results[0].Id);
    }

    [Fact]
    public void Hyphenated_and_unhyphenated_forms_meet()
    {
        var index = BuildIndex();
        Assert.Equal(NoBall, index.Search("noball", 1).Single().Id);
        Assert.Equal(NoBall, index.Search("no-ball", 1).Single().Id);
    }

    [Fact]
    public void Plural_and_singular_match_via_stemming()
    {
        Assert.Equal(Players, BuildIndex().Search("how many player can play", 1).Single().Id);
    }

    [Fact]
    public void Stop_words_alone_match_nothing()
    {
        Assert.Empty(BuildIndex().Search("what is the", 4));
    }

    [Theory]
    [InlineData("players", "player")]
    [InlineData("penalties", "penalty")]
    [InlineData("abandoned", "abandon")]
    [InlineData("class", "class")]   // "ss" isn't a plural
    [InlineData("rules", "rule")]
    public void Stem(string word, string expected) => Assert.Equal(expected, Bm25Index.Stem(word));
}

public class WordPieceTokenizerTests
{
    // Tiny BERT-style vocab: specials at the usual positions don't matter, only that they exist.
    private static readonly WordPieceTokenizer Tokenizer = new(new[]
    {
        "[PAD]", "[UNK]", "[CLS]", "[SEP]", "the", "no", "-", "ball", "free", "hit", "##s", "run", "?", "un", "##play", "##able",
    });

    [Fact]
    public void Splits_punctuation_and_lowercases()
    {
        Assert.Equal(new[] { "no", "-", "ball", "?" }, WordPieceTokenizer.BasicTokenize("No-Ball?").ToArray());
    }

    [Fact]
    public void Greedy_longest_match_with_continuations()
    {
        // "unplayable" -> un ##play ##able ; "runs" -> run ##s
        Assert.Equal(new[] { 13, 14, 15, 11, 10 }, Tokenizer.Tokenize("unplayable runs").ToArray());
    }

    [Fact]
    public void Unknown_word_becomes_single_unk()
    {
        Assert.Equal(new[] { Tokenizer.UnkId }, Tokenizer.Tokenize("zzz").ToArray());
    }

    [Fact]
    public void Pair_encoding_layout_and_truncation()
    {
        var (ids, types) = Tokenizer.EncodePair("free hit", "the ball the ball the ball the ball", maxLength: 8);
        Assert.Equal(8, ids.Length);
        Assert.Equal(Tokenizer.ClsId, ids[0]);
        Assert.Equal(Tokenizer.SepId, ids[3]);   // [CLS] free hit [SEP]
        Assert.Equal(Tokenizer.SepId, ids[^1]);
        Assert.Equal(new long[] { 0, 0, 0, 0, 1, 1, 1, 1 }, types);
    }
}

public class CrossEncoderSmokeTests
{
    // Runs only when the real model has been downloaded (scripts/download-reranker.ps1) — checks
    // end to end that the ONNX inputs/outputs are wired correctly and that the scores make sense.
    [Fact]
    public void Relevant_chunk_scores_higher_than_unrelated_one()
    {
        var modelDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../api/models/ms-marco-MiniLM-L6-v2"));
        if (!File.Exists(Path.Combine(modelDir, "model.onnx"))) return; // not downloaded — nothing to check

        var config = TestConfig.Build(new() { ["Arcl:Reranker:ModelDirectory"] = modelDir });
        using var reranker = new CrossEncoderReranker(config, new TestHostEnvironment(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CrossEncoderReranker>.Instance);
        Assert.True(reranker.IsAvailable);

        var scores = reranker.Score("Is LBW a valid dismissal?", new[]
        {
            "Equipment. Teams will get the equipment (balls, stumps, cones, measuring tape and kit bag) directly.",
            "LBW. LBW is not a form of dismissal in ARCL. If the ball hits the legs of the batsman first it is not out.",
        });
        Assert.True(scores[1] > scores[0], $"expected the LBW chunk to win, got {scores[0]} vs {scores[1]}");
    }
}
