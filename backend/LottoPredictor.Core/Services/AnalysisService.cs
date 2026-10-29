using LottoPredictor.Core.Analysis;
using LottoPredictor.Core.Data;
using LottoPredictor.Core.Dtos;
using LottoPredictor.Core.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Globalization;

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
    /// <summary>Strategies blended by the live hedge ensemble: built-ins, live learned challengers
    /// and the online model. These differ from <see cref="AllStrategies"/>, which are the backtest set.</summary>
    private readonly IReadOnlyList<ScoringStrategy>? ensembleMembers;
    public IReadOnlyList<ScoringStrategy> EnsembleMembers
    {
        get => ensembleMembers is { Count: > 0 } ? ensembleMembers : AllStrategies;
        init => ensembleMembers = value;
    }
    private readonly IReadOnlyDictionary<string, double>? hedgeWeights;
    /// <summary>Live (persisted) hedge weights. Falls back to the backtest's final weights.</summary>
    public IReadOnlyDictionary<string, double> HedgeWeights
    {
        get => hedgeWeights ?? Backtest.HedgeWeights;
        init => hedgeWeights = value;
    }
    /// <summary>Dedicated strategy learned specifically on the secondary/bonus pool (Lucky Stars / Life Ball)
    /// when the lottery has a separate bonus pool.</summary>
    public ScoringStrategy? ActiveBonusStrategy { get; init; }
    /// <summary>Dedicated hedge weights learned on the secondary/bonus pool.</summary>
    public IReadOnlyDictionary<string, double>? BonusHedgeWeights { get; init; }
    public SelectionDecision? Selection { get; init; }
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

    // Prospective learning (shadow ledger). See README "How learning works".
    public double HedgeEta { get; set; } = Backtester.HedgeEta;
    /// <summary>Fixed-share mixing toward uniform after each live hedge update, so weights can recover.</summary>
    public double HedgeFixedShare { get; set; } = 0.01;
    public double ThreePlusRewardWeight { get; set; } = LearningSignals.DefaultThreePlusWeight;
    public double OnlineLearningRate { get; set; } = 0.05;
    public double OnlineL2 { get; set; } = 0.01;
    /// <summary>Scored prospective draws needed before real results can override the backtest choice.</summary>
    public int MinProspectiveDraws { get; set; } = 100;
    /// <summary>Family-wise confidence required to promote a challenger (Bonferroni-corrected).</summary>
    public double PromotionConfidence { get; set; } = 0.95;
    /// <summary>Draws a promoted strategy keeps the role before it can be replaced.</summary>
    public int MinHoldDraws { get; set; } = 20;
    /// <summary>New challengers are admitted at most once per this many new draws.</summary>
    public int GenerationIntervalDraws { get; set; } = 8;
    public int ChallengersAdmittedPerGeneration { get; set; } = 4;
    public int MaxLiveChallengers { get; set; } = 16;
    /// <summary>Challengers are protected from retirement until they have this many scored draws.</summary>
    public int MinDrawsBeforeRetire { get; set; } = 20;
    /// <summary>Pseudo-draws of zero reward used to shrink prospective means when ranking.</summary>
    public double ShrinkagePseudoDraws { get; set; } = 20;
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
                    lottery.BonusSharesMainPool ? d.Bonus : null, d.Machine, d.BallSet))
                .ToList();

            var features = FeatureCalculator.Compute(
                events, lottery.MainPoolSize, lottery.MainPoolExpansionDate,
                ruleEras: lottery.MainPoolRules);
            var luckyStarEvents = !lottery.BonusSharesMainPool
                ? draws.Select(d => new DrawEvent(
                    d.Sequence, d.DrawNumber, d.Date, d.BonusNumbers(), null, d.Machine, d.BallSet)).ToList()
                : null;
            var luckyStarFeatures = luckyStarEvents is { Count: > 0 }
                ? FeatureCalculator.Compute(luckyStarEvents, lottery.BonusPoolSize,
                    ruleEras: lottery.BonusPoolRules)
                : null;

            int drawCount = events.Count;
            int latestSequence = events[^1].Sequence;
            int evalWindow = useAllHistory ? drawCount : options.BacktestEvalWindow;
            var ledger = new ShadowLedger(db, lottery, options);
            var (generation, generationIsNew) = await ResolveGenerationAsync(ledger, latestSequence, ct);
            int selectionCutoff = Backtester.SelectionCutoff(
                drawCount, evalWindow, options.BacktestWarmup);
            IReadOnlyList<ScoringStrategy> seeds = ScoringStrategy.Candidates;
            ReportProgress(5, "Training strategies");
            // Multi-generation evolution strictly inside the training prefix (0..selectionCutoff).
            // Saved winners in DB are never fed back in, preserving leak-free holdout guarantees.
            IReadOnlyList<ScoringStrategy> newCandidates;
            if (selectionCutoff > options.BacktestWarmup + 2)
            {
                newCandidates = StrategyOptimizer.EvolveOnPrefix(
                    Backtester.Prefix(events, selectionCutoff),
                    seeds,
                    generation,
                    evalWindow,
                    options.BacktestWarmup,
                    configuredPoolSize: lottery.MainPoolSize,
                    poolExpansionDate: lottery.MainPoolExpansionDate,
                    ruleEras: lottery.MainPoolRules,
                    onProgress: (completed, total) =>
                        ReportProgress(5 + completed * 25 / total, "Training strategies"),
                    ct: ct);
            }
            else
            {
                newCandidates = StrategyOptimizer.GenerateCandidates(seeds, generation);
            }
            ReportProgress(30, "Validating strategies");

            var allStrategies = ScoringStrategy.Candidates
                .Concat(newCandidates)
                .DistinctBy(s => s.Name)
                .ToList();

            // The online model learns walk-forward: each step sees only the draws before it, and
            // persisted progress means each draw is learned from exactly once across rebuilds.
            var (onlineModel, onlineState) = await ledger.LoadOnlineModelAsync(ct);
            int onlineThrough = onlineState.UpdatedThroughSequence;
            var backtest = Backtester.Run(
                events, allStrategies, evalWindow, options.BacktestWarmup,
                configuredPoolSize: lottery.MainPoolSize,
                poolExpansionDate: lottery.MainPoolExpansionDate,
                onProgress: (completed, total) =>
                    ReportProgress(30 + completed * 60 / total, "Validating strategies"),
                ct: ct,
                onStep: (_, stepFeatures, target) =>
                {
                    if (target.Sequence <= onlineThrough) return;
                    onlineModel.Update(stepFeatures, target.Numbers);
                    onlineThrough = target.Sequence;
                },
                ruleEras: lottery.MainPoolRules,
                includeOnlineInHedge: true);

            ScoringStrategy? activeBonusStrategy = null;
            IReadOnlyDictionary<string, double>? bonusHedgeWeights = null;
            if (luckyStarEvents is { Count: > 0 } &&
                luckyStarEvents.Count > options.BacktestWarmup + 2 &&
                luckyStarEvents[0].Numbers.Length > 0)
            {
                ReportProgress(92, "Validating bonus pool strategies");
                int bonusEvalWindow = Math.Min(evalWindow, 250);
                var bonusBacktest = Backtester.Run(
                    luckyStarEvents,
                    ScoringStrategy.Candidates,
                    bonusEvalWindow,
                    options.BacktestWarmup,
                    configuredPoolSize: lottery.BonusPoolSize,
                    ct: ct,
                    ruleEras: lottery.BonusPoolRules);
                activeBonusStrategy = bonusBacktest.Best.Strategy;
                bonusHedgeWeights = bonusBacktest.HedgeWeights;
            }

            ReportProgress(95, "Updating prospective learning");
            ShadowLedger.StoreOnlineModel(onlineModel, onlineState, onlineThrough);
            await db.SaveChangesAsync(ct);
            await PersistLearningAsync(db, ledger, backtest, newCandidates, generation, generationIsNew,
                latestSequence, drawCount, ct);

            var members = await ledger.LoadMembersAsync(ct);
            await ledger.InitialiseHedgeAsync(backtest.HedgeWeights, members, latestSequence, ct);
            await db.SaveChangesAsync(ct);
            await ledger.FreezeAsync(latestSequence, features, ct);
            await db.SaveChangesAsync(ct);
            var hedgeWeights = await ledger.LoadHedgeWeightsAsync(members, ct);

            var decision = await ChooseActiveAsync(db, ledger, backtest, latestSequence, ct);
            var memberStrategies = members.Select(m => m.Strategy).ToList();
            var activeStrategy = decision.Source == ProspectiveEvaluator.SourceBacktest
                ? backtest.Best.Strategy
                : decision.ActiveName == Backtester.EnsembleName
                    ? new ScoringStrategy(Backtester.EnsembleName, 0, 0, 0, 0, 0, 0)
                    : memberStrategies.Concat(allStrategies)
                        .FirstOrDefault(s => s.Name == decision.ActiveName) ?? backtest.Best.Strategy;

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
                ActiveStrategy = activeStrategy,
                ActiveBonusStrategy = activeBonusStrategy ?? activeStrategy,
                BonusHedgeWeights = bonusHedgeWeights ?? hedgeWeights,
                AllStrategies = allStrategies,
                LearningGeneration = generation,
                UseAllHistory = useAllHistory,
                EnsembleMembers = memberStrategies,
                HedgeWeights = hedgeWeights,
                Selection = decision,
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

    /// <summary>The generation advances once per new latest draw, never on a rebuild of the same
    /// data. This keeps rebuilds deterministic while letting evolution move on as results arrive.</summary>
    private static async Task<(int Generation, bool IsNew)> ResolveGenerationAsync(
        ShadowLedger ledger, int latestSequence, CancellationToken ct)
    {
        bool hasGeneration = int.TryParse(
            await ledger.GetStateAsync(ShadowLedger.StateGeneration, ct), out int stored);
        bool hasKey = int.TryParse(
            await ledger.GetStateAsync(ShadowLedger.StateGenerationKey, ct), out int key);
        if (!hasGeneration || !hasKey) return (1, true);
        return latestSequence > key ? (stored + 1, true) : (stored, false);
    }

    /// <summary>Evolves the live challenger population and appends one performance log row per
    /// strategy, so improvement over time is auditable. Fresh GA candidates are bred only from
    /// training-prefix winners. Admitted challengers are never backtested again; from their birth
    /// they are judged only by the forward-only shadow ledger. When room is needed, the weakest
    /// challengers by shrunk prospective reward are retired.</summary>
    private async Task PersistLearningAsync(
        LottoDbContext db, ShadowLedger ledger, BacktestReport backtest,
        IReadOnlyList<ScoringStrategy> freshCandidates, int generation, bool generationIsNew,
        int latestSequence, int drawCount, CancellationToken ct)
    {
        if (generationIsNew)
        {
            await ledger.SetStateAsync(ShadowLedger.StateGeneration,
                generation.ToString(CultureInfo.InvariantCulture), ct);
            await ledger.SetStateAsync(ShadowLedger.StateGenerationKey,
                latestSequence.ToString(CultureInfo.InvariantCulture), ct);
            await EvolvePopulationAsync(db, ledger, backtest, freshCandidates, generation, latestSequence, ct);
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

    private async Task EvolvePopulationAsync(
        LottoDbContext db, ShadowLedger ledger, BacktestReport backtest,
        IReadOnlyList<ScoringStrategy> freshCandidates, int generation, int latestSequence,
        CancellationToken ct)
    {
        var learned = await db.LearnedStrategies.ToListAsync(ct);
        var live = learned.Where(s => s.Status != LearnedStatus.Retired).ToList();
        int lastAdmission = int.TryParse(
            await ledger.GetStateAsync(ShadowLedger.StateAdmissionSequence, ct), out int seq)
            ? seq
            : int.MinValue / 2;
        if (live.Count > 0 && latestSequence - lastAdmission < options.GenerationIntervalDraws) return;

        int admitTarget = Math.Min(options.ChallengersAdmittedPerGeneration, options.MaxLiveChallengers);
        int room = options.MaxLiveChallengers - live.Count;
        if (room < admitTarget)
        {
            var records = ProspectiveEvaluator.Summarise(await ledger.LoadOutcomesAsync(ct))
                .ToDictionary(r => r.StrategyName);
            string? champion = await ledger.GetStateAsync(ShadowLedger.StateActiveName, ct);
            var retiring = live
                .Where(s => s.Name != champion &&
                    records.TryGetValue(s.Name, out var r) && r.Draws >= options.MinDrawsBeforeRetire)
                .OrderBy(s => records[s.Name].ShrunkReward(options.ShrinkagePseudoDraws))
                .ThenBy(s => s.Id)
                .Take(admitTarget - room)
                .ToList();
            var retiredNames = retiring.Select(s => s.Name).ToHashSet();
            var now = DateTime.UtcNow;
            foreach (var s in retiring)
            {
                s.Status = LearnedStatus.Retired;
                s.RetiredUtc = now;
            }
            db.HedgeWeightStates.RemoveRange(
                await db.HedgeWeightStates.Where(h => retiredNames.Contains(h.StrategyName)).ToListAsync(ct));
            room += retiring.Count;
        }
        if (room <= 0) return;

        var freshNames = freshCandidates.Select(s => s.Name).ToHashSet();
        var knownNames = learned.Select(s => s.Name).ToHashSet();
        // Same ordering as the backtest's own selection, so a fresh backtest winner is admitted first.
        var admitted = backtest.SelectionStrategies
            .Where(r => freshNames.Contains(r.Strategy.Name) && !knownNames.Contains(r.Strategy.Name))
            .OrderByDescending(r => r.ThreePlusRate)
            .ThenByDescending(r => r.AvgMatches)
            .ThenByDescending(r => r.RecencyWeightedAvg)
            .ThenBy(r => r.Strategy.Name)
            .Take(Math.Min(room, admitTarget))
            .ToList();
        foreach (var r in admitted)
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
                CreatedUtc = DateTime.UtcNow,
                BornAfterSequence = latestSequence,
                Status = LearnedStatus.Challenger,
            });
        }
        if (admitted.Count > 0)
            await ledger.SetStateAsync(ShadowLedger.StateAdmissionSequence,
                latestSequence.ToString(CultureInfo.InvariantCulture), ct);
    }

    /// <summary>Backtest first, then real (prospective) results take over through the promotion
    /// gate. The decision is persisted so that holds and incumbency survive restarts.</summary>
    private async Task<SelectionDecision> ChooseActiveAsync(
        LottoDbContext db, ShadowLedger ledger, BacktestReport backtest, int latestSequence,
        CancellationToken ct)
    {
        var records = ProspectiveEvaluator.Summarise(await ledger.LoadOutcomesAsync(ct));
        string? currentName = await ledger.GetStateAsync(ShadowLedger.StateActiveName, ct);
        string? currentSource = await ledger.GetStateAsync(ShadowLedger.StateActiveSource, ct);
        int promotedAt = int.TryParse(
            await ledger.GetStateAsync(ShadowLedger.StateActivePromotedAt, ct), out int p) ? p : 0;
        var decision = ProspectiveEvaluator.Choose(
            backtest.Best.Strategy.Name, records, currentName, currentSource, promotedAt, latestSequence,
            new SelectionSettings(options.MinProspectiveDraws, options.PromotionConfidence, options.MinHoldDraws));

        var inv = CultureInfo.InvariantCulture;
        await ledger.SetStateAsync(ShadowLedger.StateActiveName, decision.ActiveName, ct);
        await ledger.SetStateAsync(ShadowLedger.StateActiveSource, decision.Source, ct);
        await ledger.SetStateAsync(ShadowLedger.StateActivePromotedAt, decision.PromotedAtSequence.ToString(inv), ct);
        await ledger.SetStateAsync(ShadowLedger.StateActiveReason, decision.Reason, ct);
        await ledger.SetStateAsync(ShadowLedger.StateActiveConfidence,
            decision.Confidence?.ToString("R", inv) ?? "", ct);

        foreach (var s in await db.LearnedStrategies.Where(s => s.Status != LearnedStatus.Retired).ToListAsync(ct))
            s.Status = s.Name == decision.ActiveName && decision.Source == ProspectiveEvaluator.SourceProspective
                ? LearnedStatus.Champion
                : LearnedStatus.Challenger;
        await db.SaveChangesAsync(ct);
        return decision;
    }

    public void Invalidate()
    {
        var lottery = lotterySelection.Current;
        var state = profileStates.GetOrAdd(lottery.Key, _ => new ProfileAnalysisState());
        lock (state.Sync) state.Revision++;
    }
}

public sealed class AnalysisRequiredException(string message) : InvalidOperationException(message);
