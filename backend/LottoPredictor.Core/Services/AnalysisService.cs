using LottoPredictor.Core.Analysis;
using LottoPredictor.Core.Data;
using LottoPredictor.Core.Dtos;
using LottoPredictor.Core.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace LottoPredictor.Core.Services;

public sealed class AnalysisSnapshot
{
    public required IReadOnlyList<DrawEvent> Draws { get; init; }
    public required FeatureSet Features { get; init; }
    public FeatureSet? LuckyStarFeatures { get; init; }
    public required int[] LatestLuckyStars { get; init; }
    public required LotteryProfile Lottery { get; init; }
    public required BacktestReport Backtest { get; init; }
    public required ScoringStrategy ActiveStrategy { get; init; }
    public required IReadOnlyList<ScoringStrategy> AllStrategies { get; init; }
    public required int LearningGeneration { get; init; }
    public bool UseAllHistory { get; init; }
    public IReadOnlyDictionary<string, double> HedgeWeights => Backtest.HedgeWeights;
    public int PoolSize => Features.Pool.PoolSize;
}

public interface IAnalysisService
{
    /// <summary>Reads the last analysis without recalculating. Generation requires current data.</summary>
    Task<AnalysisSnapshot> GetSnapshotAsync(CancellationToken ct = default, bool requireCurrent = false);
    AnalysisSnapshot? GetCachedSnapshot();
    Task<AnalysisStatusDto> GetStatusAsync(CancellationToken ct = default);
    Task<AnalysisSnapshot> RecalculateAsync(bool useAllHistory = false, CancellationToken ct = default);
    void Invalidate();
}

public class AnalysisOptions
{
    public int BacktestEvalWindow { get; set; } = 1000;
    public int BacktestWarmup { get; set; } = 150;
}

