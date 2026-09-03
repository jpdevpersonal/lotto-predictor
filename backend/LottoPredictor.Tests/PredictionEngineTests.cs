using LottoPredictor.Core.Analysis;
using static LottoPredictor.Tests.TestData;

namespace LottoPredictor.Tests;

public class PredictionEngineTests
{
    [Fact]
    public void Returns_six_unique_numbers_within_pool()
    {
        var draws = RandomHistory(300, 59);
        var fs = FeatureCalculator.Compute(draws);

        foreach (var strategy in ScoringStrategy.Candidates)
        {
            var result = PredictionEngine.Generate(fs, strategy);
            Assert.Equal(6, result.Numbers.Length);
            Assert.Equal(6, result.Numbers.Distinct().Count());
            Assert.All(result.Numbers, n => Assert.InRange(n, 1, 59));
            Assert.Equal(result.Numbers.OrderBy(x => x), result.Numbers);
        }
    }

    [Fact]
    public void Is_deterministic()
    {
        var draws = RandomHistory(250, 59);
        var strategy = ScoringStrategy.Candidates[0];
        var a = PredictionEngine.Generate(FeatureCalculator.Compute(draws), strategy);
        var b = PredictionEngine.Generate(FeatureCalculator.Compute(draws), strategy);
        Assert.Equal(a.Numbers, b.Numbers);
        Assert.Equal(a.SetScore, b.SetScore);
    }

    [Fact]
    public void Long_term_frequency_strategy_prefers_frequent_numbers()
    {
        // Numbers 1-6 appear in every draw; the rest rotate rarely.
        var draws = new List<DrawEvent>();
        for (int i = 1; i <= 100; i++)
        {
            draws.Add(Ev(i, 1, 2, 3, 4, 5, 7 + (i % 42)));
        }
        var fs = FeatureCalculator.Compute(draws);
        var scores = PredictionEngine.ScoreNumbers(fs, new ScoringStrategy("f", 1, 0, 0, 0, 0, 0));
        // Every always-drawn number must outscore every rarely-drawn number.
        foreach (var frequent in new[] { 1, 2, 3, 4, 5 })
            foreach (var rare in new[] { 10, 20, 30, 40 })
                Assert.True(scores[frequent] > scores[rare],
                    $"expected score[{frequent}] > score[{rare}]");
    }

    [Fact]
    public void Typicality_penalty_is_higher_for_extreme_sets()
    {
        var draws = RandomHistory(500, 59);
        var fs = FeatureCalculator.Compute(draws);
        double typical = PredictionEngine.TypicalityPenalty(fs, [8, 17, 25, 33, 44, 55]);
        double extreme = PredictionEngine.TypicalityPenalty(fs, [1, 2, 3, 4, 5, 6]);
        Assert.True(extreme > typical);
    }

    [Fact]
    public void Scoring_only_covers_eligible_numbers()
    {
        // Single-era 49 pool: numbers above 49 must not be scored.
        var draws = RandomHistory(200, 49);
        var fs = FeatureCalculator.Compute(draws);
        var scores = PredictionEngine.ScoreNumbers(fs, ScoringStrategy.Candidates[0]);
        Assert.All(scores.Keys, n => Assert.InRange(n, 1, 49));
    }

    [Fact]
    public void Top_lines_are_distinct_sorted_and_consensus_is_valid()
    {
        var draws = RandomHistory(300, 59);
        var fs = FeatureCalculator.Compute(draws);
        var strategy = ScoringStrategy.Candidates[0];
        var scores = PredictionEngine.ScoreNumbers(fs, strategy);

        var lines = PredictionEngine.GenerateTopLines(fs, scores, strategy, 50);

        Assert.Equal(50, lines.Count);
        Assert.Equal(50, lines.Select(l => string.Join(",", l.Numbers)).Distinct().Count());
        for (int i = 1; i < lines.Count; i++)
            Assert.True(lines[i - 1].SetScore >= lines[i].SetScore);
        // Line 1 must equal the single best prediction.
        Assert.Equal(PredictionEngine.Generate(fs, strategy).Numbers, lines[0].Numbers);

        var (numbers, frequencies) = PredictionEngine.Consensus(lines, scores);
        Assert.Equal(6, numbers.Distinct().Count());
        Assert.Equal(numbers.OrderBy(x => x), numbers);
        Assert.All(frequencies, f => Assert.InRange(f, 1, 50));
    }
}
