using LottoPredictor.Core.Analysis;
using static LottoPredictor.Tests.TestData;

namespace LottoPredictor.Tests;

public class PortfolioOptimizerTests
{
    [Fact]
    public void Coverage_portfolio_returns_distinct_fixed_k_lines()
    {
        var history = RandomHistory(260, 59, seed: 123);
        var fs = FeatureCalculator.Compute(history, configuredPoolSize: 59);
        var strategy = ScoringStrategy.Candidates[0];
        var portfolio = PortfolioOptimizer.BuildCoveragePortfolio(
            fs, PredictionEngine.ScoreNumbers(fs, strategy), strategy, lineCount: 12);

        Assert.Equal(12, portfolio.Lines.Count);
        Assert.Equal(12, portfolio.Lines.Select(line => string.Join(',', line.Numbers)).Distinct().Count());
        Assert.All(portfolio.Lines, line => Assert.Equal(6, line.Numbers.Length));
        Assert.All(portfolio.Lines, line => Assert.Equal(line.Numbers, line.Numbers.OrderBy(number => number)));
    }

    [Fact]
    public void Coverage_portfolio_improves_or_matches_random_distinct_three_plus_estimate()
    {
        var history = RandomHistory(260, 59, seed: 456);
        var fs = FeatureCalculator.Compute(history, configuredPoolSize: 59);
        var strategy = ScoringStrategy.Candidates[0];
        var portfolio = PortfolioOptimizer.BuildCoveragePortfolio(
            fs, PredictionEngine.ScoreNumbers(fs, strategy), strategy, lineCount: 25);

        Assert.True(portfolio.CoverageOptimized.Probability >= portfolio.RandomDistinct.Probability * 0.95);
        Assert.True(portfolio.CoverageOptimized.Probability >= portfolio.SingleLineThreePlusProbability);
    }

    [Fact]
    public void Low_overlap_six_ball_portfolio_reports_estimates_not_exact_union_bound()
    {
        var history = RandomHistory(260, 59, seed: 42);
        var fs = FeatureCalculator.Compute(history, configuredPoolSize: 59);
        var strategy = ScoringStrategy.Candidates[0];
        var portfolio = PortfolioOptimizer.BuildCoveragePortfolio(
            fs, PredictionEngine.ScoreNumbers(fs, strategy), strategy, lineCount: 60);

        Assert.All(portfolio.Lines, line => Assert.All(line.Numbers, n => Assert.InRange(n, 1, 59)));
        for (int i = 0; i < portfolio.Lines.Count; i++)
            for (int j = i + 1; j < portfolio.Lines.Count; j++)
                Assert.True(portfolio.Lines[i].Numbers.Intersect(portfolio.Lines[j].Numbers).Count() <= 1);
        Assert.All(portfolio.Lines, line => Assert.InRange(line.MaxOverlapWithEarlier, 0, 1));
        Assert.False(portfolio.Odds.IsExact);
        Assert.Equal(1, portfolio.Odds.MaxPairwiseOverlap);
        Assert.True(portfolio.Odds.ThreePlusProbability < 60 * portfolio.SingleLineThreePlusProbability);
        Assert.Equal(portfolio.CoverageOptimized.Probability, portfolio.Odds.ThreePlusProbability);
        Assert.InRange(portfolio.Odds.ThreePlusProbability,
            portfolio.CoverageOptimized.CiLow, portfolio.CoverageOptimized.CiHigh);
    }

    [Fact]
    public void Large_portfolio_reports_a_valid_estimate_when_pairwise_bounds_are_uninformative()
    {
        var history = RandomHistory(260, 59, seed: 99);
        var fs = FeatureCalculator.Compute(history, configuredPoolSize: 59);
        var strategy = ScoringStrategy.Candidates[0];
        var portfolio = PortfolioOptimizer.BuildCoveragePortfolio(
            fs, PredictionEngine.ScoreNumbers(fs, strategy), strategy, lineCount: 200);

        Assert.Equal(200, portfolio.Lines.Select(line => string.Join(',', line.Numbers)).Distinct().Count());
        Assert.True(portfolio.Odds.MaxPairwiseOverlap <= 2);
        Assert.False(portfolio.Odds.IsExact);
        Assert.InRange(portfolio.Odds.ThreePlusProbability, portfolio.SingleLineThreePlusProbability, 1);
        Assert.Equal(portfolio.CoverageOptimized.Probability, portfolio.Odds.ThreePlusProbability);
    }

