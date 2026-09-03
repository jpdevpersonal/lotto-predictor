using LottoPredictor.Core.Analysis;
using static LottoPredictor.Tests.TestData;

namespace LottoPredictor.Tests;

public class LearningEngineTests
{
    [Fact]
    public void Inverse_normal_cdf_matches_known_quantiles()
    {
        Assert.Equal(1.959964, StatFunctions.InverseNormalCdf(0.975), 4);
        Assert.Equal(0.0, StatFunctions.InverseNormalCdf(0.5), 6);
        Assert.Equal(2.575829, StatFunctions.InverseNormalCdf(0.995), 4);
    }

    [Fact]
    public void Chi_square_p_value_is_sane()
    {
        // For df=58, mean of the distribution is 58: p should be ~0.5.
        Assert.InRange(StatFunctions.ChiSquarePValue(58, 58), 0.4, 0.6);
        // Extreme statistic: essentially zero.
        Assert.True(StatFunctions.ChiSquarePValue(300, 58) < 1e-6);
        // Tiny statistic: essentially one.
        Assert.True(StatFunctions.ChiSquarePValue(5, 58) > 0.99);
    }

    [Fact]
    public void Fair_data_yields_small_bias_z_and_uniform_chi_square()
    {
        var draws = RandomHistory(600, 59, seed: 11);
        var fs = FeatureCalculator.Compute(draws);

        // On uniform data BiasZ ~ N(0,1): the vast majority within |z| < 4.
        Assert.All(fs.Numbers, f => Assert.InRange(f.BiasZ, -4.5, 4.5));
        Assert.True(fs.ChiSquarePValue > 0.001);
    }

    [Fact]
    public void Biased_ball_produces_large_bias_z()
    {
        // Number 7 appears in every draw: a maximally biased ball.
        var draws = new List<DrawEvent>();
        var rng = new Random(3);
        for (int i = 1; i <= 300; i++)
        {
            var set = new HashSet<int> { 7 };
            while (set.Count < 6) set.Add(rng.Next(1, 60));
            draws.Add(new DrawEvent(i, i, new DateOnly(2020, 1, 1).AddDays(i), [.. set.OrderBy(x => x)]));
        }
        var fs = FeatureCalculator.Compute(draws);

        Assert.True(fs.For(7).BiasZ > 10, $"BiasZ was {fs.For(7).BiasZ}");
        Assert.True(fs.ChiSquarePValue < 0.001);

        // The bias-detector strategy must rank the biased ball first.
        var biasDetector = ScoringStrategy.Candidates.First(s => s.Name == "bias-detector");
        var scores = PredictionEngine.ScoreNumbers(fs, biasDetector);
        Assert.Equal(7, scores.OrderByDescending(kv => kv.Value).First().Key);
    }

    [Fact]
    public void Hedge_ensemble_is_evaluated_and_weights_are_a_distribution()
    {
        var draws = RandomHistory(400, 59, seed: 21);
        var report = Backtester.Run(draws, ScoringStrategy.Candidates, evalWindow: 150, warmup: 200);

        var ensemble = report.Strategies.Single(s => s.Strategy.Name == Backtester.EnsembleName);
        Assert.Equal(150, ensemble.MatchCounts.Sum());
        Assert.Equal(1.0, report.HedgeWeights.Values.Sum(), 6);
        Assert.All(report.HedgeWeights.Values, w => Assert.InRange(w, 0.0, 1.0));
    }

    [Fact]
    public void Hedge_weights_concentrate_on_the_winning_strategy_for_a_planted_pattern()
    {
        // Numbers 1-5 in every draw: frequency-style strategies dominate, so the hedge should
        // shift weight away from strategies that ignore frequency (e.g. overdue-gap).
        var draws = new List<DrawEvent>();
        for (int i = 1; i <= 300; i++)
            draws.Add(Ev(i, 1, 2, 3, 4, 5, 7 + (i % 53)));

        var report = Backtester.Run(draws, ScoringStrategy.Candidates, evalWindow: 100, warmup: 150);

        double freqWeight = report.HedgeWeights["long-term-frequency"];
        double gapWeight = report.HedgeWeights["overdue-gap"];
        Assert.True(freqWeight > gapWeight,
            $"freq={freqWeight}, gap={gapWeight}: hedge failed to learn the winner");

        // And the ensemble itself must exploit the pattern.
        var ensemble = report.Strategies.Single(s => s.Strategy.Name == Backtester.EnsembleName);
        Assert.InRange(ensemble.AvgMatches, 4.5, 6.0);
    }

    [Fact]
    public void Blend_scores_weights_strategies_proportionally()
    {
        var a = new Dictionary<int, double> { [1] = 2.0, [2] = 0.0, [3] = -2.0 };
        var b = new Dictionary<int, double> { [1] = -2.0, [2] = 0.0, [3] = 2.0 };

        // Equal weights cancel exactly after z-normalisation.
        var blended = PredictionEngine.BlendScores([(a, 0.5), (b, 0.5)]);
        Assert.All(blended.Values, v => Assert.Equal(0.0, v, 9));

        // Dominant weight wins.
        var dominated = PredictionEngine.BlendScores([(a, 0.9), (b, 0.1)]);
        Assert.True(dominated[1] > dominated[3]);
    }

    [Fact]
    public void Optimizer_candidates_are_normalised_and_deterministic()
    {
        var seeds = ScoringStrategy.Candidates;
        var g5a = StrategyOptimizer.GenerateCandidates(seeds, 5);
        var g5b = StrategyOptimizer.GenerateCandidates(seeds, 5);
        var g6 = StrategyOptimizer.GenerateCandidates(seeds, 6);

        Assert.Equal(g5a.Select(s => s.Describe()), g5b.Select(s => s.Describe()));
        Assert.NotEqual(g5a.Select(s => s.Describe()), g6.Select(s => s.Describe()));

        foreach (var c in g5a)
        {
            double sum = c.WLongTerm + c.WRecent + c.WGap + c.WMomentum + c.WBias;
            Assert.Equal(1.0, sum, 2);
            Assert.InRange(c.PairWeight, 0.0, 1.0);
            Assert.InRange(c.PenaltyWeight, 0.0, 2.0);
            Assert.StartsWith("learned-g5-", c.Name);
        }
    }
}