/// <summary>Owns the derived state (features + walk-forward backtest + active strategy choice).
/// Recalculation is explicit; draw changes mark the last snapshot stale without deleting it.
/// The active strategy is selected by the earlier part of the walk-forward window
/// and its displayed performance comes only from the later chronological holdout.</summary>
public class AnalysisService : IAnalysisService
{
    private sealed class ProfileAnalysisState
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public AnalysisSnapshot? Snapshot { get; set; }
        public object Sync { get; } = new();
        public long Revision { get; set; }
        public long AnalyzedRevision { get; set; }
        public bool UseAllHistory { get; set; }
        public bool IsCalculating { get; set; }
        public int ProgressPercent { get; set; }
        public string ProgressStage { get; set; } = "Not started";
    }

    private readonly IDbContextFactory<LottoDbContext> contextFactory;
    private readonly AnalysisOptions options;
    private readonly ILotterySelection lotterySelection;
    private readonly ConcurrentDictionary<string, ProfileAnalysisState> profileStates = new();

    public AnalysisService(
        IDbContextFactory<LottoDbContext> contextFactory,
        AnalysisOptions options,
        ILotterySelection? lotterySelection = null)
    {
        this.contextFactory = contextFactory;
        this.options = options;
        this.lotterySelection = lotterySelection ?? new DefaultLotterySelection();
    }

    public AnalysisSnapshot? GetCachedSnapshot()
    {
        var state = profileStates.GetOrAdd(lotterySelection.Current.Key, _ => new ProfileAnalysisState());
        lock (state.Sync) return state.Snapshot;
    }

    public async Task<AnalysisStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        var state = profileStates.GetOrAdd(lotterySelection.Current.Key, _ => new ProfileAnalysisState());
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        int drawCount = await db.Draws.CountAsync(ct);
        lock (state.Sync)
            return new AnalysisStatusDto(
                state.Snapshot is not null,
                state.Snapshot is null || state.Revision != state.AnalyzedRevision ||
                    state.Snapshot.Draws.Count != drawCount,
                state.IsCalculating, state.UseAllHistory, drawCount,
                state.Snapshot?.Draws.Count ?? 0, drawCount > options.BacktestWarmup + 2,
                state.ProgressPercent, state.ProgressStage);
    }

    public async Task<AnalysisSnapshot> GetSnapshotAsync(
        CancellationToken ct = default, bool requireCurrent = false)
    {
        if (requireCurrent)
        {
            var status = await GetStatusAsync(ct);
            if (status.NeedsRecalculation || status.IsCalculating)
                throw new AnalysisRequiredException("Recalculate this lottery before generating new predictions.");
        }
        return GetCachedSnapshot() ??
            throw new AnalysisRequiredException("No analysis is available. Press Recalculate for this lottery.");
    }

    public async Task<AnalysisSnapshot> RecalculateAsync(
        bool useAllHistory = false, CancellationToken ct = default)
    {
        var lottery = lotterySelection.Current;
        var state = profileStates.GetOrAdd(lottery.Key, _ => new ProfileAnalysisState());
        if (!await state.Lock.WaitAsync(0, ct))
            throw new AnalysisRequiredException("This lottery is already being recalculated.");
        long revision;
        lock (state.Sync)
        {
            if (state.UseAllHistory != useAllHistory) state.Revision++;
            state.UseAllHistory = useAllHistory;
            state.IsCalculating = true;
            state.ProgressPercent = 0;
            state.ProgressStage = "Loading history and computing features";
            revision = state.Revision;
        }
        void ReportProgress(int percent, string stage)
        {
            lock (state.Sync)
            {
                state.ProgressPercent = percent;
                state.ProgressStage = stage;
            }
        }
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);
            var draws = await db.Draws.AsNoTracking()
                .OrderBy(d => d.Sequence)
                .ToListAsync(ct);
            if (draws.Count == 0)
                throw new AnalysisRequiredException("No draws are available. Import or add results first.");
            if (draws.Count <= options.BacktestWarmup + 2)
                throw new AnalysisRequiredException(
                    $"More than {options.BacktestWarmup + 2} draws are needed to recalculate.");

            var events = draws
                .Select(d => new DrawEvent(d.Sequence, d.DrawNumber, d.Date, d.Numbers(),
                    lottery.BonusSharesMainPool ? d.Bonus : null))
                .ToList();

            var features = FeatureCalculator.Compute(
                events, lottery.MainPoolSize, lottery.MainPoolExpansionDate,
                ruleEras: lottery.MainPoolRules);
            var luckyStarEvents = !lottery.BonusSharesMainPool
                ? draws.Select(d => new DrawEvent(
                    d.Sequence, d.DrawNumber, d.Date, d.BonusNumbers())).ToList()
                : null;
            var luckyStarFeatures = luckyStarEvents is { Count: > 0 }
                ? FeatureCalculator.Compute(luckyStarEvents, lottery.BonusPoolSize,
                    ruleEras: lottery.BonusPoolRules)
                : null;

            int drawCount = events.Count;
            int evalWindow = useAllHistory ? drawCount : options.BacktestEvalWindow;
            int generation = 1;
            int selectionCutoff = Backtester.SelectionCutoff(
                drawCount, evalWindow, options.BacktestWarmup);
            IReadOnlyList<ScoringStrategy> seeds = ScoringStrategy.Candidates;
            ReportProgress(5, "Training strategies");
            // Replay training from clean seeds. Saved winners may have seen today's historical
            // holdout in an earlier rebuild, so they must never seed this validation.
            if (selectionCutoff > options.BacktestWarmup + 2)
            {
                var training = Backtester.Run(
                    Backtester.Prefix(events, selectionCutoff), seeds,
                    evalWindow, options.BacktestWarmup,
                    configuredPoolSize: lottery.MainPoolSize,
                    poolExpansionDate: lottery.MainPoolExpansionDate,
                    onProgress: (completed, total) =>
                        ReportProgress(5 + completed * 25 / total, "Training strategies"),
                    ct: ct);
                seeds = training.Strategies
                    .Where(r => r.Strategy.Name != Backtester.EnsembleName)
                    .OrderByDescending(r => r.ThreePlusRate)
                    .ThenByDescending(r => r.AvgMatches)
                    .ThenBy(r => r.Strategy.Name)
                    .Select(r => r.Strategy).ToList();
            }
            var newCandidates = StrategyOptimizer.GenerateCandidates(seeds, generation);
            ReportProgress(30, "Validating strategies");

            var allStrategies = ScoringStrategy.Candidates
                .Concat(newCandidates)
                .DistinctBy(s => s.Name)
                .ToList();

            var backtest = Backtester.Run(
                events, allStrategies, evalWindow, options.BacktestWarmup,
                configuredPoolSize: lottery.MainPoolSize,
                poolExpansionDate: lottery.MainPoolExpansionDate,
                onProgress: (completed, total) =>
                    ReportProgress(30 + completed * 65 / total, "Validating strategies"),
                ct: ct);

            ReportProgress(95, "Saving analysis");
            await PersistLearningAsync(db, backtest, generation, drawCount, ct);

            var snapshot = new AnalysisSnapshot
            {
                Draws = events,
                Features = features,
                LuckyStarFeatures = luckyStarFeatures,
                LatestLuckyStars = !lottery.BonusSharesMainPool
                    ? draws[^1].BonusNumbers()
                    : [],
                Lottery = lottery,
                Backtest = backtest,
                ActiveStrategy = backtest.Best.Strategy,
                AllStrategies = allStrategies,
                LearningGeneration = generation,
                UseAllHistory = useAllHistory,
            };
            lock (state.Sync)
            {
                state.Snapshot = snapshot;
                state.AnalyzedRevision = revision;
                state.ProgressPercent = 100;
                state.ProgressStage = "Complete";
            }
            return snapshot;
        }
        finally
        {
            lock (state.Sync)
            {
                state.IsCalculating = false;
                if (state.ProgressPercent < 100) state.ProgressStage = "Recalculation did not complete";
            }
            state.Lock.Release();
        }
    }

    /// <summary>Saves selection-period winners for reporting, not as future training seeds, and appends a
    /// performance log row per strategy so improvement over time is auditable.</summary>
    private static async Task PersistLearningAsync(
        LottoDbContext db, BacktestReport backtest, int generation, int drawCount, CancellationToken ct)
    {
        var handWritten = ScoringStrategy.Candidates.Select(s => s.Name).ToHashSet();
        handWritten.Add(Backtester.EnsembleName); // ensemble is adaptive, not a weight vector to keep

        // Survivors are ranked on the same three-plus objective used to select the active
        // strategy. Average matches are a tie-breaker only, so a candidate cannot survive merely
        // by improving a different metric.
        double handWrittenBest = backtest.SelectionStrategies
            .Where(r => handWritten.Contains(r.Strategy.Name))
            .Max(r => r.ThreePlusRate);
        var survivors = backtest.SelectionStrategies
            .Where(r => !handWritten.Contains(r.Strategy.Name))
            .OrderByDescending(r => r.ThreePlusRate)
            .ThenByDescending(r => r.AvgMatches)
            .Take(StrategyOptimizer.MaxLearnedKept)
            .Where(r => r.ThreePlusRate >= handWrittenBest)
            .ToList();

        var existing = await db.LearnedStrategies.ToListAsync(ct);
        db.LearnedStrategies.RemoveRange(existing);
        var existingByName = existing.ToDictionary(s => s.Name);
        foreach (var r in survivors)
        {
            var s = r.Strategy;
            db.LearnedStrategies.Add(new LearnedStrategy
            {
                Name = s.Name,
                WLongTerm = s.WLongTerm,
                WRecent = s.WRecent,
                WGap = s.WGap,
                WMomentum = s.WMomentum,
                PairWeight = s.PairWeight,
                PenaltyWeight = s.PenaltyWeight,
                WBias = s.WBias,
                WBonus = s.WBonus,
                Generation = generation,
                AvgMatches = r.AvgMatches,
                RecencyWeightedAvg = r.RecencyWeightedAvg,
                EvaluatedDraws = r.Evaluated,
                CreatedUtc = existingByName.TryGetValue(s.Name, out var prev)
                    ? prev.CreatedUtc
                    : DateTime.UtcNow,
            });
        }

        // One log row per strategy per dataset size; skip if this draw count was already logged.
        bool alreadyLogged = await db.StrategyPerformanceLogs
            .AnyAsync(l => l.DrawCount == drawCount, ct);
        if (!alreadyLogged)
        {
            var now = DateTime.UtcNow;
            foreach (var r in backtest.Strategies)
            {
                db.StrategyPerformanceLogs.Add(new StrategyPerformanceLog
                {
                    LoggedUtc = now,
                    DrawCount = drawCount,
                    StrategyName = r.Strategy.Name,
                    AvgMatches = r.AvgMatches,
                    RecencyWeightedAvg = r.RecencyWeightedAvg,
                    RandomExpected = backtest.RandomExpectedMatches,
                    WasActive = r.Strategy.Name == backtest.Best.Strategy.Name,
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public void Invalidate()
    {
        var lottery = lotterySelection.Current;
        var state = profileStates.GetOrAdd(lottery.Key, _ => new ProfileAnalysisState());
        lock (state.Sync) state.Revision++;
    }
}

public sealed class AnalysisRequiredException(string message) : InvalidOperationException(message);
