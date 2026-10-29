using LottoPredictor.Core.Analysis;
using LottoPredictor.Core.Data;
using LottoPredictor.Core.Dtos;
using LottoPredictor.Core.Models;
using LottoPredictor.Core.Services;
using Microsoft.EntityFrameworkCore;
using static LottoPredictor.Tests.TestData;

namespace LottoPredictor.Tests;

/// <summary>Unit tests for the prospective learning signals, the online model and the promotion gate.</summary>
public class LearningLoopTests
{
    [Fact]
    public void ScoreZ_has_zero_mean_and_unit_variance_under_fair_draws()
    {
        var rng = new Random(5);
        var scores = Enumerable.Range(1, 59).ToDictionary(n => n, _ => rng.NextDouble() * 10);
        var standardised = LearningSignals.Standardise(scores);
        var values = new List<double>();
        for (int i = 0; i < 20000; i++)
        {
            var drawn = Enumerable.Range(1, 59).OrderBy(_ => rng.Next()).Take(6).ToArray();
            values.Add(LearningSignals.ScoreZ(standardised, drawn));
        }
        double mean = values.Average();
        double variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
        Assert.InRange(mean, -0.03, 0.03);
        Assert.InRange(variance, 0.95, 1.05);
    }

    [Fact]
    public void ScoreZ_is_positive_when_the_ranking_favours_the_drawn_balls()
    {
        var scores = Enumerable.Range(1, 59).ToDictionary(n => n, n => n <= 6 ? 1.0 : 0.0);
        double z = LearningSignals.ScoreZ(LearningSignals.Standardise(scores), [1, 2, 3, 4, 5, 6]);
        Assert.True(z > 5, $"z={z}");
        Assert.Equal(0.0, LearningSignals.ScoreZ(
            LearningSignals.Standardise(Enumerable.Range(1, 59).ToDictionary(n => n, _ => 3.0)),
            [1, 2, 3, 4, 5, 6]));
    }

    private static OnlineBallModel TrainWalkForward(IReadOnlyList<DrawEvent> draws, int start)
    {
        var model = new OnlineBallModel();
        for (int i = start; i < draws.Count; i++)
            model.Update(FeatureCalculator.Compute(Backtester.Prefix(draws, i)), draws[i].Numbers);
        return model;
    }

    [Fact]
    public void Online_model_learns_a_planted_bias_and_stays_small_on_fair_data()
    {
        var rng = new Random(11);
        var biased = new List<DrawEvent>();
        for (int i = 1; i <= 400; i++)
        {
            var set = new HashSet<int>();
            if (rng.NextDouble() < 0.6) set.Add(7);
            while (set.Count < 6) set.Add(rng.Next(1, 60));
            biased.Add(Ev(i, [.. set]));
        }
        var biasedModel = TrainWalkForward(biased, 60);
        var scores = PredictionEngine.ScoreNumbers(
            FeatureCalculator.Compute(biased), biasedModel.ToStrategy());
        Assert.Equal(7, scores.OrderByDescending(kv => kv.Value).First().Key);

        var fairModel = TrainWalkForward(RandomHistory(400, 59, seed: 3), 60);
        double biasedNorm = Math.Sqrt(biasedModel.Weights.Sum(w => w * w));
        double fairNorm = Math.Sqrt(fairModel.Weights.Sum(w => w * w));
        Assert.True(biasedNorm > 2 * fairNorm, $"biased={biasedNorm}, fair={fairNorm}");
        Assert.Equal(340, fairModel.Updates);
    }

    [Fact]
    public void Live_hedge_moves_weight_toward_the_rewarded_strategy_and_keeps_a_floor()
    {
        var rows = new List<HedgeWeightState>
        {
            new() { StrategyName = "good", LogWeight = Math.Log(1.0 / 3) },
            new() { StrategyName = "bad", LogWeight = Math.Log(1.0 / 3) },
            new() { StrategyName = "neutral", LogWeight = Math.Log(1.0 / 3) },
        };
        for (int seq = 1; seq <= 200; seq++)
            ShadowLedger.ApplyHedgeUpdate(rows, [("good", 1.0), ("bad", -1.0), ("neutral", 0.0)],
                eta: 0.05, fixedShare: 0.01, sequence: seq);

        var w = ShadowLedger.NormalisedHedge(rows, ["good", "bad", "neutral"]);
        Assert.Equal(1.0, w.Values.Sum(), 9);
        Assert.True(w["good"] > w["neutral"] && w["neutral"] > w["bad"]);
        Assert.True(w["good"] > 0.9, $"good={w["good"]}");
        Assert.True(w["bad"] >= 0.01 / 3 * 0.99, $"fixed share floor broken: bad={w["bad"]}");

        // The same draw is never applied twice.
        double before = rows.Single(r => r.StrategyName == "good").LogWeight;
        ShadowLedger.ApplyHedgeUpdate(rows, [("good", 5.0)], 0.05, 0.01, sequence: 200);
        Assert.Equal(before, rows.Single(r => r.StrategyName == "good").LogWeight);

        // A newcomer starts at the mean log-weight rather than at zero.
        var created = ShadowLedger.ApplyHedgeUpdate(rows, [("newcomer", 0.0)], 0.05, 0.01, sequence: 201);
        Assert.Single(created);
    }

