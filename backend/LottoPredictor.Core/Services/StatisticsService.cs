using LottoPredictor.Core.Analysis;
using LottoPredictor.Core.Dtos;

namespace LottoPredictor.Core.Services;

public interface IStatisticsService
{
    Task<StatisticsDto> GetStatisticsAsync(CancellationToken ct = default);
    Task<BacktestingDto> GetBacktestingAsync(CancellationToken ct = default);
}

public class StatisticsService(
    IAnalysisService analysis,
    IDrawService draws,
    ILotterySelection? lotterySelection = null) : IStatisticsService
{
    public async Task<StatisticsDto> GetStatisticsAsync(CancellationToken ct = default)
    {
        if (await draws.CountAsync(ct) == 0)
        {
            var lottery = (lotterySelection ?? new DefaultLotterySelection()).Current;
            return new StatisticsDto(
                0, lottery.MainPoolSize, null, null, [], [],
                new SetStatsDto(0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        }

        var snapshot = await analysis.GetSnapshotAsync(ct);
        var fs = snapshot.Features;
        var latest = await draws.GetLatestAsync(ct);
        var latestRounds = latest is null
            ? []
            : await draws.GetDrawsOnDateAsync(DateOnly.Parse(latest.Date), ct);

        int? poolChangeDrawNumber = fs.Pool.Era2StartIndex < snapshot.Draws.Count && fs.Pool.Era2StartIndex > 0
            ? snapshot.Draws[fs.Pool.Era2StartIndex].DrawNumber
            : null;

        var numbers = fs.Numbers.Select(f => new NumberStatsDto(
            f.Number, f.TotalCount, f.EligibleDraws, Math.Round(f.FreqRate, 5),
            f.Count10, f.Count25, f.Count50, f.Count100,
            f.DrawsSinceLast, Math.Round(f.AvgGap, 2), Math.Round(f.GapRatio, 3),
            Math.Round(f.RecentVsLongTerm, 3), f.PositionCounts)).ToList();

        return new StatisticsDto(
            snapshot.Draws.Count,
            fs.Pool.PoolSize,
            poolChangeDrawNumber,
            latest,
            latestRounds,
            numbers,
            new SetStatsDto(
                Math.Round(fs.SumMean, 2), Math.Round(fs.SumStd, 2),
                Math.Round(fs.RangeMean, 2), Math.Round(fs.RangeStd, 2),
                Math.Round(fs.OddMean, 2), Math.Round(fs.OddStd, 2),
                Math.Round(fs.ConsecMean, 2), Math.Round(fs.ConsecStd, 2),
                Math.Round(fs.LowHalfMean, 2), Math.Round(fs.LowHalfStd, 2)));
    }

    public async Task<BacktestingDto> GetBacktestingAsync(CancellationToken ct = default)
    {
        if (await draws.CountAsync(ct) == 0)
            return new BacktestingDto(
                0, 0, [], "Waiting for draw history", 0, 0, 0, 0, 0, 0,
                0, 0,
                "Add or import draw history to start backtesting.");

        var snapshot = await analysis.GetSnapshotAsync(ct);
        var report = snapshot.Backtest;

        var strategies = report.Strategies
            .OrderByDescending(s => s.RecencyWeightedAvg)
            .Select(s => ToDto(s, s.Strategy.Name == report.Best.Strategy.Name))
            .ToList();

        var randomSim = report.RandomSimulated;
        return new BacktestingDto(
            report.Best.Evaluated,
            report.WarmupDraws,
            strategies,
            snapshot.ActiveStrategy.Name,
            Math.Round(report.RandomExpectedMatches, 4),
            Math.Round(randomSim.AvgMatches, 4),
            Pct(randomSim.MatchCounts, 0, randomSim.Evaluated),
            Pct(randomSim.MatchCounts, 1, randomSim.Evaluated),
            Pct(randomSim.MatchCounts, 2, randomSim.Evaluated),
            PctAtLeast(randomSim.MatchCounts, 3, randomSim.Evaluated),
            Math.Round(report.RandomFourPlusProbability, 8),
            Math.Round(report.RandomExpectedFourPlusHits, 4),
            report.Verdict);
    }

    private static StrategyBacktestDto ToDto(StrategyBacktest s, bool isBest) => new(
        s.Strategy.Name,
        s.Strategy.Name == Backtester.EnsembleName
            ? "online multiplicative-weights blend of all strategies"
            : s.Strategy.Describe(),
        s.Evaluated, Math.Round(s.AvgMatches, 4),
        Math.Round(s.RecencyWeightedAvg, 4),
        Pct(s.MatchCounts, 0, s.Evaluated),
        Pct(s.MatchCounts, 1, s.Evaluated),
        Pct(s.MatchCounts, 2, s.Evaluated),
        PctAtLeast(s.MatchCounts, 3, s.Evaluated),
        s.FourPlusHits,
        Math.Round(s.FourPlusRate, 8),
        Math.Round(s.FourPlusCiLow, 8),
        Math.Round(s.FourPlusCiHigh, 8),
        isBest,
        s.Strategy.Name.StartsWith("learned-") || s.Strategy.Name == Backtester.EnsembleName);

    private static double Pct(int[] counts, int k, int total) =>
        total > 0 ? Math.Round(100.0 * counts[k] / total, 2) : 0;

    private static double PctAtLeast(int[] counts, int k, int total) =>
        total > 0 ? Math.Round(100.0 * counts.Skip(k).Sum() / total, 2) : 0;
}
