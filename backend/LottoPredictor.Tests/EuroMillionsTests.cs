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

    private sealed class EuroMillionsSelection : ILotterySelection
    {
        public LotteryProfile Current => LotteryProfile.EuroMillions;
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
