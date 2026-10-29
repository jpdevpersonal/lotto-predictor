using LottoPredictor.Core.Data;
using LottoPredictor.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace LottoPredictor.Core.Services;

public interface ILearningService
{
    Task<LearningDto> GetLearningAsync(int historyLimit = 500, CancellationToken ct = default);
}

public class LearningService(
    IDbContextFactory<LottoDbContext> contextFactory,
    IAnalysisService analysis,
    AnalysisOptions? options = null,
    ILotterySelection? lotterySelection = null)
    : ILearningService
{
    private readonly AnalysisOptions options = options ?? new AnalysisOptions();
    private readonly ILotterySelection lotterySelection = lotterySelection ?? new DefaultLotterySelection();

    public async Task<LearningDto> GetLearningAsync(int historyLimit = 500, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        if (!await db.Draws.AnyAsync(ct))
            return new LearningDto(
                0,
                0,
                null,
                "Waiting for draw history",
                "Add or import draw history to start learning.",
                false,
                new UniformityDto(0, 0, 1, 0, "Not enough draws for a uniformity test."),
                [],
                [],
                []);

        var snapshot = await analysis.GetSnapshotAsync(ct);

        var learned = await db.LearnedStrategies.AsNoTracking()
            .OrderByDescending(s => s.RecencyWeightedAvg)
            .ToListAsync(ct);
        var history = await db.StrategyPerformanceLogs.AsNoTracking()
            .OrderByDescending(l => l.DrawCount).ThenByDescending(l => l.RecencyWeightedAvg)
            .Take(Math.Clamp(historyLimit, 1, 2000))
            .ToListAsync(ct);

        var active = snapshot.ActiveStrategy;
        var fs = snapshot.Features;

        string assessment = fs.ChiSquareDf == 0
            ? "Not enough current-era draws for a uniformity test."
            : fs.ChiSquarePValue < 0.01
                ? $"Ball frequencies deviate significantly from uniform (p={fs.ChiSquarePValue:0.0000}) — " +
                  "a possible physical bias the bias-detector strategies can exploit."
                : fs.ChiSquarePValue < 0.05
                    ? $"Weak evidence of non-uniformity (p={fs.ChiSquarePValue:0.0000}); likely noise, monitored each draw."
                    : $"Ball frequencies are consistent with a fair machine (p={fs.ChiSquarePValue:0.0000}); " +
                      "no detectable frequency bias; this does not prove that no bias exists.";

        var hedgeWeights = snapshot.HedgeWeights
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new HedgeWeightDto(kv.Key, Math.Round(kv.Value, 4)))
            .ToList();

        return new LearningDto(
            snapshot.LearningGeneration,
            snapshot.Draws.Count,
            history.Count == 0 ? null : history.Max(point => point.LoggedUtc),
            active.Name,
            active.Name == Analysis.Backtester.EnsembleName
                ? "persistent multiplicative-weights blend of the live strategies"
                : active.Describe(),
            active.Name.StartsWith("learned-") || active.Name == Analysis.Backtester.EnsembleName ||
                active.Name == Analysis.OnlineBallModel.StrategyName,
            new UniformityDto(
                Math.Round(fs.ChiSquare, 2),
                fs.ChiSquareDf,
                Math.Round(fs.ChiSquarePValue, 6),
                fs.ChiSquareWindowDraws,
                assessment),
            hedgeWeights,
            learned.Select(s => new LearnedStrategyDto(
                s.Name,
                Analysis.StrategyOptimizer.ToStrategy(s).Describe(),
                s.Generation,
                Math.Round(s.AvgMatches, 4),
                Math.Round(s.RecencyWeightedAvg, 4),
                s.EvaluatedDraws,
                s.CreatedUtc,
                s.Status,
                s.BornAfterSequence,
                s.RetiredUtc)).ToList(),
            history.Select(l => new PerformancePointDto(
                l.DrawCount,
                l.LoggedUtc,
                l.StrategyName,
                Math.Round(l.AvgMatches, 4),
                Math.Round(l.RecencyWeightedAvg, 4),
                Math.Round(l.RandomExpected, 4),
                l.WasActive)).ToList(),
            await BuildProspectiveAsync(db, snapshot, learned, ct));
    }

    private const int SeriesStrategies = 5;

    private async Task<ProspectiveLearningDto> BuildProspectiveAsync(
        LottoDbContext db, AnalysisSnapshot snapshot, IReadOnlyList<Models.LearnedStrategy> learned,
        CancellationToken ct)
    {
        var ledger = new ShadowLedger(db, lotterySelection.Current, options);
        var outcomes = await ledger.LoadOutcomesAsync(ct);
        var records = Analysis.ProspectiveEvaluator.Summarise(outcomes);
        var status = learned.ToDictionary(s => s.Name, s => s.Status);
        var weights = snapshot.HedgeWeights;
        string activeName = snapshot.ActiveStrategy.Name;

        var leaderboard = records
            .OrderByDescending(r => r.ShrunkReward(options.ShrinkagePseudoDraws))
            .ThenBy(r => r.StrategyName)
            .Select(r => new ProspectiveRecordDto(
                r.StrategyName,
                r.Kind,
                status.GetValueOrDefault(r.StrategyName),
                r.Draws,
                r.ThreePlusHits,
                Math.Round(r.ExpectedThreePlusHits, 3),
                Math.Round(r.MeanMatches, 4),
                Math.Round(r.MeanExpectedMatches, 4),
                Math.Round(r.MeanScoreZ, 4),
                Math.Round(r.ScoreZTotal, 3),
                Math.Round(r.MeanReward, 4),
                Math.Round(r.ShrunkReward(options.ShrinkagePseudoDraws), 4),
                weights.TryGetValue(r.StrategyName, out double w) ? Math.Round(w, 4) : null))
            .ToList();

        // Chart the active strategy, the ensemble and the best few, as cumulative excess over random.
        var charted = leaderboard.Select(r => r.StrategyName).Take(SeriesStrategies)
            .Prepend(Analysis.Backtester.EnsembleName)
            .Prepend(activeName)
            .Distinct()
            .Take(SeriesStrategies + 1)
            .ToHashSet();
        var series = outcomes
            .Where(o => charted.Contains(o.StrategyName))
            .GroupBy(o => o.StrategyName)
            .Select(g =>
            {
                double excess = 0, scoreZ = 0;
                var points = g.OrderBy(o => o.CutoffSequence).Select(o =>
                {
                    excess += o.Matches - o.RandomExpectedMatches;
                    scoreZ += o.ScoreZ;
                    return new ProspectivePointDto(o.CutoffSequence, Math.Round(excess, 3), Math.Round(scoreZ, 3));
                }).ToList();
                return new ProspectiveSeriesDto(g.Key, points);
            })
            .OrderBy(s => s.StrategyName)
            .ToList();

        var online = await db.OnlineModelStates.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Name == Analysis.OnlineBallModel.StrategyName, ct);
        OnlineModelDto? onlineDto = null;
        if (online is not null && !string.IsNullOrWhiteSpace(online.WeightsCsv))
        {
            var values = online.WeightsCsv.Split(',')
                .Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            onlineDto = new OnlineModelDto(
                online.Updates,
                online.UpdatedThroughSequence,
                Analysis.PredictionEngine.FeatureNames
                    .Zip(values, (name, value) => new CoefficientDto(name, Math.Round(value, 4)))
                    .ToList());
        }

        var selection = snapshot.Selection;
        var selectionDto = selection is null
            ? new SelectionDto(activeName, Analysis.ProspectiveEvaluator.SourceBacktest,
                "Chosen by the walk-forward backtest.", null, 0, options.MinProspectiveDraws, 0)
            : new SelectionDto(selection.ActiveName, selection.Source, selection.Reason,
                selection.Confidence is { } c ? Math.Round(c, 4) : null,
                selection.ProspectiveDraws, options.MinProspectiveDraws, selection.PromotedAtSequence);

        int pending = await db.ShadowPredictions.CountAsync(s => s.Reward == null && !s.Invalid, ct);
        int? lastScored = outcomes.Count == 0 ? null : outcomes.Max(o => o.CutoffSequence) + 1;
        string? lastError = await ledger.GetStateAsync(ShadowLedger.StateLastError, ct);

        return new ProspectiveLearningDto(
            selectionDto,
            outcomes.Select(o => o.CutoffSequence).Distinct().Count(),
            pending,
            lastScored,
            leaderboard,
            series,
            onlineDto,
            string.IsNullOrWhiteSpace(lastError) ? null : lastError);
    }
}
