using LottoPredictor.Core.Analysis;
using LottoPredictor.Core.Data;
using LottoPredictor.Core.Dtos;
using LottoPredictor.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LottoPredictor.Core.Services;

public class ValidationFailedException(IReadOnlyList<string> errors)
    : Exception(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public interface IDrawService
{
    Task<IReadOnlyList<DrawDto>> GetDrawsAsync(int limit = 50, CancellationToken ct = default);
    Task<IReadOnlyList<DrawDto>> GetDrawRoundsAsync(int drawNumber, CancellationToken ct = default);
    Task<IReadOnlyList<DrawDto>> GetDrawsOnDateAsync(DateOnly date, CancellationToken ct = default);
    Task<DrawHistoryDto> GetDrawHistoryAsync(
        int offset = 0, int limit = 100, bool loadAll = false, CancellationToken ct = default);
    Task<DrawDto?> GetLatestAsync(CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
    /// <summary>Validates and stores a new result, evaluates any outstanding predictions against it,
    /// and invalidates the analysis cache so statistics and backtests are recomputed.</summary>
    Task<DrawDto> AddDrawAsync(AddDrawRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<DrawDto>> AddDrawRoundsAsync(AddDrawRoundsRequest request, CancellationToken ct = default);
    Task<DrawDto> AddLatestRoundAsync(AddDrawRequest request, CancellationToken ct = default);
    Task<DrawDto> UpdateDrawAsync(int id, UpdateDrawRequest request, CancellationToken ct = default);
}

public class DrawService(IDbContextFactory<LottoDbContext> contextFactory, IAnalysisService analysis)
    : IDrawService
{
    public async Task<IReadOnlyList<DrawDto>> GetDrawsAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var draws = await db.Draws.AsNoTracking()
            .OrderByDescending(d => d.Sequence)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);
        return draws.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<DrawDto>> GetDrawRoundsAsync(
        int drawNumber, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rounds = await db.Draws.AsNoTracking()
            .Where(draw => draw.DrawNumber == drawNumber)
            .OrderBy(draw => draw.Sequence)
            .ToListAsync(ct);
        return rounds.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<DrawDto>> GetDrawsOnDateAsync(
        DateOnly date, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var draws = await db.Draws.AsNoTracking()
            .Where(draw => draw.Date == date)
            .OrderBy(draw => draw.Sequence)
            .ToListAsync(ct);
        return draws.Select(ToDto).ToList();
    }

    public async Task<DrawHistoryDto> GetDrawHistoryAsync(
        int offset = 0, int limit = 100, bool loadAll = false, CancellationToken ct = default)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 200);

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        int total = await db.Draws.CountAsync(ct);
        var query = db.Draws.AsNoTracking().OrderByDescending(draw => draw.Sequence);
        var items = loadAll
            ? await query.ToListAsync(ct)
            : await query.Skip(offset).Take(limit).ToListAsync(ct);

        return new DrawHistoryDto(
            items.Select(ToDto).ToList(), total, loadAll ? 0 : offset, loadAll ? total : limit);
    }

    public async Task<DrawDto?> GetLatestAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var draw = await db.Draws.AsNoTracking()
            .OrderByDescending(d => d.Sequence)
            .FirstOrDefaultAsync(ct);
        return draw is null ? null : ToDto(draw);
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Draws.CountAsync(ct);
    }

    public async Task<DrawDto> AddDrawAsync(AddDrawRequest request, CancellationToken ct = default)
    {
        var added = await AddRoundsAsync([request.Numbers], [request.Bonus], ct);
        return added[0];
    }

    public async Task<IReadOnlyList<DrawDto>> AddDrawRoundsAsync(
        AddDrawRoundsRequest request, CancellationToken ct = default)
    {
        if (request.Rounds is not { Length: 2 })
            throw new ValidationFailedException(["Exactly two rounds of six numbers are required."]);
        if (request.Bonuses is { Length: not 2 })
            throw new ValidationFailedException(["Provide one bonus value per round."]);

        return await AddRoundsAsync(request.Rounds, request.Bonuses ?? [null, null], ct);
    }

    public async Task<DrawDto> AddLatestRoundAsync(
        AddDrawRequest request, CancellationToken ct = default)
    {
        var snapshot = await analysis.GetSnapshotAsync(ct);
        var errors = ValidateResult(request.Numbers, request.Bonus, snapshot.PoolSize);
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var latest = await db.Draws.OrderByDescending(draw => draw.Sequence).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("No existing draw is available.");
        int existingRounds = await db.Draws.CountAsync(draw => draw.DrawNumber == latest.DrawNumber, ct);
        if (existingRounds >= 2)
            throw new ValidationFailedException(["The latest draw already has two rounds."]);

        var draw = new Draw
        {
            Sequence = latest.Sequence + 1,
            DrawNumber = latest.DrawNumber,
            Date = latest.Date,
            Machine = $"Manual Round {existingRounds + 1}",
            BallSet = latest.BallSet,
            Source = "manual",
        };
        draw.SetNumbers(request.Numbers);
        draw.Bonus = request.Bonus;
        db.Draws.Add(draw);

        var outstanding = await db.Predictions
            .Where(prediction => prediction.Matches == null && prediction.CutoffSequence < draw.Sequence)
            .ToListAsync(ct);
        EvaluatePredictions(outstanding, draw);

        await db.SaveChangesAsync(ct);
        foreach (var prediction in outstanding) prediction.EvaluatedDrawId = draw.Id;
        if (outstanding.Count > 0) await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        analysis.Invalidate();
        return ToDto(draw);
    }

    public async Task<DrawDto> UpdateDrawAsync(
        int id, UpdateDrawRequest request, CancellationToken ct = default)
    {
        var snapshot = await analysis.GetSnapshotAsync(ct);
        var errors = ValidateResult(request.Numbers, request.Bonus, snapshot.PoolSize);
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var draw = await db.Draws.FirstOrDefaultAsync(item => item.Id == id, ct)
            ?? throw new KeyNotFoundException($"Draw {id} was not found.");
        draw.SetNumbers(request.Numbers);
        draw.Bonus = request.Bonus;

        var evaluatedPredictions = await db.Predictions
            .Where(prediction => prediction.EvaluatedDrawId == draw.Id)
            .ToListAsync(ct);
        EvaluatePredictions(evaluatedPredictions, draw);

        await db.SaveChangesAsync(ct);
        analysis.Invalidate();
        return ToDto(draw);
    }

    private async Task<IReadOnlyList<DrawDto>> AddRoundsAsync(
        IReadOnlyList<int[]> rounds, IReadOnlyList<int?> bonuses, CancellationToken ct)
    {
        var snapshot = await analysis.GetSnapshotAsync(ct);
        var errors = rounds
            .SelectMany((numbers, index) => ValidateResult(numbers, bonuses[index], snapshot.PoolSize)
                .Select(error => rounds.Count > 1 ? $"Round {index + 1}: {error}" : error))
            .ToList();
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        int maxSequence = await db.Draws.MaxAsync(d => (int?)d.Sequence, ct) ?? 0;
        int maxDrawNumber = await db.Draws.MaxAsync(d => (int?)d.DrawNumber, ct) ?? 0;

        var drawDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var added = rounds.Select((numbers, index) =>
        {
            var draw = new Draw
            {
                Sequence = maxSequence + index + 1,
                DrawNumber = maxDrawNumber + 1,
                Date = drawDate,
                Machine = $"Manual Round {index + 1}",
                BallSet = "",
                Source = "manual",
                Bonus = bonuses[index],
            };
            draw.SetNumbers(numbers);
            return draw;
        }).ToList();
        db.Draws.AddRange(added);

        // A pending prediction targets the first chronological round after its cutoff.
        var firstDraw = added[0];
        var outstanding = await db.Predictions
            .Where(p => p.Matches == null && p.CutoffSequence < firstDraw.Sequence)
            .ToListAsync(ct);
        EvaluatePredictions(outstanding, firstDraw);

        await db.SaveChangesAsync(ct);

        foreach (var prediction in outstanding)
        {
            prediction.EvaluatedDrawId = firstDraw.Id;
        }
        if (outstanding.Count > 0) await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        analysis.Invalidate();
        return added.Select(ToDto).ToList();
    }

    private static IReadOnlyList<string> ValidateResult(int[] numbers, int? bonus, int poolSize)
    {
        var errors = NumberValidator.Validate(numbers, poolSize).ToList();
        if (bonus is < 1 || bonus > poolSize)
            errors.Add($"Bonus ball must be between 1 and {poolSize}.");
        else if (bonus.HasValue && numbers.Contains(bonus.Value))
            errors.Add("Bonus ball must be different from the six main numbers.");
        return errors;
    }

    private static void EvaluatePredictions(IEnumerable<Prediction> predictions, Draw draw)
    {
        var actual = draw.Numbers();
        foreach (var prediction in predictions)
        {
            prediction.Matches = Backtester.CountMatches(prediction.Numbers(), actual);
            prediction.ActualNumbersCsv = string.Join(",", actual);
            prediction.EvaluatedUtc = DateTime.UtcNow;
        }
    }

    internal static DrawDto ToDto(Draw d) => new(
        d.Id, d.Sequence, d.DrawNumber, d.Date.ToString("yyyy-MM-dd"),
        d.Numbers(), d.Bonus, d.Machine, d.BallSet, d.Source);
}
