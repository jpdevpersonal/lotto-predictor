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
        int offset = 0, int limit = 100, CancellationToken ct = default);
    Task<DrawDto?> GetLatestAsync(CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
    /// <summary>Validates and stores a new result, evaluates any outstanding predictions against it,
    /// and invalidates the analysis cache so statistics and backtests are recomputed.</summary>
    Task<DrawDto> AddDrawAsync(AddDrawRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<DrawDto>> AddDrawRoundsAsync(AddDrawRoundsRequest request, CancellationToken ct = default);
    Task<DrawDto> AddLatestRoundAsync(AddDrawRequest request, CancellationToken ct = default);
    Task<DrawDto> UpdateDrawAsync(int id, UpdateDrawRequest request, CancellationToken ct = default);
}

public class DrawService : IDrawService
{
    private readonly IDbContextFactory<LottoDbContext> contextFactory;
    private readonly IAnalysisService analysis;
    private readonly ILotterySelection lotterySelection;

    public DrawService(
        IDbContextFactory<LottoDbContext> contextFactory,
        IAnalysisService analysis,
        ILotterySelection? lotterySelection = null)
    {
        this.contextFactory = contextFactory;
        this.analysis = analysis;
        this.lotterySelection = lotterySelection ?? new DefaultLotterySelection();
    }

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
        int offset = 0, int limit = 100, CancellationToken ct = default)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 200);

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        int total = await db.Draws.CountAsync(ct);
        var query = db.Draws.AsNoTracking().OrderByDescending(draw => draw.Sequence);
        var items = await query.Skip(offset).Take(limit).ToListAsync(ct);

        return new DrawHistoryDto(
            items.Select(ToDto).ToList(), total, offset, limit);
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
        var added = await AddRoundsAsync(
            [request.Numbers], [request.Bonus], [request.LuckyStars ?? []], request.DrawNumber, request.Date, ct);
        return added[0];
    }

    public async Task<IReadOnlyList<DrawDto>> AddDrawRoundsAsync(
        AddDrawRoundsRequest request, CancellationToken ct = default)
    {
        var lottery = lotterySelection.Current;
        if (request.Rounds.Length != lottery.RoundCount)
            throw new ValidationFailedException(
                [$"Exactly {lottery.RoundCount} round(s) of {lottery.MainNumberCount} numbers are required."]);
        if (request.Bonuses is not null && request.Bonuses.Length != lottery.RoundCount)
            throw new ValidationFailedException(["Provide one bonus value per round."]);
        if (request.LuckyStars is not null && request.LuckyStars.Length != lottery.RoundCount)
            throw new ValidationFailedException(["Provide one Lucky Star set per round."]);

        return await AddRoundsAsync(
            request.Rounds,
            request.Bonuses ?? Enumerable.Repeat<int?>(null, lottery.RoundCount).ToArray(),
            request.LuckyStars ?? Enumerable.Range(0, lottery.RoundCount).Select(_ => Array.Empty<int>()).ToArray(),
            request.DrawNumber,
            request.Date,
            ct);
    }

    public async Task<DrawDto> AddLatestRoundAsync(
        AddDrawRequest request, CancellationToken ct = default)
    {
        var lottery = lotterySelection.Current;
        if (lottery.RoundCount == 1)
            throw new ValidationFailedException([$"{lottery.Name} has one round per draw."]);

        var errors = ValidateResult(request.Numbers, request.Bonus, request.LuckyStars, lottery);
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var latest = await db.Draws.OrderByDescending(draw => draw.Sequence).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("No existing draw is available.");
        int existingRounds = await db.Draws.CountAsync(draw => draw.DrawNumber == latest.DrawNumber, ct);
        if (existingRounds >= lottery.RoundCount)
            throw new ValidationFailedException([$"The latest draw already has {lottery.RoundCount} rounds."]);

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
        SetLuckyStars(draw, request.LuckyStars);
        db.Draws.Add(draw);

        await db.SaveChangesAsync(ct);

        var outstanding = await db.Predictions
            .Include(prediction => prediction.Evaluations)
            .Where(prediction =>
                (prediction.Matches == null && prediction.CutoffSequence < draw.Sequence) ||
                prediction.Evaluations.Any(evaluation =>
                    evaluation.EvaluatedDraw.DrawNumber == draw.DrawNumber))
            .ToListAsync(ct);
        AddPredictionEvaluations(db, outstanding, draw, lottery);

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        analysis.Invalidate();
        return ToDto(draw);
    }

    public async Task<DrawDto> UpdateDrawAsync(
        int id, UpdateDrawRequest request, CancellationToken ct = default)
    {
        var lottery = lotterySelection.Current;
        var errors = ValidateResult(request.Numbers, request.Bonus, request.LuckyStars, lottery);
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var draw = await db.Draws.FirstOrDefaultAsync(item => item.Id == id, ct)
            ?? throw new KeyNotFoundException($"Draw {id} was not found.");
        draw.SetNumbers(request.Numbers);
        draw.Bonus = request.Bonus;
        if (lottery.BonusSharesMainPool)
            draw.Bonus2 = null;
        SetLuckyStars(draw, request.LuckyStars);
        if (request.DrawNumber.HasValue)
            draw.DrawNumber = request.DrawNumber.Value;
        if (request.Date is not null)
        {
            var updatedDate = ParseDate(request.Date);
            await EnsureDateFitsSequenceAsync(db, draw.Sequence, updatedDate, ct);
            draw.Date = updatedDate;
        }

        var evaluatedPredictions = await db.Predictions
            .Include(prediction => prediction.Evaluations)
            .Where(prediction => prediction.Evaluations.Any(evaluation => evaluation.EvaluatedDrawId == draw.Id))
            .ToListAsync(ct);
        UpdatePredictionEvaluations(evaluatedPredictions, draw, lottery);

        await db.SaveChangesAsync(ct);
        analysis.Invalidate();
        return ToDto(draw);
    }

    private async Task<IReadOnlyList<DrawDto>> AddRoundsAsync(
        IReadOnlyList<int[]> rounds,
        IReadOnlyList<int?> bonuses,
        IReadOnlyList<int[]> luckyStars,
        int? drawNumber,
        string? date,
        CancellationToken ct)
    {
        var lottery = lotterySelection.Current;
        var errors = rounds
            .SelectMany((numbers, index) => ValidateResult(numbers, bonuses[index], luckyStars[index], lottery)
                .Select(error => rounds.Count > 1 ? $"Round {index + 1}: {error}" : error))
            .ToList();
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        int maxSequence = await db.Draws.MaxAsync(d => (int?)d.Sequence, ct) ?? 0;
        int maxDrawNumber = await db.Draws.MaxAsync(d => (int?)d.DrawNumber, ct) ?? 0;

        var drawDate = date is not null ? ParseDate(date) : DateOnly.FromDateTime(DateTime.UtcNow);
        var latestDate = await db.Draws
            .OrderByDescending(draw => draw.Sequence)
            .Select(draw => (DateOnly?)draw.Date)
            .FirstOrDefaultAsync(ct);
        if (latestDate is DateOnly last && drawDate < last)
            throw new ValidationFailedException([
                $"Draw date {drawDate:yyyy-MM-dd} is before the latest recorded draw date {last:yyyy-MM-dd}. " +
                "Backdated results must be imported in chronological order."]);
        var resolvedDrawNumber = drawNumber ?? maxDrawNumber + 1;
        var added = rounds.Select((numbers, index) =>
        {
            var draw = new Draw
            {
                Sequence = maxSequence + index + 1,
                DrawNumber = resolvedDrawNumber,
                Date = drawDate,
                Machine = $"Manual Round {index + 1}",
                BallSet = "",
                Source = "manual",
                Bonus = bonuses[index],
            };
            draw.SetNumbers(numbers);
            SetLuckyStars(draw, luckyStars[index]);
            return draw;
        }).ToList();
        db.Draws.AddRange(added);

        await db.SaveChangesAsync(ct);

        var firstDraw = added[0];
        var outstanding = await db.Predictions
            .Include(prediction => prediction.Evaluations)
            .Where(prediction => prediction.Matches == null && prediction.CutoffSequence < firstDraw.Sequence)
            .ToListAsync(ct);
        foreach (var draw in added)
        {
            AddPredictionEvaluations(db, outstanding, draw, lottery);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        analysis.Invalidate();
        return added.Select(ToDto).ToList();
    }

    private static DateOnly ParseDate(string date)
    {
        if (!DateOnly.TryParse(date, out var parsed))
            throw new ValidationFailedException([$"'{date}' is not a valid date."]);
        return parsed;
    }

    private static async Task EnsureDateFitsSequenceAsync(
        LottoDbContext db, int sequence, DateOnly date, CancellationToken ct)
    {
        var previousDate = await db.Draws
            .Where(item => item.Sequence < sequence)
            .OrderByDescending(item => item.Sequence)
            .Select(item => (DateOnly?)item.Date)
            .FirstOrDefaultAsync(ct);
        var nextDate = await db.Draws
            .Where(item => item.Sequence > sequence)
            .OrderBy(item => item.Sequence)
            .Select(item => (DateOnly?)item.Date)
            .FirstOrDefaultAsync(ct);

        if (previousDate is DateOnly previous && date < previous ||
            nextDate is DateOnly next && date > next)
            throw new ValidationFailedException([
                "A corrected draw date must remain between the dates of its chronological neighbours."]);
    }

    private static IReadOnlyList<string> ValidateResult(
        int[] numbers, int? bonus, int[]? luckyStars, LotteryProfile lottery)
    {
        var errors = NumberValidator.Validate(
            numbers, lottery.MainPoolSize, lottery.MainNumberCount).ToList();
        if (lottery.BonusSharesMainPool)
        {
            if (bonus is < 1 || bonus > lottery.BonusPoolSize)
                errors.Add($"Bonus ball must be between 1 and {lottery.BonusPoolSize}.");
            else if (bonus.HasValue && numbers.Contains(bonus.Value))
                errors.Add("Bonus ball must be different from the main numbers.");
        }
        else
        {
            var stars = luckyStars ?? [];
            if (stars.Length != lottery.BonusNumberCount)
                errors.Add($"Exactly {lottery.BonusNumberCount} Lucky Stars are required.");
            else if (stars.Distinct().Count() != stars.Length)
                errors.Add("Lucky Stars must be distinct.");
            else if (stars.Any(star => star < 1 || star > lottery.BonusPoolSize))
                errors.Add($"Lucky Stars must be between 1 and {lottery.BonusPoolSize}.");
        }
        return errors;
    }

    private static void SetLuckyStars(Draw draw, int[]? luckyStars)
    {
        if (luckyStars is not { Length: > 0 }) return;
        var sorted = luckyStars.OrderBy(number => number).ToArray();
        draw.Bonus = sorted[0];
        draw.Bonus2 = sorted.Length > 1 ? sorted[1] : null;
    }

    private static void AddPredictionEvaluations(
        LottoDbContext db,
        IEnumerable<Prediction> predictions,
        Draw draw,
        LotteryProfile lottery)
    {
        foreach (var prediction in predictions)
        {
            if (prediction.Evaluations.Any(evaluation => evaluation.EvaluatedDrawId == draw.Id)) continue;

            var evaluation = BuildPredictionEvaluation(prediction, draw, lottery);
            prediction.Evaluations.Add(evaluation);
            db.PredictionEvaluations.Add(evaluation);

            if (prediction.Matches is null)
                CopyToLegacyFields(prediction, evaluation);
        }
    }

    private static void UpdatePredictionEvaluations(
        IEnumerable<Prediction> predictions,
        Draw draw,
        LotteryProfile lottery)
    {
        foreach (var prediction in predictions)
        {
            var existing = prediction.Evaluations.Single(evaluation => evaluation.EvaluatedDrawId == draw.Id);
            var updated = BuildPredictionEvaluation(prediction, draw, lottery);
            existing.ActualNumbersCsv = updated.ActualNumbersCsv;
            existing.Matches = updated.Matches;
            existing.BonusMatches = updated.BonusMatches;
            existing.ActualLuckyStarsCsv = updated.ActualLuckyStarsCsv;
            existing.LuckyStarMatches = updated.LuckyStarMatches;
            existing.EvaluatedUtc = updated.EvaluatedUtc;

            if (prediction.EvaluatedDrawId == draw.Id)
                CopyToLegacyFields(prediction, existing);
        }
    }

    private static PredictionEvaluation BuildPredictionEvaluation(
        Prediction prediction,
        Draw draw,
        LotteryProfile lottery)
    {
        var predicted = prediction.Numbers();
        var actual = draw.Numbers();
        var actualBonusNumbers = draw.BonusNumbers();
        var isUkLotto = lottery.BonusSharesMainPool;
        return new PredictionEvaluation
        {
            PredictionId = prediction.Id,
            EvaluatedDrawId = draw.Id,
            EvaluatedDraw = draw,
            ActualNumbersCsv = string.Join(",", actual),
            Matches = Backtester.CountMatches(predicted, actual),
            BonusMatches = isUkLotto
                ? actualBonusNumbers.Count(predicted.Contains)
                : null,
            ActualLuckyStarsCsv = isUkLotto ? null : string.Join(",", actualBonusNumbers),
            LuckyStarMatches = isUkLotto
                ? null
                : Backtester.CountMatches(prediction.LuckyStars(), actualBonusNumbers),
            EvaluatedUtc = DateTime.UtcNow,
        };
    }

    private static void CopyToLegacyFields(Prediction prediction, PredictionEvaluation evaluation)
    {
        prediction.ActualNumbersCsv = evaluation.ActualNumbersCsv;
        prediction.Matches = evaluation.Matches;
        prediction.ActualLuckyStarsCsv = evaluation.ActualLuckyStarsCsv;
        prediction.LuckyStarMatches = evaluation.LuckyStarMatches;
        prediction.EvaluatedDrawId = evaluation.EvaluatedDrawId;
        prediction.EvaluatedUtc = evaluation.EvaluatedUtc;
    }

    internal static DrawDto ToDto(Draw d) => new(
        d.Id, d.Sequence, d.DrawNumber, d.Date.ToString("yyyy-MM-dd"),
        d.Numbers(), d.Bonus, d.BonusNumbers(), d.Machine, d.BallSet, d.Source);
}
