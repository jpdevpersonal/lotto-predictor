using LottoPredictor.Core.Analysis;
using static LottoPredictor.Tests.TestData;

namespace LottoPredictor.Tests;

public class FeatureCalculatorTests
{
    [Fact]
    public void Counts_total_frequency_and_windows()
    {
        // Number 7 appears in draws 1, 3 and 12 (of 12).
        var draws = new List<DrawEvent>();
        for (int i = 1; i <= 12; i++)
        {
            var numbers = i is 1 or 3 or 12
                ? new[] { 7, 20, 21, 22, 23, 24 }
                : new[] { 10, 20, 21, 22, 23, 24 };
            draws.Add(Ev(i, numbers));
        }

        var fs = FeatureCalculator.Compute(draws);
        var f = fs.For(7);

        Assert.Equal(3, f.TotalCount);
        Assert.Equal(12, f.EligibleDraws);
        Assert.Equal(3.0 / 12, f.FreqRate, 10);
        Assert.Equal(2, f.Count10);   // draws 3 and 12 fall within the last 10 (draws 3-12)
        Assert.Equal(3, f.Count25);   // window clamps to all 12 draws
        Assert.Equal(3, f.Count100);
    }

    [Fact]
    public void Short_history_counts_each_distinct_recent_window_once()
    {
        var draws = Enumerable.Range(1, 20)
            .Select(i => i >= 16
                ? Ev(i, 7, 20, 21, 22, 23, 24)
                : Ev(i, 10, 20, 21, 22, 23, 24))
            .ToList();

        var feature = FeatureCalculator.Compute(
            draws, configuredPoolSize: 59, poolExpansionDate: new DateOnly(2015, 10, 10)).For(7);
        double fairRate = 6.0 / 59.0;
        double expectedRecentRate = (5.0 / 10.0 + 5.0 / 20.0) / 2.0;
        double expectedShrunk = ((5 + 20 * fairRate) / 30.0 + (5 + 20 * fairRate) / 40.0) / 2.0;

        Assert.Equal(expectedRecentRate, feature.RecentRate, 10);
        Assert.Equal(expectedShrunk, feature.RecentRateShrunk, 10);
    }

    [Fact]
    public void Computes_draws_since_last_and_average_gap()
    {
        // Number 7 in draws 1, 3, 12 -> gaps 2 and 9, avg 5.5; last seen at draw 12 of 15 -> 3 since.
        var draws = new List<DrawEvent>();
        for (int i = 1; i <= 15; i++)
        {
            var numbers = i is 1 or 3 or 12
                ? new[] { 7, 20, 21, 22, 23, 24 }
                : new[] { 10, 20, 21, 22, 23, 24 };
            draws.Add(Ev(i, numbers));
        }

        var f = FeatureCalculator.Compute(draws).For(7);
        Assert.Equal(3, f.DrawsSinceLast);
        Assert.Equal(5.5, f.AvgGap, 10);
        Assert.Equal(3 / 5.5, f.GapRatio, 10);
    }

    [Fact]
    public void Never_seen_number_gets_full_gap()
    {
        var draws = Enumerable.Range(1, 20)
            .Select(i => Ev(i, 1, 2, 3, 4, 5, 49)).ToList();
        var f = FeatureCalculator.Compute(draws).For(30);
        Assert.Equal(0, f.TotalCount);
        Assert.Equal(20, f.DrawsSinceLast);
    }

    [Fact]
    public void Configured_pool_keeps_legal_unobserved_numbers_eligible()
    {
        var draws = Enumerable.Range(1, 20)
            .Select(i => Ev(i, 1, 2, 3, 4, 5, 48)).ToList();

        var fs = FeatureCalculator.Compute(
            draws, configuredPoolSize: 59, poolExpansionDate: new DateOnly(2015, 10, 10));

        Assert.Equal(59, fs.Pool.PoolSize);
        Assert.Equal(20, fs.For(59).EligibleDraws);
        Assert.Equal(0, fs.For(59).TotalCount);
    }

    [Fact]
    public void Configured_pool_rejects_observed_numbers_outside_it()
    {
        var draws = new List<DrawEvent> { Ev(1, 1, 2, 3, 4, 5, 50) };

        Assert.Throws<InvalidOperationException>(() =>
            FeatureCalculator.Compute(draws, configuredPoolSize: 49));
    }

    [Fact]
    public void Era_awareness_numbers_above_49_only_eligible_after_pool_change()
    {
        var draws = new List<DrawEvent>();
        for (int i = 1; i <= 100; i++) draws.Add(Ev(i, 1, 2, 3, 4, 5, 49));   // 49-pool era
        for (int i = 101; i <= 140; i++) draws.Add(Ev(i, 1, 2, 3, 4, 5, 59)); // 59-pool era

        var fs = FeatureCalculator.Compute(draws);
        Assert.Equal(59, fs.Pool.PoolSize);
        Assert.Equal(100, fs.Pool.Era2StartIndex);
        Assert.Equal(140, fs.For(10).EligibleDraws);
        Assert.Equal(40, fs.For(55).EligibleDraws);
        Assert.Equal(49, fs.Pool.PoolAt(50));
        Assert.Equal(59, fs.Pool.PoolAt(120));
    }

    [Fact]
    public void Counts_pairs()
    {
        var draws = new List<DrawEvent>
        {
            Ev(1, 1, 2, 3, 4, 5, 6),
            Ev(2, 1, 2, 10, 11, 12, 13),
            Ev(3, 7, 8, 10, 11, 20, 21),
        };
        var fs = FeatureCalculator.Compute(draws);
        Assert.Equal(2, fs.PairCounts[1, 2]);
        Assert.Equal(2, fs.PairCounts[10, 11]);
        Assert.Equal(0, fs.PairCounts[1, 20]);
    }

    [Fact]
    public void Counts_triples()
    {
        var draws = new List<DrawEvent>
        {
            Ev(1, 1, 2, 3, 10, 11, 12),
            Ev(2, 1, 2, 3, 20, 21, 22),
        };
        var fs = FeatureCalculator.Compute(draws);
        Assert.Equal(2, fs.TripleCounts[(1, 2, 3)]);
    }

    [Fact]
    public void Computes_set_level_distributions()
    {
        var draws = new List<DrawEvent>
        {
            Ev(1, 1, 2, 3, 4, 5, 6),      // sum 21, range 5, 3 odd, 5 consecutive
            Ev(2, 10, 20, 30, 40, 41, 48) // sum 189, range 38, 1 odd, 1 consecutive
        };
        var fs = FeatureCalculator.Compute(draws);
        Assert.Equal(105, fs.SumMean, 5);
        Assert.Equal(21.5, fs.RangeMean, 5);
        Assert.Equal(2, fs.OddMean, 5);
        Assert.Equal(3, fs.ConsecMean, 5);
    }

    [Fact]
    public void Position_counts_track_sorted_slots()
    {
        var draws = new List<DrawEvent> { Ev(1, 5, 10, 15, 20, 25, 30) };
        var fs = FeatureCalculator.Compute(draws);
        Assert.Equal(1, fs.For(5).PositionCounts[0]);
        Assert.Equal(1, fs.For(30).PositionCounts[5]);
        Assert.Equal(0, fs.For(30).PositionCounts[0]);
    }
}
