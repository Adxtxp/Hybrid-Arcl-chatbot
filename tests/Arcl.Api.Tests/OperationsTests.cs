using Arcl.Api.Services;
using Arcl.Ingestion;

namespace Arcl.Api.Tests;

public class RecrawlPlannerTests
{
    private static readonly DateOnly Today = new(2026, 11, 16);
    private static ChunkResult Chunk(string number, string content) => new("mens", number, $"Title {number}", content, "https://arcl.org/x", Today);

    private static readonly Dictionary<string, string> Current = new()
    {
        ["1"] = new string('a', 300), ["2"] = new string('b', 300), ["3"] = new string('c', 300),
        ["4"] = new string('d', 300), ["5"] = new string('e', 300), ["6"] = new string('f', 300),
    };

    private static List<ChunkResult> Unchanged() => Current.Select(kv => Chunk(kv.Key, kv.Value)).ToList();

    [Fact]
    public void Identical_page_is_unchanged()
    {
        Assert.Equal(RecrawlDecision.Unchanged, RecrawlPlanner.Plan(Unchanged(), Array.Empty<SectionAnchor>(), Current).Decision);
    }

    [Fact]
    public void One_edited_section_is_applied()
    {
        var chunks = Unchanged();
        chunks[2] = Chunk("3", new string('c', 280) + " new sentence about runners.");
        var plan = RecrawlPlanner.Plan(chunks, Array.Empty<SectionAnchor>(), Current);
        Assert.Equal(RecrawlDecision.Apply, plan.Decision);
        Assert.Equal("3", Assert.Single(plan.Changed).SectionNumber);
    }

    [Fact]
    public void Missing_heading_needs_review()
    {
        var plan = RecrawlPlanner.Plan(Unchanged(), new[] { new SectionAnchor("7", "Players") }, Current);
        Assert.Equal(RecrawlDecision.NeedsReview, plan.Decision);
        Assert.Contains("Players", plan.Reason);
    }

    [Fact]
    public void Sharp_shrink_needs_review()
    {
        // The real bug this guards against: a false heading match cut "Caught Out" to 4 characters.
        var chunks = Unchanged();
        chunks[1] = Chunk("2", "· No");
        Assert.Equal(RecrawlDecision.NeedsReview, RecrawlPlanner.Plan(chunks, Array.Empty<SectionAnchor>(), Current).Decision);
    }

    [Fact]
    public void Most_of_page_changing_needs_review()
    {
        var chunks = Current.Select(kv => Chunk(kv.Key, kv.Value + " edited")).ToList();
        Assert.Equal(RecrawlDecision.NeedsReview, RecrawlPlanner.Plan(chunks, Array.Empty<SectionAnchor>(), Current).Decision);
    }

    [Fact]
    public void Empty_page_needs_review()
    {
        Assert.Equal(RecrawlDecision.NeedsReview,
            RecrawlPlanner.Plan(Array.Empty<ChunkResult>(), Array.Empty<SectionAnchor>(), Current).Decision);
    }
}

public class DriftCheckTests
{
    [Fact]
    public void Too_little_traffic_is_never_drift()
    {
        Assert.Null(DriftCheck.Evaluate(new WindowStats(10, 0.40, 0.9), new WindowStats(500, 0.20, 0.1)));
    }

    [Fact]
    public void Stable_numbers_are_not_drift()
    {
        Assert.Null(DriftCheck.Evaluate(new WindowStats(100, 0.215, 0.12), new WindowStats(400, 0.210, 0.10)));
    }

    [Fact]
    public void Rising_distance_is_drift()
    {
        var reason = DriftCheck.Evaluate(new WindowStats(100, 0.26, 0.12), new WindowStats(400, 0.21, 0.10));
        Assert.NotNull(reason);
        Assert.Contains("distance", reason);
    }

    [Fact]
    public void Rising_no_answer_share_is_drift()
    {
        var reason = DriftCheck.Evaluate(new WindowStats(100, 0.21, 0.30), new WindowStats(400, 0.21, 0.10));
        Assert.NotNull(reason);
        Assert.Contains("no confident answer", reason);
    }
}

public class BudgetStateTests
{
    [Theory]
    [InlineData(1.00, false, false)]
    [InlineData(8.00, true, false)]
    [InlineData(10.00, true, true)]
    [InlineData(12.50, true, true)]
    public void Thresholds(decimal spent, bool warning, bool exhausted)
    {
        var s = new BudgetState(spent, 10m, 80);
        Assert.Equal(warning, s.Warning);
        Assert.Equal(exhausted, s.Exhausted);
    }

    [Fact]
    public void Zero_cap_means_no_cap()
    {
        var s = new BudgetState(1000m, 0m, 80);
        Assert.False(s.Exhausted);
        Assert.False(s.Warning);
    }
}

public class MemorySelectorTests
{
    private static readonly ConversationTurn NoBall = new("Is a hit wicket out on a no-ball?", "No, hit wicket is not out on a no-ball [§44].");
    private static readonly ConversationTurn Scorecard = new("How do I upload my team's scorecard?", "1. Log in. 2. Click your division, then the scorecard tab [§FAQ-08].");
    private static readonly ConversationTurn Rain = new("What happens if rain stops the match?", "If the match is abandoned both teams get 0 points [§28].");

    [Fact]
    public void Follow_up_keeps_the_previous_turn_even_with_no_shared_words()
    {
        var kept = MemorySelector.Select(new[] { Scorecard, NoBall }, "and if they're caught?", 4);
        Assert.Contains(NoBall, kept);
        Assert.DoesNotContain(Scorecard, kept);
    }

    [Fact]
    public void Fresh_topic_drops_unrelated_turns()
    {
        var kept = MemorySelector.Select(new[] { NoBall, Rain }, "How do I upload my team's scorecard after the game?", 4);
        Assert.Empty(kept);
    }

    [Fact]
    public void Related_older_turn_is_kept_in_order()
    {
        var kept = MemorySelector.Select(new[] { Scorecard, NoBall, Rain }, "Can a no-ball also be a hit wicket in the youth league?", 4);
        Assert.Equal(new[] { NoBall }, kept);
    }

    [Fact]
    public void Never_more_than_max_turns_and_prefers_recent()
    {
        var turns = Enumerable.Range(1, 6).Select(i => new ConversationTurn($"no-ball rule question {i}", "answer")).ToList();
        var kept = MemorySelector.Select(turns, "what is the no-ball rule exactly", 2);
        Assert.Equal(new[] { turns[4], turns[5] }, kept);
    }
}
