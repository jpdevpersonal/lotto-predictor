using System.Globalization;
using LottoPredictor.Core.Analysis;
using LottoPredictor.Core.Data;
using LottoPredictor.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LottoPredictor.Core.Services;

/// <summary>A strategy taking part in live prospective learning.</summary>
public sealed record LiveMember(ScoringStrategy Strategy, string Kind);

/// <summary>Prospective (forward-only) learning ledger. For each new draw, in sequence order, it:
/// <list type="number">
/// <item>scores the lines frozen at the previous draw,</item>
/// <item>updates the persistent hedge weights with the composite reward,</item>
/// <item>takes one online-model gradient step using features from earlier draws only, and</item>
/// <item>freezes every live strategy's line for the next draw.</item>
/// </list>
/// Nothing is ever frozen after the draw it predicts is known, so the ledger is leak-free by
/// construction. It is therefore safe to use as fitness for evolution and promotion.</summary>
public sealed class ShadowLedger(LottoDbContext db, LotteryProfile lottery, AnalysisOptions options)
{
    public const string KindBuiltin = "builtin";
    public const string KindLearned = "learned";
    public const string KindOnline = "online";
    public const string KindEnsemble = "ensemble";

    public const string StateGeneration = "learning.generation";
    public const string StateGenerationKey = "learning.generationKey";
    public const string StateAdmissionSequence = "learning.admissionSequence";
    public const string StateActiveName = "active.name";
    public const string StateActiveSource = "active.source";
    public const string StateActivePromotedAt = "active.promotedAt";
    public const string StateActiveReason = "active.reason";
    public const string StateActiveConfidence = "active.confidence";
    public const string StateLastError = "ledger.lastError";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static List<DrawEvent> MainEvents(IEnumerable<Draw> draws, LotteryProfile lottery) =>
        draws.OrderBy(d => d.Sequence)
            .Select(d => new DrawEvent(d.Sequence, d.DrawNumber, d.Date, d.Numbers(),
                lottery.BonusSharesMainPool ? d.Bonus : null, d.Machine, d.BallSet))
            .ToList();

    public FeatureSet Features(IReadOnlyList<DrawEvent> prefix) =>
        FeatureCalculator.Compute(prefix, lottery.MainPoolSize, lottery.MainPoolExpansionDate,
            ruleEras: lottery.MainPoolRules);

    // ---------------------------------------------------------------- draw hooks

    /// <summary>Call after new draws are saved (in the same transaction).</summary>
    public async Task OnDrawsAddedAsync(IReadOnlyList<Draw> added, CancellationToken ct = default)
    {
        if (added.Count == 0) return;
        var all = await db.Draws.AsNoTracking().OrderBy(d => d.Sequence).ToListAsync(ct);
        var events = MainEvents(all, lottery);
        var indexBySequence = events.Select((e, i) => (e.Sequence, i)).ToDictionary(t => t.Sequence, t => t.i);

        FeatureSet? previousFeatures = null;
        int previousIndex = -1;
        foreach (var draw in added.OrderBy(d => d.Sequence))
        {
            int index = indexBySequence[draw.Sequence];
            var scored = await ScoreTargetAsync(draw, ct);
            await UpdateHedgeAsync(scored, draw.Sequence, ct);

            if (index >= 2)
            {
                var prefixFeatures = previousIndex == index - 1 && previousFeatures is not null
                    ? previousFeatures
                    : Features(Backtester.Prefix(events, index));
                await UpdateOnlineModelAsync(prefixFeatures, events[index], ct);
            }
            await db.SaveChangesAsync(ct);

            if (index + 1 <= options.BacktestWarmup) continue;
            var features = Features(Backtester.Prefix(events, index + 1));
            await FreezeAsync(draw.Sequence, features, ct);
            await db.SaveChangesAsync(ct);
            previousFeatures = features;
            previousIndex = index;
        }
    }

