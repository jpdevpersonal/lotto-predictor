using LottoPredictor.Core.Analysis;
using LottoPredictor.Core.Services;

namespace LottoPredictor.Tests;

/// <summary>Integration tests against the real supplied numbers.csv (found by walking up from
/// the test directory). These validate the full pipeline on genuine data.</summary>
public class RealCsvIntegrationTests
{
    private static string? FindCsv()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "numbers.csv");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static List<DrawEvent> LoadEvents()
    {
        var path = FindCsv();
        Assert.True(path is not null, "numbers.csv not found in any parent directory");
        using var reader = new StreamReader(path!);
        return new CsvImporter().Parse(reader)
            .Select(d => new DrawEvent(d.Sequence, d.DrawNumber, d.Date, d.Numbers(), d.Bonus))
            .ToList();
    }

    [Fact]
    public void Imports_all_rows_and_detects_duplicated_draw_numbers()
    {
        var events = LoadEvents();
        Assert.Equal(3226, events.Count);

        // Draws 3179-3202 each contain two independent draw events; nothing was discarded.
        var duplicated = events.GroupBy(e => e.DrawNumber).Where(g => g.Count() > 1).ToList();
        Assert.Equal(24, duplicated.Count);
        Assert.All(duplicated, g => Assert.InRange(g.Key, 3179, 3202));

        // Chronological ordering with contiguous sequences.
        Assert.Equal(Enumerable.Range(1, events.Count), events.Select(e => e.Sequence));
        for (int i = 1; i < events.Count; i++)
            Assert.True(events[i].DrawNumber >= events[i - 1].DrawNumber);
    }

    [Fact]
    public void Detects_pool_change_from_49_to_59()
    {
        var events = LoadEvents();
        var pool = PoolInfo.Detect(events);
        Assert.Equal(59, pool.PoolSize);
        // The 1-59 era starts at draw number 2066 (October 2015).
        Assert.Equal(2066, events[pool.Era2StartIndex].DrawNumber);
        Assert.Equal(2015, events[pool.Era2StartIndex].Date.Year);
    }

    [Fact]
    public void Features_are_consistent_on_real_data()
    {
        var events = LoadEvents();
        var fs = FeatureCalculator.Compute(events);

        // Every draw contributes exactly 6 appearances.
        Assert.Equal(events.Count * 6, fs.Numbers.Sum(f => f.TotalCount));

        // Numbers 50-59 are only eligible in the second era.
        int era2Draws = events.Count - fs.Pool.Era2StartIndex;
        Assert.All(fs.Numbers.Where(f => f.Number > 49),
            f => Assert.Equal(era2Draws, f.EligibleDraws));

        // Sum distribution should be broadly plausible for 6-of-59 (mean 180).
        Assert.InRange(fs.SumMean, 160, 200);
    }

    [Fact]
    public void Walk_forward_backtest_runs_on_real_data_and_reports_honestly()
    {
        var events = LoadEvents();
        var report = Backtester.Run(events, ScoringStrategy.Candidates, evalWindow: 200, warmup: 150);

        Assert.Equal(200, report.Best.Evaluated);
        Assert.All(report.Strategies, s => Assert.Equal(200, s.MatchCounts.Sum()));

        // Sanity bounds: nobody legitimately averages anywhere near 6/6 on a real lottery.
        Assert.InRange(report.Best.AvgMatches, 0, 2.0);
        Assert.InRange(report.RandomExpectedMatches, 0.5, 0.7);
        Assert.False(string.IsNullOrWhiteSpace(report.Verdict));
    }

    [Fact]
    public void Generates_a_valid_prediction_from_real_data()
    {
        var events = LoadEvents();
        var fs = FeatureCalculator.Compute(events);
        foreach (var strategy in ScoringStrategy.Candidates)
        {
            var result = PredictionEngine.Generate(fs, strategy);
            Assert.Equal(6, result.Numbers.Distinct().Count());
            Assert.All(result.Numbers, n => Assert.InRange(n, 1, 59));
        }
    }
}
