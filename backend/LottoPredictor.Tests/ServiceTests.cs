using LottoPredictor.Core.Data;
using LottoPredictor.Core.Dtos;
using LottoPredictor.Core.Models;
using LottoPredictor.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace LottoPredictor.Tests;

/// <summary>End-to-end service tests over a real (temp file) SQLite database.</summary>
public sealed class ServiceTests : IDisposable
{
    private sealed class TempDbFactory : IDbContextFactory<LottoDbContext>, IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"lotto-test-{Guid.NewGuid():N}.db");

        public LottoDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<LottoDbContext>()
                .UseSqlite($"Data Source={Path}")
                .Options;
            var ctx = new LottoDbContext(options);
            ctx.Database.EnsureCreated();
            return ctx;
        }

        public void Dispose()
        {
            SqliteConnectionPoolCleaner.Clear();
            if (File.Exists(Path)) File.Delete(Path);
        }
    }

    private static class SqliteConnectionPoolCleaner
    {
        public static void Clear() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private readonly TempDbFactory _factory = new();
    private readonly AnalysisService _analysis;
    private readonly DrawService _drawService;
    private readonly PredictionService _predictionService;

    public ServiceTests()
    {
        _analysis = new AnalysisService(_factory, new AnalysisOptions
        {
            BacktestEvalWindow = 30,
            BacktestWarmup = 150,
        });
        _drawService = new DrawService(_factory, _analysis);
        _predictionService = new PredictionService(_factory, _analysis);
    }

    public void Dispose() => _factory.Dispose();

    private void SeedDraws(int count, int poolSize = 59)
    {
        using var db = _factory.CreateDbContext();
        var events = TestData.RandomHistory(count, poolSize);
        foreach (var e in events)
        {
            var draw = new Draw
            {
                Sequence = e.Sequence,
                DrawNumber = e.DrawNumber,
                Date = e.Date,
                Source = "csv",
            };
            draw.SetNumbers(e.Numbers);
            db.Draws.Add(draw);
        }
        db.SaveChanges();
    }

    [Fact]
    public async Task AddDraw_persists_assigns_next_sequence_and_draw_number()
    {
        SeedDraws(200);
        var added = await _drawService.AddDrawAsync(new AddDrawRequest([5, 10, 15, 20, 25, 59]));

        Assert.Equal(201, added.Sequence);
        Assert.Equal(201, added.DrawNumber);
        Assert.Equal(new[] { 5, 10, 15, 20, 25, 59 }, added.Numbers);
        Assert.Equal("manual", added.Source);
        Assert.Equal(201, await _drawService.CountAsync());

        var latest = await _drawService.GetLatestAsync();
        Assert.Equal(added.Numbers, latest!.Numbers);
    }

    [Fact]
    public async Task AddDraw_rejects_invalid_input()
    {
        SeedDraws(200);
        await Assert.ThrowsAsync<ValidationFailedException>(
            () => _drawService.AddDrawAsync(new AddDrawRequest([1, 2, 3, 4, 5])));
        await Assert.ThrowsAsync<ValidationFailedException>(
            () => _drawService.AddDrawAsync(new AddDrawRequest([1, 1, 3, 4, 5, 6])));
        await Assert.ThrowsAsync<ValidationFailedException>(
            () => _drawService.AddDrawAsync(new AddDrawRequest([1, 2, 3, 4, 5, 60])));
        Assert.Equal(200, await _drawService.CountAsync());
    }

    [Fact]
    public async Task AddDrawRounds_stores_two_sequences_under_one_draw_number()
    {
        SeedDraws(200);
        var rounds = await _drawService.AddDrawRoundsAsync(new AddDrawRoundsRequest([
            [5, 10, 15, 20, 25, 59],
            [3, 12, 21, 30, 39, 48],
        ], [7, 8]));

        Assert.Equal(2, rounds.Count);
        Assert.Equal([201, 202], rounds.Select(draw => draw.Sequence));
        Assert.All(rounds, draw => Assert.Equal(201, draw.DrawNumber));
        Assert.Equal(rounds[0].Date, rounds[1].Date);
        Assert.Equal("Manual Round 1", rounds[0].Machine);
        Assert.Equal("Manual Round 2", rounds[1].Machine);
        Assert.Equal([7, 8], rounds.Select(draw => draw.Bonus));
        Assert.Equal(202, await _drawService.CountAsync());
    }

    [Fact]
    public async Task Bonus_ball_can_be_updated_but_cannot_duplicate_a_main_number()
    {
        SeedDraws(200);
        var added = await _drawService.AddDrawAsync(
            new AddDrawRequest([5, 10, 15, 20, 25, 59], 7));

        var updated = await _drawService.UpdateDrawAsync(
            added.Id, new UpdateDrawRequest(added.Numbers, 8));

        Assert.Equal(8, updated.Bonus);
        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            _drawService.UpdateDrawAsync(added.Id, new UpdateDrawRequest(added.Numbers, 10)));
    }

    [Fact]
    public async Task Statistics_returns_both_rounds_for_the_latest_draw()
    {
        SeedDraws(200);
        var added = await _drawService.AddDrawRoundsAsync(new AddDrawRoundsRequest([
            [5, 10, 15, 20, 25, 59],
            [3, 12, 21, 30, 39, 48],
        ]));
        var statistics = new StatisticsService(_analysis, _drawService);

        var result = await statistics.GetStatisticsAsync();

        Assert.Equal(2, result.LatestRounds.Count);
        Assert.Equal(added.Select(round => round.Id), result.LatestRounds.Select(round => round.Id));
        Assert.All(result.LatestRounds, round => Assert.Equal(201, round.DrawNumber));
    }

    [Fact]
    public async Task Statistics_returns_same_day_legacy_rounds_with_different_draw_numbers()
    {
        SeedDraws(200);
        var first = await _drawService.AddDrawAsync(new AddDrawRequest([5, 10, 15, 20, 25, 59]));
        var second = await _drawService.AddDrawAsync(new AddDrawRequest([3, 12, 21, 30, 39, 48]));
        var statistics = new StatisticsService(_analysis, _drawService);

        var result = await statistics.GetStatisticsAsync();

        Assert.NotEqual(first.DrawNumber, second.DrawNumber);
        Assert.Equal([first.Id, second.Id], result.LatestRounds.Select(round => round.Id));
        Assert.All(result.LatestRounds, round => Assert.Equal(second.Date, round.Date));
    }

    [Fact]
    public async Task AddDrawRounds_rejects_both_atomically_when_either_round_is_invalid()
    {
        SeedDraws(200);

        var exception = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            _drawService.AddDrawRoundsAsync(new AddDrawRoundsRequest([
                [5, 10, 15, 20, 25, 59],
                [1, 1, 3, 4, 5, 6],
            ])));

        Assert.Contains(exception.Errors, error => error.StartsWith("Round 2:"));
        Assert.Equal(200, await _drawService.CountAsync());
    }

    [Fact]
    public async Task DrawHistory_is_paged_newest_first()
    {
        SeedDraws(200);

        var page = await _drawService.GetDrawHistoryAsync(offset: 10, limit: 5);

        Assert.Equal(200, page.Total);
        Assert.Equal(10, page.Offset);
        Assert.Equal(5, page.Limit);
        Assert.Equal([190, 189, 188, 187, 186], page.Items.Select(draw => draw.Sequence));
    }

    [Fact]
    public async Task DrawHistory_loads_100_by_default_and_all_only_when_requested()
    {
        SeedDraws(200);

        var initial = await _drawService.GetDrawHistoryAsync();
        var all = await _drawService.GetDrawHistoryAsync(loadAll: true);

        Assert.Equal(100, initial.Items.Count);
        Assert.Equal(200, initial.Total);
        Assert.Equal(200, all.Items.Count);
        Assert.Equal(200, all.Limit);
    }

    [Fact]
    public async Task AddLatestRound_completes_latest_draw_group_only_once()
    {
        SeedDraws(200);
        var first = await _drawService.AddDrawAsync(new AddDrawRequest([1, 2, 3, 4, 5, 6]));

        var second = await _drawService.AddLatestRoundAsync(
            new AddDrawRequest([7, 8, 9, 10, 11, 12]));

        Assert.Equal(first.DrawNumber, second.DrawNumber);
        Assert.Equal(first.Date, second.Date);
        Assert.Equal(first.Sequence + 1, second.Sequence);
        Assert.Equal("Manual Round 2", second.Machine);
        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            _drawService.AddLatestRoundAsync(new AddDrawRequest([13, 14, 15, 16, 17, 18])));
        Assert.Equal(202, await _drawService.CountAsync());
    }

    [Fact]
    public async Task AddDraw_recomputes_statistics_automatically()
    {
        SeedDraws(200);
        var before = await _analysis.GetSnapshotAsync();
        await _drawService.AddDrawAsync(new AddDrawRequest([1, 2, 3, 4, 5, 6]));
        var after = await _analysis.GetSnapshotAsync();

        Assert.Equal(200, before.Draws.Count);
        Assert.Equal(201, after.Draws.Count);
        Assert.Equal(before.Features.For(1).TotalCount + 1, after.Features.For(1).TotalCount);
    }

    [Fact]
    public async Task Generate_stores_prediction_with_cutoff_and_explanation()
    {
        SeedDraws(200);
        var prediction = await _predictionService.GenerateAsync();

        Assert.Equal(6, prediction.Numbers.Distinct().Count());
        Assert.Equal(200, prediction.CutoffSequence);
        Assert.NotNull(prediction.Explanation);
        Assert.Equal(6, prediction.Explanation!.Count);
        Assert.StartsWith("v3/", prediction.ModelVersion);
        Assert.Null(prediction.Matches);

        var latest = await _predictionService.GetLatestAsync();
        Assert.Equal(prediction.Numbers, latest!.Numbers);
        Assert.NotNull(latest.Explanation);
    }

    [Fact]
    public async Task Adding_result_evaluates_outstanding_prediction()
    {
        SeedDraws(200);
        var prediction = await _predictionService.GenerateAsync();

        // Enter an actual result that shares exactly three numbers with the prediction.
        var predicted = prediction.Numbers;
        var others = Enumerable.Range(1, 59).Except(predicted).Take(3).ToArray();
        var actual = predicted.Take(3).Concat(others).ToArray();
        await _drawService.AddDrawAsync(new AddDrawRequest(actual));

        var evaluated = (await _predictionService.GetHistoryAsync()).Single(p => p.Id == prediction.Id);
        Assert.Equal(3, evaluated.Matches);
        Assert.Equal(actual.OrderBy(x => x).ToArray(), evaluated.ActualNumbers);
    }

    [Fact]
    public async Task Adding_two_rounds_evaluates_pending_prediction_against_first_round()
    {
        SeedDraws(200);
        var prediction = await _predictionService.GenerateAsync();
        var predicted = prediction.Numbers;
        var nonPredicted = Enumerable.Range(1, 59).Except(predicted).ToArray();
        var firstRound = predicted.Take(2).Concat(nonPredicted.Take(4)).ToArray();
        var secondRound = predicted.Take(5).Concat(nonPredicted.Skip(4).Take(1)).ToArray();

        await _drawService.AddDrawRoundsAsync(new AddDrawRoundsRequest([firstRound, secondRound]));

        var evaluated = (await _predictionService.GetHistoryAsync()).Single(p => p.Id == prediction.Id);
        Assert.Equal(2, evaluated.Matches);
        Assert.Equal(firstRound.OrderBy(number => number), evaluated.ActualNumbers);
        Assert.Equal(202, (await _analysis.GetSnapshotAsync()).Draws.Count);
    }

    [Fact]
    public async Task Correcting_draw_recalculates_prediction_evaluated_against_it()
    {
        SeedDraws(200);
        var prediction = await _predictionService.GenerateAsync();
        var nonPredicted = Enumerable.Range(1, 59).Except(prediction.Numbers).ToArray();
        var original = prediction.Numbers.Take(1).Concat(nonPredicted.Take(5)).ToArray();
        var added = await _drawService.AddDrawAsync(new AddDrawRequest(original));

        var corrected = prediction.Numbers.Take(4).Concat(nonPredicted.Skip(5).Take(2)).ToArray();
        await _drawService.UpdateDrawAsync(added.Id, new UpdateDrawRequest(corrected));

        var evaluated = (await _predictionService.GetHistoryAsync()).Single(item => item.Id == prediction.Id);
        Assert.Equal(4, evaluated.Matches);
        Assert.Equal(corrected.OrderBy(number => number), evaluated.ActualNumbers);
        Assert.Equal(201, (await _analysis.GetSnapshotAsync()).Draws.Count);
    }

    [Fact]
    public async Task Prediction_made_after_result_is_not_evaluated_against_it()
    {
        SeedDraws(200);
        await _drawService.AddDrawAsync(new AddDrawRequest([1, 2, 3, 4, 5, 6]));
        var prediction = await _predictionService.GenerateAsync();

        Assert.Null(prediction.Matches);
        Assert.Equal(201, prediction.CutoffSequence);
    }

    [Fact]
    public async Task Explanation_reflects_data_at_cutoff_not_later_draws()
    {
        SeedDraws(200);
        var prediction = await _predictionService.GenerateAsync();
        var explanationBefore = (await _predictionService.GetLatestAsync())!.Explanation!;

        // Add a draw containing the first predicted number; the stored prediction's
        // explanation must still reflect the original cutoff.
        int first = prediction.Numbers[0];
        var filler = Enumerable.Range(1, 59).Where(n => n != first).Take(5);
        await _drawService.AddDrawAsync(new AddDrawRequest(filler.Append(first).ToArray()));

        var explanationAfter = (await _predictionService.GetLatestAsync())!.Explanation!;
        Assert.Equal(
            explanationBefore.Select(e => e.OverallFrequency),
            explanationAfter.Select(e => e.OverallFrequency));
    }
}