    // ------------------------------------------------------------------ promotion gate

    private static readonly SelectionSettings Settings = new(100, 0.95, 20);

    private static IEnumerable<ProspectiveOutcome> Track(
        string name, int draws, Func<int, double> reward, int threePlusHits = 2, double p3 = 0.0186) =>
        Enumerable.Range(1, draws).Select(i => new ProspectiveOutcome(
            name, "test", i, i <= threePlusHits ? 3 : 1, i <= threePlusHits,
            reward(i), reward(i), p3, 0.61));

    private static double Alternating(int i) => i % 2 == 0 ? 1 : -1;

    private static SelectionDecision Decide(IEnumerable<ProspectiveOutcome> outcomes,
        string? currentName = null, string? currentSource = null, int promotedAt = 0, int latest = 100) =>
        ProspectiveEvaluator.Choose("backtest-pick", ProspectiveEvaluator.Summarise(outcomes),
            currentName, currentSource, promotedAt, latest, Settings);

    [Fact]
    public void Selector_uses_the_backtest_until_enough_prospective_draws()
    {
        var decision = Decide(Track(Backtester.EnsembleName, 99, Alternating)
            .Concat(Track("strong", 99, i => 3 + Alternating(i))));
        Assert.Equal("backtest-pick", decision.ActiveName);
        Assert.Equal(ProspectiveEvaluator.SourceBacktest, decision.Source);
    }

    [Fact]
    public void Selector_keeps_the_ensemble_on_weak_evidence_and_promotes_on_strong_evidence()
    {
        var weak = Decide(Track(Backtester.EnsembleName, 100, _ => 0)
            .Concat(Track("weak", 100, i => 0.05 + Alternating(i))));
        Assert.Equal(Backtester.EnsembleName, weak.ActiveName);
        Assert.Equal(ProspectiveEvaluator.SourceProspective, weak.Source);

        var strong = Decide(Track(Backtester.EnsembleName, 100, _ => 0)
            .Concat(Track("strong", 100, i => 0.6 + Alternating(i) * 0.5)));
        Assert.Equal("strong", strong.ActiveName);
        Assert.Equal(100, strong.PromotedAtSequence);
    }

    [Fact]
    public void Selector_applies_a_bonferroni_correction_for_many_challengers()
    {
        // A z≈2 edge is enough for one challenger, but not when it is the best of ten.
        var ensemble = Track(Backtester.EnsembleName, 100, _ => 0);
        var edge = Track("edge", 100, i => 0.2 + Alternating(i));
        Assert.Equal("edge", Decide(ensemble.Concat(edge)).ActiveName);

        var crowd = Enumerable.Range(0, 9).SelectMany(k => Track($"noise-{k}", 100, Alternating));
        Assert.Equal(Backtester.EnsembleName, Decide(ensemble.Concat(edge).Concat(crowd)).ActiveName);
    }

    [Fact]
    public void Selector_blocks_a_challenger_whose_three_plus_rate_is_credibly_below_random()
    {
        var decision = Decide(Track(Backtester.EnsembleName, 300, _ => 0, p3: 0.05)
            .Concat(Track("no-hits", 300, i => 0.6 + Alternating(i) * 0.5, threePlusHits: 0, p3: 0.05)),
            latest: 300);
        Assert.Equal(Backtester.EnsembleName, decision.ActiveName);
    }

    [Fact]
    public void Selector_holds_a_promoted_strategy_for_the_minimum_period()
    {
        var outcomes = Track(Backtester.EnsembleName, 100, i => 1 + Alternating(i))
            .Concat(Track("promoted", 100, Alternating)).ToList();
        var held = Decide(outcomes, "promoted", ProspectiveEvaluator.SourceProspective, promotedAt: 95);
        Assert.Equal("promoted", held.ActiveName);
        Assert.Equal(95, held.PromotedAtSequence);

        var replaced = Decide(outcomes, "promoted", ProspectiveEvaluator.SourceProspective, promotedAt: 50);
        Assert.Equal(Backtester.EnsembleName, replaced.ActiveName);
    }
}

