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
    public void Coverage_portfolio_improves_or_matches_random_distinct_four_plus_estimate()
    {
        var history = RandomHistory(260, 59, seed: 456);
        var fs = FeatureCalculator.Compute(history, configuredPoolSize: 59);
        var strategy = ScoringStrategy.Candidates[0];
        var portfolio = PortfolioOptimizer.BuildCoveragePortfolio(
            fs, PredictionEngine.ScoreNumbers(fs, strategy), strategy, lineCount: 25);

        Assert.True(portfolio.CoverageOptimized.Probability >= portfolio.RandomDistinct.Probability * 0.95);
        Assert.True(portfolio.CoverageOptimized.Probability >= portfolio.SingleLineFourPlusProbability);
    }

    [Fact]
    public void Coverage_portfolio_lines_pairwise_share_at_most_one_ball_so_odds_are_exact()
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
        Assert.True(portfolio.Odds.IsExact);
        Assert.Equal(1, portfolio.Odds.MaxPairwiseOverlap);
        // Mutually exclusive 4+ events: the portfolio reaches the union bound K * p exactly.
        Assert.Equal(60 * portfolio.SingleLineFourPlusProbability, portfolio.Odds.FourPlusProbability, 12);
        Assert.InRange(portfolio.Odds.FourPlusProbability,
            portfolio.CoverageOptimized.CiLow, portfolio.CoverageOptimized.CiHigh);
    }

    [Fact]
    public void Large_portfolio_relaxes_overlap_gradually_and_stays_near_union_bound()
    {
        var history = RandomHistory(260, 59, seed: 99);
        var fs = FeatureCalculator.Compute(history, configuredPoolSize: 59);
        var strategy = ScoringStrategy.Candidates[0];
        var portfolio = PortfolioOptimizer.BuildCoveragePortfolio(
            fs, PredictionEngine.ScoreNumbers(fs, strategy), strategy, lineCount: 200);

        Assert.Equal(200, portfolio.Lines.Select(line => string.Join(',', line.Numbers)).Distinct().Count());
        Assert.True(portfolio.Odds.MaxPairwiseOverlap <= 2);
        // 200 lines use 3000 pair-slots over C(59,2)=1711 pairs, so some two-ball overlaps are
        // unavoidable; each costs only 36/C(59,6) of probability.
        double unionBound = 200 * portfolio.SingleLineFourPlusProbability;
        Assert.InRange(portfolio.Odds.FourPlusProbability, unionBound * 0.98, unionBound);
    }

    [Fact]
    public void Pairwise_four_plus_overlap_matches_brute_force_enumeration()
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
                if (draw.Intersect(a).Count() >= 4 && draw.Intersect(b).Count() >= 4) both++;
            }
            Assert.Equal((double)both / total, PortfolioOptimizer.PairwiseFourPlusProbability(pool, pick, shared), 12);
            if (shared <= 1) Assert.Equal(0, both);
        }
    }

    [Fact]
    public void Lines_for_target_uses_two_rounds_per_night()
    {
        double p = Backtester.FourPlusProbability(59, 6);
        int oneRound = PortfolioOptimizer.LinesForTarget(0.5, p, 1);
        int twoRounds = PortfolioOptimizer.LinesForTarget(0.5, p, 2);
        Assert.Equal(1074, oneRound);
        Assert.InRange(twoRounds, 620, 640);
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
    public void Single_line_simulation_agrees_with_closed_form_four_plus_odds()
    {
        double exact = 20989.0 / 45057474.0;
        Assert.Equal(exact, Backtester.FourPlusProbability(59, 6), 10);

        var sim = PortfolioOptimizer.Simulate([[1, 2, 3, 4, 5, 6]], 59, 6, randomSeed: 7, trials: 1_000_000);
        Assert.InRange(exact, sim.CiLow, sim.CiHigh);
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
