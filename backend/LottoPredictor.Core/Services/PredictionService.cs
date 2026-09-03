using LottoPredictor.Core.Analysis;
using LottoPredictor.Core.Data;
using LottoPredictor.Core.Dtos;
using LottoPredictor.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LottoPredictor.Core.Services;

public interface IPredictionService
{
    Task<PredictionDto> GenerateAsync(CancellationToken ct = default);
    Task<PredictionDto?> GetLatestAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PredictionDto>> GetHistoryAsync(int limit = 100, CancellationToken ct = default);
}

public class PredictionService(IDbContextFactory<LottoDbContext> contextFactory, IAnalysisService analysis)
    : IPredictionService
{
    public async Task<PredictionDto> GenerateAsync(CancellationToken ct = default)
    {
        var snapshot = await analysis.GetSnapshotAsync(ct);
        var strategy = snapshot.ActiveStrategy;
        var result = strategy.Name == Backtester.EnsembleName
            ? PredictionEngine.GenerateEnsemble(
                snapshot.Features, snapshot.AllStrategies, snapshot.HedgeWeights, Backtester.EnsembleName)
            : PredictionEngine.Generate(snapshot.Features, strategy);
        var lastDraw = snapshot.Draws[^1];

        var prediction = new Prediction
        {
            CreatedUtc = DateTime.UtcNow,
            CutoffSequence = lastDraw.Sequence,
            CutoffDrawNumber = lastDraw.DrawNumber,
            ModelVersion = strategy.Version,
            StrategyName = strategy.Name,
        };
        prediction.SetNumbers(result.Numbers);

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        db.Predictions.Add(prediction);
        await db.SaveChangesAsync(ct);

        var explanation = result.SelectedNumbers
            .Select(sn => ToExplanation(sn.Features, sn.Score))
            .ToList();
        return ToDto(prediction, explanation);
    }

    public async Task<PredictionDto?> GetLatestAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var prediction = await db.Predictions.AsNoTracking()
            .OrderByDescending(p => p.Id)
            .FirstOrDefaultAsync(ct);
        if (prediction is null) return null;
        return ToDto(prediction, await BuildExplanationAsync(prediction, ct));
    }

    public async Task<IReadOnlyList<PredictionDto>> GetHistoryAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var predictions = await db.Predictions.AsNoTracking()
            .OrderByDescending(p => p.Id)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);
        return predictions.Select(p => ToDto(p, null)).ToList();
    }

    /// <summary>Rebuilds the explanation from the exact prefix of draws the prediction used
    /// (everything up to its cutoff sequence), so the numbers shown match what the model saw.</summary>
    private async Task<IReadOnlyList<NumberExplanationDto>?> BuildExplanationAsync(
        Prediction prediction, CancellationToken ct)
    {
        var snapshot = await analysis.GetSnapshotAsync(ct);
        var prefix = snapshot.Draws.Where(d => d.Sequence <= prediction.CutoffSequence).ToList();
        if (prefix.Count == 0) return null;

        var fs = prefix.Count == snapshot.Draws.Count
            ? snapshot.Features
            : FeatureCalculator.Compute(prefix);

        Dictionary<int, double> scores;
        if (prediction.StrategyName == Backtester.EnsembleName)
        {
            scores = PredictionEngine.BlendScores(snapshot.AllStrategies.Select(s => (
                PredictionEngine.ScoreNumbers(fs, s),
                snapshot.HedgeWeights.GetValueOrDefault(s.Name))));
        }
        else
        {
            var strategy = snapshot.AllStrategies.FirstOrDefault(s => s.Name == prediction.StrategyName)
                ?? snapshot.ActiveStrategy;
            scores = PredictionEngine.ScoreNumbers(fs, strategy);
        }

        return prediction.Numbers()
            .Select(n => ToExplanation(fs.For(n), scores.GetValueOrDefault(n)))
            .ToList();
    }

    private static NumberExplanationDto ToExplanation(NumberFeatures f, double score) => new(
        f.Number, f.TotalCount, f.FreqRate, f.Count10, f.Count25, f.Count50, f.Count100,
        f.DrawsSinceLast, Math.Round(f.AvgGap, 2), Math.Round(f.GapRatio, 3), Math.Round(score, 4));

    private static PredictionDto ToDto(Prediction p, IReadOnlyList<NumberExplanationDto>? explanation) => new(
        p.Id, p.CreatedUtc, p.Numbers(), p.CutoffSequence, p.CutoffDrawNumber,
        p.ModelVersion, p.StrategyName,
        p.ActualNumbersCsv?.Split(',').Select(int.Parse).ToArray(),
        p.Matches, explanation);
}