    [Fact]
    public void Pairwise_three_plus_overlap_matches_brute_force_enumeration()
    {
        const int pool = 14, pick = 6;
        int[] a = [1, 2, 3, 4, 5, 6];
        foreach (int shared in new[] { 0, 1, 2, 3, 4 })
        {
            var b = Enumerable.Range(1, shared).Concat(Enumerable.Range(7, 6 - shared)).ToArray();
            int both = 0, total = 0;
            foreach (var draw in AllDraws(pool, pick))
            {
                total++;
                if (draw.Intersect(a).Count() >= 3 && draw.Intersect(b).Count() >= 3) both++;
            }
            Assert.Equal((double)both / total, PortfolioOptimizer.PairwiseThreePlusProbability(pool, pick, shared), 12);
            Assert.True(both > 0);
            var odds = PortfolioOptimizer.ComputeOdds([a, b], pool, pick);
            Assert.True(odds.IsExact);
            Assert.Equal(2 * Backtester.ThreePlusProbability(pool, pick) - (double)both / total,
                odds.ThreePlusProbability, 12);
        }
    }

    [Fact]
    public void Lines_for_target_uses_two_rounds_per_night()
    {
        double p = Backtester.ThreePlusProbability(59, 6);
        int oneRound = PortfolioOptimizer.LinesForTarget(0.5, p, 1);
        int twoRounds = PortfolioOptimizer.LinesForTarget(0.5, p, 2);
        Assert.Equal(47, oneRound);
        Assert.Equal(27, twoRounds);
        Assert.Equal(0, PortfolioOptimizer.LinesForTarget(0, p, 2));
    }

    private static IEnumerable<int[]> AllDraws(int pool, int pick)
    {
        var idx = Enumerable.Range(1, pick).ToArray();
        while (true)
        {
            yield return (int[])idx.Clone();
            int pos = pick - 1;
            while (pos >= 0 && idx[pos] == pool - pick + pos + 1) pos--;
            if (pos < 0) yield break;
            idx[pos]++;
            for (int i = pos + 1; i < pick; i++) idx[i] = idx[i - 1] + 1;
        }
    }

    [Fact]
    public void Single_line_simulation_agrees_with_closed_form_three_plus_odds()
    {
        double exact = 489509.0 / 45057474.0;
        Assert.Equal(exact, Backtester.ThreePlusProbability(59, 6), 10);

        var sim = PortfolioOptimizer.Simulate([[1, 2, 3, 4, 5, 6]], 59, 6, randomSeed: 7, trials: 1_000_000);
        Assert.InRange(exact, sim.CiLow, sim.CiHigh);
    }

    [Fact]
    public void Disjoint_five_ball_lines_have_mutually_exclusive_three_plus_hits()
    {
        var odds = PortfolioOptimizer.ComputeOdds([[1, 2, 3, 4, 5], [6, 7, 8, 9, 10]], 50, 5);
        Assert.True(odds.IsExact);
        Assert.Equal(2 * Backtester.ThreePlusProbability(50, 5), odds.ThreePlusProbability, 12);
    }

    [Fact]
    public void Five_number_games_are_generated_uniformly_at_their_actual_pick_count()
    {
        var history = RandomHistory(100, 50, seed: 789, pickCount: 5);

        Assert.All(history, draw => Assert.Equal(5, draw.Numbers.Length));
        Assert.Contains(history, draw => draw.Numbers.Any(number => number > 5));
    }

    [Fact]
    public void Coverage_portfolio_rejects_more_lines_than_the_legal_pool_can_supply()
    {
        var history = RandomHistory(60, 6, seed: 321);
        var fs = FeatureCalculator.Compute(history, configuredPoolSize: 6);
        var strategy = ScoringStrategy.Candidates[0];

        Assert.Throws<InvalidOperationException>(() => PortfolioOptimizer.BuildCoveragePortfolio(
            fs, PredictionEngine.ScoreNumbers(fs, strategy), strategy, lineCount: 2));
    }
}