/// <summary>Database-backed tests of the shadow ledger, evolution and schema bootstrap.</summary>
public sealed class ShadowLedgerTests : IDisposable
{
    private sealed class TempDbFactory : IDbContextFactory<LottoDbContext>, IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"lotto-ledger-{Guid.NewGuid():N}.db");

        public LottoDbContext CreateDbContext()
        {
            var ctx = new LottoDbContext(new DbContextOptionsBuilder<LottoDbContext>()
                .UseSqlite($"Data Source={Path}").Options);
            ctx.Database.EnsureCreated();
            return ctx;
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(Path)) File.Delete(Path);
        }
    }

    private readonly TempDbFactory factory = new();
    private readonly AnalysisOptions options = new()
    {
        BacktestEvalWindow = 30,
        BacktestWarmup = 150,
        GenerationIntervalDraws = 2,
    };
    private readonly AnalysisService analysis;
    private readonly DrawService draws;

    public ShadowLedgerTests()
    {
        analysis = new AnalysisService(factory, options);
        draws = new DrawService(factory, analysis, options: options);
    }

    public void Dispose() => factory.Dispose();

    private void Seed(IEnumerable<DrawEvent> events)
    {
        using var db = factory.CreateDbContext();
        foreach (var e in events)
        {
            var draw = new Draw { Sequence = e.Sequence, DrawNumber = e.DrawNumber, Date = e.Date, Source = "csv" };
            draw.SetNumbers(e.Numbers);
            db.Draws.Add(draw);
        }
        db.SaveChanges();
    }

    [Fact]
    public async Task Lines_are_frozen_before_the_draw_and_scored_after_it()
    {
        Seed(RandomHistory(200, 59));
        var snapshot = await analysis.RecalculateAsync();

        using (var db = factory.CreateDbContext())
        {
            var frozen = db.ShadowPredictions.Where(s => s.CutoffSequence == 200).ToList();
            var names = frozen.Select(s => s.StrategyName).ToHashSet();
            Assert.All(ScoringStrategy.Candidates, s => Assert.Contains(s.Name, names));
            Assert.Contains(Backtester.EnsembleName, names);
            Assert.Contains(OnlineBallModel.StrategyName, names);
            Assert.Contains(frozen, s => s.StrategyKind == ShadowLedger.KindLearned);
            Assert.All(frozen, s => Assert.Null(s.Reward));

            // The frozen line is exactly what the strategy produced from draws 1..200.
            var strategy = ScoringStrategy.Candidates[0];
            Assert.Equal(PredictionEngine.Generate(snapshot.Features, strategy).Numbers,
                frozen.Single(s => s.StrategyName == strategy.Name).Numbers());
        }

        int updatesBefore;
        using (var db = factory.CreateDbContext())
            updatesBefore = db.OnlineModelStates.Single().Updates;

        int[] result = [1, 2, 3, 4, 5, 6];
        var added = await draws.AddDrawAsync(new AddDrawRequest(result));

        using (var db = factory.CreateDbContext())
        {
            var scored = db.ShadowPredictions.Where(s => s.CutoffSequence == 200).ToList();
            Assert.All(scored, s =>
            {
                Assert.Equal(added.Id, s.TargetDrawId);
                Assert.Equal(Backtester.CountMatches(s.Numbers(), result), s.Matches);
                Assert.NotNull(s.Reward);
            });
            Assert.Contains(db.ShadowPredictions, s => s.CutoffSequence == 201 && s.Reward == null);

            var online = db.OnlineModelStates.Single();
            Assert.Equal(updatesBefore + 1, online.Updates);
            Assert.Equal(201, online.UpdatedThroughSequence);

            var hedge = db.HedgeWeightStates.ToList();
            Assert.Contains(hedge, h => h.UpdatedThroughSequence == 201);
            Assert.Equal(1.0, hedge.Sum(h => Math.Exp(h.LogWeight)), 6);
        }
    }

    [Fact]
    public async Task Editing_a_draw_rescores_its_lines_and_invalidates_lines_that_saw_it()
    {
        Seed(RandomHistory(200, 59));
        await analysis.RecalculateAsync();
        var added = await draws.AddDrawAsync(new AddDrawRequest([1, 2, 3, 4, 5, 6]));

        int[] corrected = [7, 8, 9, 10, 11, 12];
        await draws.UpdateDrawAsync(added.Id, new UpdateDrawRequest(corrected));
        using (var db = factory.CreateDbContext())
        {
            Assert.All(db.ShadowPredictions.Where(s => s.CutoffSequence == 200).ToList(), s =>
                Assert.Equal(Backtester.CountMatches(s.Numbers(), corrected), s.Matches));
            var refrozen = db.ShadowPredictions.Where(s => s.CutoffSequence == 201).ToList();
            Assert.NotEmpty(refrozen);
            Assert.All(refrozen, s => Assert.False(s.Invalid));
        }

        int olderId;
        using (var db = factory.CreateDbContext())
            olderId = db.Draws.Single(d => d.Sequence == 180).Id;
        await draws.UpdateDrawAsync(olderId, new UpdateDrawRequest([13, 14, 15, 16, 17, 18]));
        using (var db = factory.CreateDbContext())
        {
            Assert.All(db.ShadowPredictions.Where(s => s.CutoffSequence == 200).ToList(),
                s => Assert.True(s.Invalid));
            var ledger = new ShadowLedger(db, LotteryProfile.UkLotto, options);
            Assert.Empty(await ledger.LoadOutcomesAsync());
        }
    }

    [Fact]
    public async Task Evolution_advances_only_with_new_draws_and_admits_challengers_per_interval()
    {
        Seed(RandomHistory(200, 59));
        var first = await analysis.RecalculateAsync();
        Assert.Equal(1, first.LearningGeneration);
        using (var db = factory.CreateDbContext())
        {
            var learned = db.LearnedStrategies.ToList();
            Assert.Equal(options.ChallengersAdmittedPerGeneration, learned.Count);
            Assert.All(learned, s =>
            {
                Assert.Equal(LearnedStatus.Challenger, s.Status);
                Assert.Equal(200, s.BornAfterSequence);
            });
        }

        analysis.Invalidate();
        Assert.Equal(1, (await analysis.RecalculateAsync()).LearningGeneration);

        await draws.AddDrawAsync(new AddDrawRequest([1, 2, 3, 4, 5, 6]));
        var second = await analysis.RecalculateAsync();
        Assert.Equal(2, second.LearningGeneration);
        using (var db = factory.CreateDbContext())
            Assert.Equal(options.ChallengersAdmittedPerGeneration, db.LearnedStrategies.Count());

        await draws.AddDrawAsync(new AddDrawRequest([7, 8, 9, 10, 11, 12]));
        var third = await analysis.RecalculateAsync();
        Assert.Equal(3, third.LearningGeneration);
        using (var db = factory.CreateDbContext())
        {
            Assert.Equal(2 * options.ChallengersAdmittedPerGeneration, db.LearnedStrategies.Count());
            Assert.Contains(db.LearnedStrategies, s => s.BornAfterSequence == 202);
        }
        Assert.Contains(third.EnsembleMembers, s => s.Name == OnlineBallModel.StrategyName);
        Assert.Contains(third.EnsembleMembers, s => s.Name.StartsWith("learned-g3-"));
        Assert.Equal(ProspectiveEvaluator.SourceBacktest, third.Selection!.Source);
    }

    [Fact]
    public async Task Learning_schema_bootstraps_a_database_created_before_prospective_learning()
    {
        using (var db = factory.CreateDbContext())
        {
            foreach (var table in new[] { "ShadowPredictions", "HedgeWeightStates", "OnlineModelStates", "LearningStateEntries" })
                await db.Database.ExecuteSqlRawAsync($"DROP TABLE \"{table}\";");
            foreach (var column in new[] { "BornAfterSequence", "Status", "RetiredUtc" })
                await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"LearnedStrategies\" DROP COLUMN \"{column}\";");
        }

        using (var db = factory.CreateDbContext())
        {
            await LearningSchema.EnsureAsync(db);
            await LearningSchema.EnsureAsync(db); // idempotent
            db.LearnedStrategies.Add(new LearnedStrategy { Name = "learned-x", BornAfterSequence = 5 });
            db.ShadowPredictions.Add(new ShadowPrediction
            {
                StrategyName = "a", StrategyKind = "builtin", Weights = "", CutoffSequence = 1,
                NumbersCsv = "1,2,3,4,5,6", ScoresCsv = "0", CreatedUtc = DateTime.UtcNow,
            });
            db.HedgeWeightStates.Add(new HedgeWeightState { StrategyName = "a" });
            db.OnlineModelStates.Add(new OnlineModelState { Name = "m" });
            db.LearningStateEntries.Add(new LearningStateEntry { Key = "k", Value = "v" });
            await db.SaveChangesAsync();
        }

        using (var db = factory.CreateDbContext())
        {
            var learned = db.LearnedStrategies.Single();
            Assert.Equal(LearnedStatus.Challenger, learned.Status);
            Assert.Equal(5, learned.BornAfterSequence);
            Assert.Single(db.ShadowPredictions);
        }
    }
}
