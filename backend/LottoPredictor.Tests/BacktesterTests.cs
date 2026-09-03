using LottoPredictor.Core.Analysis;
using static LottoPredictor.Tests.TestData;

namespace LottoPredictor.Tests;

public class BacktesterTests
{
    [Fact]
    public void Match_distribution_sums_to_evaluated_count()
    {
        var draws = RandomHistory(300, 59);
        var report = Backtester.Run(draws, ScoringStrategy.Candidates, evalWindow: 100, warmup: 150);

        foreach (var s in report.Strategies)
        {
            Assert.Equal(100, s.Evaluated);
            Assert.Equal(100, s.MatchCounts.Sum());
        }
        Assert.Equal(100, report.RandomSimulated.MatchCounts.Sum());
    }

    [Fact]
    public void No_future_data_leakage_prefix_view_hides_later_draws()
    {
        // Two datasets share their first 200 draws but have completely different futures.
        // The backtester's prefix view over each must yield identical predictions: if any
        // future information reached the engine, these would diverge.
        var shared = RandomHistory(200, 59, seed: 1);
        var futureA = RandomHistory(100, 59, seed: 2).Select((d, i) => d with { Sequence = 201 + i }).ToList();
        var futureB = RandomHistory(100, 59, seed: 3).Select((d, i) => d with { Sequence = 201 + i }).ToList();
        var datasetA = shared.Concat(futureA).ToList();
        var datasetB = shared.Concat(futureB).ToList();

        foreach (var strategy in ScoringStrategy.Candidates)
        {
            var fromA = PredictionEngine.Generate(
                FeatureCalculator.Compute(Backtester.Prefix(datasetA, 200)), strategy);
            var fromB = PredictionEngine.Generate(
                FeatureCalculator.Compute(Backtester.Prefix(datasetB, 200)), strategy);
            Assert.Equal(fromA.Numbers, fromB.Numbers);
            Assert.Equal(fromA.SetScore, fromB.SetScore, 10);
        }
    }

    [Fact]
    public void Prefix_view_cannot_reach_beyond_its_count()
    {
        var draws = RandomHistory(50, 59);
        var prefix = Backtester.Prefix(draws, 30);
        Assert.Equal(30, prefix.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => prefix[30]);
        Assert.Equal(30, prefix.Count());
    }

    [Fact]
    public void Appending_future_draws_does_not_change_a_historical_prediction()
    {
        // Same cutoff, dataset later extended: the historical prediction must be unchanged.
        var draws = RandomHistory(300, 59);
        var strategy = ScoringStrategy.Candidates[0];

        var shortDataset = draws.Take(250).ToList();
        var before = PredictionEngine.Generate(
            FeatureCalculator.Compute(Backtester.Prefix(shortDataset, 250)), strategy);

        var extendedDataset = draws; // 50 additional future draws now exist
        var after = PredictionEngine.Generate(
            FeatureCalculator.Compute(Backtester.Prefix(extendedDataset, 250)), strategy);

        Assert.Equal(before.Numbers, after.Numbers);
    }

    [Fact]
    public void Random_expectation_matches_hypergeometric_mean()
    {
        // 6 picked of 6 drawn from 59 -> E[matches] = 36/59.
        var dist = Backtester.HypergeometricMatchDistribution(59);
        Assert.Equal(1.0, dist.Sum(), 6);
        double mean = dist.Select((p, k) => p * k).Sum();
        Assert.Equal(36.0 / 59.0, mean, 6);
    }

    [Fact]
    public void On_synthetic_random_data_verdict_reports_no_advantage()
    {
        // With genuinely random draws no strategy should beat the baseline significantly.
        var draws = RandomHistory(600, 59, seed: 7);
        var report = Backtester.Run(draws, ScoringStrategy.Candidates, evalWindow: 300, warmup: 200);
        Assert.Contains("No measurable advantage", report.Verdict);
    }

    [Fact]
    public void Best_strategy_detects_a_planted_pattern()
    {
        // Plant a strong pattern in a full 1-59 pool: numbers 1-5 in every draw plus a rotating
        // sixth. Frequency-based strategies must find it and match at least five every time.
        var draws = new List<DrawEvent>();
        for (int i = 1; i <= 300; i++)
        {
            draws.Add(Ev(i, 1, 2, 3, 4, 5, 7 + (i % 53)));
        }
        var report = Backtester.Run(draws, ScoringStrategy.Candidates, evalWindow: 100, warmup: 150);
        Assert.InRange(report.Best.AvgMatches, 5.0, 6.0);
        Assert.DoesNotContain("No measurable advantage", report.Verdict);
    }

    [Fact]
    public void Counts_matches_correctly()
    {
        Assert.Equal(6, Backtester.CountMatches([1, 2, 3, 4, 5, 6], [1, 2, 3, 4, 5, 6]));
        Assert.Equal(0, Backtester.CountMatches([1, 2, 3, 4, 5, 6], [7, 8, 9, 10, 11, 12]));
        Assert.Equal(3, Backtester.CountMatches([1, 2, 3, 40, 50, 59], [1, 2, 3, 41, 51, 58]));
    }

    [Fact]
    public void Throws_when_not_enough_history()
    {
        var draws = RandomHistory(50, 59);
        Assert.Throws<InvalidOperationException>(
            () => Backtester.Run(draws, ScoringStrategy.Candidates, evalWindow: 100, warmup: 150));
    }
}
