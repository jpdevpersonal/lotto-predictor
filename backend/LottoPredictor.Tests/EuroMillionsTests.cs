using LottoPredictor.Core.Analysis;
using LottoPredictor.Core.Data;
using LottoPredictor.Core.Dtos;
using LottoPredictor.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace LottoPredictor.Tests;

public class EuroMillionsTests
{
    [Fact]
    public void Prediction_engine_generates_five_main_numbers_and_two_stars()
    {
        var history = TestData.RandomHistory(200, 50)
            .Select(draw => draw with { Numbers = draw.Numbers.Take(5).ToArray() })
            .ToList();
        var starHistory = TestData.RandomHistory(200, 12)
            .Select(draw => draw with { Numbers = draw.Numbers.Take(2).ToArray() })
            .ToList();

        var mainPrediction = PredictionEngine.Generate(
            FeatureCalculator.Compute(history), ScoringStrategy.Candidates[0]);
        var starPrediction = PredictionEngine.Generate(
            FeatureCalculator.Compute(starHistory), ScoringStrategy.Candidates[0]);

        Assert.Equal(5, mainPrediction.Numbers.Length);
        Assert.Equal(5, mainPrediction.Numbers.Distinct().Count());
        Assert.All(mainPrediction.Numbers, number => Assert.InRange(number, 1, 50));
        Assert.Equal(2, starPrediction.Numbers.Length);
        Assert.Equal(2, starPrediction.Numbers.Distinct().Count());
        Assert.All(starPrediction.Numbers, number => Assert.InRange(number, 1, 12));
    }

    [Fact]
    public async Task Draw_service_stores_one_round_with_two_lucky_stars()
    {
        string path = Path.Combine(Path.GetTempPath(), $"euromillions-test-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new TestDbFactory(path);
            var selection = new EuroMillionsSelection();
            var service = new DrawService(factory, new NoOpAnalysisService(), selection);

            var added = await service.AddDrawRoundsAsync(new AddDrawRoundsRequest(
                [[2, 10, 23, 37, 47]], [null], [[3, 5]]));

            var draw = Assert.Single(added);
            Assert.Equal([2, 10, 23, 37, 47], draw.Numbers);
            Assert.Equal([3, 5], draw.LuckyStars);
            await Assert.ThrowsAsync<ValidationFailedException>(() =>
                service.AddDrawRoundsAsync(new AddDrawRoundsRequest(
                    [[2, 10, 23, 37, 47]], [null], [[5, 5]])));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Updating_a_draw_preserves_both_lucky_stars()
    {
        string path = Path.Combine(Path.GetTempPath(), $"euromillions-test-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new TestDbFactory(path);
            var service = new DrawService(factory, new NoOpAnalysisService(), new EuroMillionsSelection());
            var draw = await service.AddDrawAsync(new AddDrawRequest(
                [2, 10, 23, 37, 47], null, [3, 5]));

            var updated = await service.UpdateDrawAsync(draw.Id, new UpdateDrawRequest(
                [1, 11, 22, 33, 44], null, [7, 12]));

            Assert.Equal([1, 11, 22, 33, 44], updated.Numbers);
            Assert.Equal([7, 12], updated.LuckyStars);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Lottery_histories_and_predictions_are_isolated_by_selection()
    {
        string ukPath = Path.Combine(Path.GetTempPath(), $"uk-lotto-test-{Guid.NewGuid():N}.db");
        string euroPath = Path.Combine(Path.GetTempPath(), $"euromillions-test-{Guid.NewGuid():N}.db");
        try
        {
            var selection = new MutableLotterySelection();
            var factory = new ProfileDbFactory(selection, ukPath, euroPath);
            SeedDraws(factory, TestData.RandomHistory(5, 59));

            selection.CurrentProfile = LotteryProfile.EuroMillions;
            SeedDraws(factory, TestData.RandomHistory(5, 50)
                .Select(draw => draw with { Numbers = draw.Numbers.Take(5).ToArray() }));

            var analysis = new AnalysisService(factory, new AnalysisOptions
            {
                BacktestEvalWindow = 2,
                BacktestWarmup = 2,
            }, selection);
            var predictions = new PredictionService(factory, analysis);

            selection.CurrentProfile = LotteryProfile.UkLotto;
            var ukPrediction = await predictions.GenerateAsync();
            var ukSnapshot = await analysis.GetSnapshotAsync();

            selection.CurrentProfile = LotteryProfile.EuroMillions;
            var euroPrediction = await predictions.GenerateAsync();
            var euroSnapshot = await analysis.GetSnapshotAsync();

            Assert.Equal(5, ukSnapshot.Draws.Count);
            Assert.Equal(59, ukSnapshot.PoolSize);
            Assert.Equal(6, ukPrediction.Numbers.Length);
            Assert.Empty(ukPrediction.LuckyStars);
            Assert.Equal(5, euroSnapshot.Draws.Count);
            Assert.Equal(50, euroSnapshot.PoolSize);
            Assert.Equal(5, euroPrediction.Numbers.Length);
            Assert.Equal(2, euroPrediction.LuckyStars.Length);

            await using var euroDb = await factory.CreateDbContextAsync();
            Assert.Single(euroDb.Predictions);
            selection.CurrentProfile = LotteryProfile.UkLotto;
            await using var ukDb = await factory.CreateDbContextAsync();
            Assert.Single(ukDb.Predictions);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(ukPath)) File.Delete(ukPath);
            if (File.Exists(euroPath)) File.Delete(euroPath);
        }
    }

    private static void SeedDraws(IDbContextFactory<LottoDbContext> factory, IEnumerable<DrawEvent> history)
    {
        using var db = factory.CreateDbContext();
        foreach (var item in history)
        {
            var draw = new Draw
            {
                Sequence = item.Sequence,
                DrawNumber = item.DrawNumber,
                Date = item.Date,
                Source = "test",
            };
            draw.SetNumbers(item.Numbers);
            db.Draws.Add(draw);
        }
        db.SaveChanges();
    }

    private sealed class EuroMillionsSelection : ILotterySelection
    {
        public LotteryProfile Current => LotteryProfile.EuroMillions;
    }

    private sealed class MutableLotterySelection : ILotterySelection
    {
        public LotteryProfile CurrentProfile { get; set; } = LotteryProfile.UkLotto;
        public LotteryProfile Current => CurrentProfile;
    }

    private sealed class ProfileDbFactory(
        MutableLotterySelection selection,
        string ukPath,
        string euroPath) : IDbContextFactory<LottoDbContext>
    {
        public LottoDbContext CreateDbContext()
        {
            string path = selection.Current == LotteryProfile.EuroMillions ? euroPath : ukPath;
            var options = new DbContextOptionsBuilder<LottoDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;
            var context = new LottoDbContext(options);
            context.Database.EnsureCreated();
            return context;
        }
    }

    private sealed class TestDbFactory(string path) : IDbContextFactory<LottoDbContext>
    {
        public LottoDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<LottoDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;
            var context = new LottoDbContext(options);
            context.Database.EnsureCreated();
            return context;
        }
    }

    private sealed class NoOpAnalysisService : IAnalysisService
    {
        public Task<AnalysisSnapshot> GetSnapshotAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public void Invalidate()
        {
        }
    }
}