    /// <summary>Call after a draw's numbers are edited and saved. Lines targeting it are re-scored.
    /// Unscored lines that could have seen it are discarded and frozen again. Scored lines that saw
    /// it are kept but marked invalid. Hedge and online-model updates already applied are not
    /// rewound.</summary>
    public async Task OnDrawEditedAsync(Draw draw, CancellationToken ct = default)
    {
        var targeting = await db.ShadowPredictions
            .Where(s => s.TargetDrawId == draw.Id && !s.Invalid)
            .ToListAsync(ct);
        foreach (var shadow in targeting) Score(shadow, draw);

        var downstream = await db.ShadowPredictions
            .Where(s => s.CutoffSequence >= draw.Sequence)
            .ToListAsync(ct);
        foreach (var shadow in downstream)
        {
            if (shadow.Reward is null) db.ShadowPredictions.Remove(shadow);
            else shadow.Invalid = true;
        }
        await db.SaveChangesAsync(ct);

        var all = await db.Draws.AsNoTracking().OrderBy(d => d.Sequence).ToListAsync(ct);
        if (all.Count <= options.BacktestWarmup) return;
        var events = MainEvents(all, lottery);
        await FreezeAsync(events[^1].Sequence, Features(events), ct);
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- freeze / score

    /// <summary>Freezes one line per live strategy plus the hedge ensemble for the draw after
    /// <paramref name="cutoffSequence"/>. Idempotent per (strategy, cutoff).</summary>
    public async Task FreezeAsync(int cutoffSequence, FeatureSet features, CancellationToken ct = default)
    {
        var existing = (await db.ShadowPredictions
                .Where(s => s.CutoffSequence == cutoffSequence)
                .Select(s => s.StrategyName)
                .ToListAsync(ct))
            .ToHashSet();
        var members = await LoadMembersAsync(ct);
        double p3 = Backtester.ThreePlusProbability(features.Pool.NextPoolSize, features.PickCount);
        double expected = (double)features.PickCount * features.PickCount / features.Pool.NextPoolSize;
        var now = DateTime.UtcNow;

        void Add(ScoringStrategy strategy, string kind, Dictionary<int, double> scores, string weights)
        {
            var line = PredictionEngine.GenerateFromScores(features, scores, strategy);
            db.ShadowPredictions.Add(new ShadowPrediction
            {
                StrategyName = strategy.Name,
                StrategyKind = kind,
                Weights = weights,
                CutoffSequence = cutoffSequence,
                NumbersCsv = string.Join(",", line.Numbers),
                ScoresCsv = string.Join(",", LearningSignals.Standardise(scores)
                    .Select(v => Math.Round(v, 5).ToString("R", Inv))),
                RandomThreePlusProbability = p3,
                RandomExpectedMatches = expected,
                CreatedUtc = now,
            });
            existing.Add(strategy.Name);
        }

        foreach (var member in members.Where(m => !existing.Contains(m.Strategy.Name)))
            Add(member.Strategy, member.Kind, PredictionEngine.ScoreNumbers(features, member.Strategy),
                member.Strategy.Describe());

        if (!existing.Contains(Backtester.EnsembleName))
        {
            var weights = await LoadHedgeWeightsAsync(members, ct);
            var (scores, pseudo) = PredictionEngine.EnsembleScores(
                features, members.Select(m => m.Strategy).ToList(), weights, Backtester.EnsembleName);
            Add(pseudo, KindEnsemble, scores, "hedge: " + string.Join(", ", weights
                .OrderByDescending(kv => kv.Value).Take(5)
                .Select(kv => $"{kv.Key}={kv.Value:0.###}")));
        }
    }

    private async Task<List<ShadowPrediction>> ScoreTargetAsync(Draw draw, CancellationToken ct)
    {
        var pending = await db.ShadowPredictions
            .Where(s => s.CutoffSequence == draw.Sequence - 1 && s.TargetDrawId == null && !s.Invalid)
            .ToListAsync(ct);
        foreach (var shadow in pending) Score(shadow, draw);
        return pending;
    }

    private void Score(ShadowPrediction shadow, Draw draw)
    {
        var actual = draw.Numbers();
        int matches = Backtester.CountMatches(shadow.Numbers(), actual);
        double scoreZ = LearningSignals.ScoreZ(shadow.Scores(), actual);
        shadow.TargetDrawId = draw.Id;
        shadow.Matches = matches;
        shadow.ThreePlus = matches >= 3;
        shadow.ScoreZ = scoreZ;
        shadow.Reward = LearningSignals.Reward(
            scoreZ, matches >= 3, shadow.RandomThreePlusProbability, options.ThreePlusRewardWeight);
        shadow.ScoredUtc = DateTime.UtcNow;
    }

    // ---------------------------------------------------------------- hedge

    private async Task UpdateHedgeAsync(IReadOnlyList<ShadowPrediction> scored, int sequence, CancellationToken ct)
    {
        var rewards = scored
            .Where(s => s.StrategyKind != KindEnsemble && s.Reward is not null)
            .ToList();
        if (rewards.Count == 0) return;

        var rows = await db.HedgeWeightStates.ToListAsync(ct);
        var added = ApplyHedgeUpdate(rows,
            rewards.Select(s => (s.StrategyName, s.Reward!.Value)).ToList(),
            options.HedgeEta, options.HedgeFixedShare, sequence);
        db.HedgeWeightStates.AddRange(added);
    }

    /// <summary>One live hedge step: logW += η·reward for each rewarded strategy not yet updated
    /// through <paramref name="sequence"/>, then fixed-share renormalisation. Strategies without a
    /// row start at the mean log-weight. Returns the rows that were created.</summary>
    public static List<HedgeWeightState> ApplyHedgeUpdate(
        List<HedgeWeightState> rows, IReadOnlyList<(string Name, double Reward)> rewards,
        double eta, double fixedShare, int sequence)
    {
        var byName = rows.ToDictionary(r => r.StrategyName);
        double initial = rows.Count > 0 ? rows.Average(r => r.LogWeight) : 0;
        var created = new List<HedgeWeightState>();
        bool changed = false;
        foreach (var (name, reward) in rewards)
        {
            if (!byName.TryGetValue(name, out var row))
            {
                row = new HedgeWeightState { StrategyName = name, LogWeight = initial };
                created.Add(row);
                rows.Add(row);
                byName[name] = row;
            }
            if (row.UpdatedThroughSequence >= sequence) continue;
            row.LogWeight += eta * reward;
            row.UpdatedThroughSequence = sequence;
            changed = true;
        }
        if (changed) NormaliseWithFixedShare(rows, fixedShare);
        return created;
    }

    /// <summary>Renormalises log-weights to a distribution, then mixes in α of the uniform one.</summary>
    public static void NormaliseWithFixedShare(IReadOnlyList<HedgeWeightState> rows, double alpha)
    {
        if (rows.Count == 0) return;
        double max = rows.Max(r => r.LogWeight);
        double sum = rows.Sum(r => Math.Exp(r.LogWeight - max));
        foreach (var row in rows)
        {
            double p = Math.Exp(row.LogWeight - max) / sum;
            row.LogWeight = Math.Log((1 - alpha) * p + alpha / rows.Count);
        }
    }

    /// <summary>Live hedge weights normalised over the given members. Members without a row get
    /// the average weight; with no rows at all, the weights are uniform.</summary>
    public async Task<Dictionary<string, double>> LoadHedgeWeightsAsync(
        IReadOnlyList<LiveMember> members, CancellationToken ct = default)
    {
        var rows = await db.HedgeWeightStates.AsNoTracking().ToListAsync(ct);
        return NormalisedHedge(rows, members.Select(m => m.Strategy.Name));
    }

    public static Dictionary<string, double> NormalisedHedge(
        IReadOnlyList<HedgeWeightState> rows, IEnumerable<string> memberNames)
    {
        var names = memberNames.Distinct().ToList();
        if (names.Count == 0) return [];
        var byName = rows.ToDictionary(r => r.StrategyName, r => r.LogWeight);
        var known = names.Where(byName.ContainsKey).Select(n => byName[n]).ToList();
        double fallback = known.Count > 0 ? known.Average() : 0;
        var logs = names.ToDictionary(n => n, n => byName.GetValueOrDefault(n, fallback));
        double max = logs.Values.Max();
        double sum = logs.Values.Sum(v => Math.Exp(v - max));
        return logs.ToDictionary(kv => kv.Key, kv => Math.Exp(kv.Value - max) / sum);
    }

    /// <summary>First-time initialisation from the backtest's final walk-forward hedge weights.</summary>
    public async Task InitialiseHedgeAsync(
        IReadOnlyDictionary<string, double> backtestWeights, IReadOnlyList<LiveMember> members,
        int throughSequence, CancellationToken ct = default)
    {
        if (await db.HedgeWeightStates.AnyAsync(ct)) return;
        var known = members.Where(m => backtestWeights.GetValueOrDefault(m.Strategy.Name) > 0).ToList();
        double fallback = known.Count > 0
            ? known.Average(m => Math.Log(backtestWeights[m.Strategy.Name]))
            : 0;
        var rows = members.Select(m => new HedgeWeightState
        {
            StrategyName = m.Strategy.Name,
            LogWeight = backtestWeights.GetValueOrDefault(m.Strategy.Name) > 0
                ? Math.Log(backtestWeights[m.Strategy.Name])
                : fallback,
            UpdatedThroughSequence = throughSequence,
        }).ToList();
        NormaliseWithFixedShare(rows, 0);
        db.HedgeWeightStates.AddRange(rows);
    }

    // ---------------------------------------------------------------- online model

    public async Task<(OnlineBallModel Model, OnlineModelState State)> LoadOnlineModelAsync(CancellationToken ct = default)
    {
        var state = db.OnlineModelStates.Local.FirstOrDefault(m => m.Name == OnlineBallModel.StrategyName)
            ?? await db.OnlineModelStates.FirstOrDefaultAsync(m => m.Name == OnlineBallModel.StrategyName, ct);
        if (state is null)
        {
            state = new OnlineModelState { Name = OnlineBallModel.StrategyName, UpdatedUtc = DateTime.UtcNow };
            db.OnlineModelStates.Add(state);
        }
        var model = new OnlineBallModel(
            options.OnlineLearningRate, options.OnlineL2,
            ParseCsv(state.WeightsCsv), ParseCsv(state.AccumulatorsCsv), state.Updates);
        return (model, state);
    }

    public static void StoreOnlineModel(OnlineBallModel model, OnlineModelState state, int throughSequence)
    {
        state.WeightsCsv = string.Join(",", model.Weights.Select(v => v.ToString("R", Inv)));
        state.AccumulatorsCsv = string.Join(",", model.Accumulators.Select(v => v.ToString("R", Inv)));
        state.Updates = model.Updates;
        state.UpdatedThroughSequence = throughSequence;
        state.UpdatedUtc = DateTime.UtcNow;
    }

    private async Task UpdateOnlineModelAsync(FeatureSet prefixFeatures, DrawEvent draw, CancellationToken ct)
    {
        var (model, state) = await LoadOnlineModelAsync(ct);
        if (state.UpdatedThroughSequence >= draw.Sequence) return;
        model.Update(prefixFeatures, draw.Numbers);
        StoreOnlineModel(model, state, draw.Sequence);
    }

    private static double[]? ParseCsv(string csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? null
            : csv.Split(',').Select(v => double.Parse(v, Inv)).ToArray();

    // ---------------------------------------------------------------- population / outcomes

    /// <summary>Built-in strategies (control group), live learned challengers and the online model.</summary>
    public async Task<IReadOnlyList<LiveMember>> LoadMembersAsync(CancellationToken ct = default)
    {
        var learned = await db.LearnedStrategies.AsNoTracking()
            .Where(s => s.Status != LearnedStatus.Retired)
            .OrderBy(s => s.Id)
            .ToListAsync(ct);
        var (model, _) = await LoadOnlineModelAsync(ct);
        var members = ScoringStrategy.Candidates.Select(s => new LiveMember(s, KindBuiltin))
            .Concat(learned.Select(s => new LiveMember(StrategyOptimizer.ToStrategy(s), KindLearned)))
            .Append(new LiveMember(model.ToStrategy(), KindOnline))
            .DistinctBy(m => m.Strategy.Name)
            .ToList();
        return members;
    }

    public async Task<IReadOnlyList<ProspectiveOutcome>> LoadOutcomesAsync(CancellationToken ct = default) =>
        (await db.ShadowPredictions.AsNoTracking()
            .Where(s => s.Reward != null && !s.Invalid)
            .ToListAsync(ct))
        .Select(s => new ProspectiveOutcome(
            s.StrategyName, s.StrategyKind, s.CutoffSequence, s.Matches ?? 0, s.ThreePlus ?? false,
            s.ScoreZ ?? 0, s.Reward ?? 0, s.RandomThreePlusProbability, s.RandomExpectedMatches))
        .ToList();

    // ---------------------------------------------------------------- key/value state

    public async Task<string?> GetStateAsync(string key, CancellationToken ct = default) =>
        (await db.LearningStateEntries.FirstOrDefaultAsync(e => e.Key == key, ct))?.Value;

    public async Task SetStateAsync(string key, string value, CancellationToken ct = default)
    {
        var entry = db.LearningStateEntries.Local.FirstOrDefault(e => e.Key == key)
            ?? await db.LearningStateEntries.FirstOrDefaultAsync(e => e.Key == key, ct);
        if (entry is null) db.LearningStateEntries.Add(new LearningStateEntry { Key = key, Value = value });
        else entry.Value = value;
    }
}
