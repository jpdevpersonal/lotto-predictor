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
            Assert.Equal(report.HoldoutEvaluated, s.Evaluated);
            Assert.Equal(report.HoldoutEvaluated, s.MatchCounts.Sum());
        }
        Assert.Equal(66, report.SelectionEvaluated);
        Assert.Equal(34, report.HoldoutEvaluated);
        Assert.Equal(report.HoldoutEvaluated, report.RandomSimulated.MatchCounts.Sum());
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
        Assert.Equal(0.01086410, Backtester.ThreePlusProbability(59, 6), 8);
        Assert.Equal(dist.Skip(3).Sum(), Backtester.ThreePlusProbability(59, 6), 12);
        Assert.Equal(Backtester.HypergeometricMatchDistribution(50, 5).Skip(3).Sum(),
            Backtester.ThreePlusProbability(50, 5), 12);
        Assert.Equal(0, Backtester.ThreePlusProbability(12, 2));
    }

    [Fact]
    public void Wilson_interval_handles_zero_three_plus_hits()
    {
        var (low, high) = Backtester.WilsonInterval(0, 200);
        Assert.Equal(0, low);
        Assert.InRange(high, 0.01, 0.02);
    }

    [Fact]
    public void Newly_eligible_pool_numbers_are_scoreable_before_ten_appearances()
    {
        var draws = Enumerable.Range(1, 160)
            .Select(i => new DrawEvent(
                i,
                i,
                i < 151 ? new DateOnly(2015, 10, 3) : new DateOnly(2015, 10, 17).AddDays(i - 151),
                i < 151
                    ? [1, 2, 3, 4, 5, 6]
                    : [1, 2, 3, 4, 5, 50 + ((i - 151) % 10)]))
            .ToList();

        var fs = FeatureCalculator.Compute(
            draws, configuredPoolSize: 59, poolExpansionDate: new DateOnly(2015, 10, 10));
        var scores = PredictionEngine.ScoreNumbers(fs, ScoringStrategy.Candidates[0]);

        Assert.Contains(50, scores.Keys);
        Assert.Contains(59, scores.Keys);
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
    public void Best_strategy_is_selected_before_the_held_out_period()
    {
        var draws = RandomHistory(300, 59, seed: 12);
        var report = Backtester.Run(draws, ScoringStrategy.Candidates, evalWindow: 90, warmup: 150);

        Assert.Equal(60, report.SelectionEvaluated);
        Assert.Equal(30, report.HoldoutEvaluated);
        Assert.Equal(30, report.Best.Evaluated);
        Assert.Contains("held-out", report.Verdict);
        Assert.All(report.SelectionStrategies, s => Assert.Equal(60, s.Evaluated));
        Assert.All(report.Strategies, s =>
            Assert.Equal(s.MatchCounts.Skip(3).Sum(), s.ThreePlusHits));
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

    [Fact]
    public void Default_validation_window_is_longer_and_small_holdouts_warn_about_precision()
    {
        Assert.Equal(1000, new LottoPredictor.Core.Services.AnalysisOptions().BacktestEvalWindow);
        Assert.Equal(1066, Backtester.SelectionCutoff(1400, 1000, 150));
        var report = Backtester.Run(RandomHistory(180, 59), [ScoringStrategy.Candidates[0]],
            evalWindow: 12, warmup: 150);
        Assert.Equal(8, report.SelectionEvaluated);
        Assert.Equal(4, report.HoldoutEvaluated);
        Assert.Contains("precision is limited", report.Verdict);
    }

    [Fact]
    public void Uniform_set_probabilities_have_zero_information_against_uniform()
    {
        var step = new Backtester.InformationStep(
            new double[10], Enumerable.Range(0, 10).Select(i => i < 3).ToArray());
        var result = Backtester.InformationTest([step, step], [step, step]);
        Assert.Equal(0, result.LogScore, 12);
        Assert.Equal(0, result.Z);
    }

    [Fact]
    public void Information_test_is_zero_at_zero_temperature_and_positive_for_a_real_signal()
    {
        // Scores that perfectly rank the drawn balls must earn a positive temperature and a
        // clearly positive held-out log score; unrelated scores must not.
        var rng = new Random(5);
        Backtester.InformationStep Informative()
        {
            var drawn = new bool[59];
            foreach (var n in RandomSet(rng)) drawn[n - 1] = true;
            var scores = Enumerable.Range(0, 59).Select(i => (drawn[i] ? 2.0 : 0.0) + rng.NextDouble() * 0.1).ToArray();
            return new Backtester.InformationStep(scores, drawn);
        }
        Backtester.InformationStep Noise()
        {
            var drawn = new bool[59];
            foreach (var n in RandomSet(rng)) drawn[n - 1] = true;
            return new Backtester.InformationStep(
                Enumerable.Range(0, 59).Select(_ => rng.NextDouble()).ToArray(), drawn);
        }

        var signal = Backtester.InformationTest(
            Enumerable.Range(0, 100).Select(_ => Informative()).ToList(),
            Enumerable.Range(0, 100).Select(_ => Informative()).ToList());
        Assert.True(signal.Temperature > 0.5);
        Assert.True(signal.LogScore > 1.0);
        Assert.True(signal.Z > 5);

        var noise = Backtester.InformationTest(
            Enumerable.Range(0, 200).Select(_ => Noise()).ToList(),
            Enumerable.Range(0, 200).Select(_ => Noise()).ToList());
        Assert.InRange(noise.Z, -3, 3);
        Assert.InRange(noise.LogScore, -0.2, 0.2);

        static int[] RandomSet(Random rng)
        {
            var set = new HashSet<int>();
            while (set.Count < 6) set.Add(rng.Next(1, 60));
            return [.. set];
        }
    }

    [Fact]
    public void Verdict_reports_the_information_test_on_random_data()
    {
        var draws = RandomHistory(600, 59, seed: 7);
        var report = Backtester.Run(draws, ScoringStrategy.Candidates, evalWindow: 300, warmup: 200);
        Assert.Contains("Information test", report.Verdict);
        Assert.Contains("carry no detectable information", report.Verdict);
        Assert.InRange(report.InformationZ, -3.5, 2.326);
    }

    [Fact]
    public void Information_test_detects_a_planted_pattern()
    {
        var draws = new List<DrawEvent>();
        for (int i = 1; i <= 300; i++) draws.Add(Ev(i, 1, 2, 3, 4, 5, 7 + (i % 53)));
        var report = Backtester.Run(draws, ScoringStrategy.Candidates, evalWindow: 100, warmup: 150);
        Assert.True(report.InformationZ > 2.326);
        Assert.True(report.InformationLogScore > 0);
        Assert.Contains("confirm this prospectively", report.Verdict);
    }
}
