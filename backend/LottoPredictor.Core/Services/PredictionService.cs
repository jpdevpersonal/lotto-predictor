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
    /// <summary>Top-N candidate lines, computed on demand and never persisted.</summary>
    Task<PredictionLinesDto> GenerateLinesAsync(int count = 50, CancellationToken ct = default);
    /// <summary>Consensus line over the same top-N lines (screen-only).</summary>
    Task<BestOfLinesDto> GenerateBestOfLinesAsync(int count = 50, CancellationToken ct = default);
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
        var luckyStars = snapshot.LuckyStarFeatures is null
            ? null
            : GenerateForFeatures(snapshot, snapshot.LuckyStarFeatures).Numbers;
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
        prediction.LuckyStarsCsv = luckyStars is null ? null : string.Join(",", luckyStars);

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        db.Predictions.Add(prediction);
        await db.SaveChangesAsync(ct);

        var explanation = result.SelectedNumbers
            .Select(sn => ToExplanation(sn.Features, sn.Score))
            .ToList();
        return ToDto(prediction, explanation);
    }

    public async Task<PredictionLinesDto> GenerateLinesAsync(int count = 50, CancellationToken ct = default)
    {
        count = Math.Clamp(count, 1, 200);
        var snapshot = await analysis.GetSnapshotAsync(ct);
        var (scores, strategy) = ActiveScores(snapshot);
        var lines = PredictionEngine.GenerateTopLines(snapshot.Features, scores, strategy, count);
        var starLines = GenerateLuckyStarLines(snapshot, count);
        return new PredictionLinesDto(
            snapshot.ActiveStrategy.Name,
            snapshot.Draws[^1].DrawNumber,
            lines.Select((line, index) => new PredictionLineDto(
                index + 1,
                line.Numbers,
                starLines.Count > 0 ? starLines[index % starLines.Count].Numbers : [],
                Math.Round(line.SetScore, 4))).ToList());
    }

    public async Task<BestOfLinesDto> GenerateBestOfLinesAsync(int count = 50, CancellationToken ct = default)
    {
        count = Math.Clamp(count, 1, 200);
        var snapshot = await analysis.GetSnapshotAsync(ct);
        var (scores, strategy) = ActiveScores(snapshot);
        var lines = PredictionEngine.GenerateTopLines(snapshot.Features, scores, strategy, count);
        var (numbers, frequencies) = PredictionEngine.Consensus(lines, scores);
        var starLines = GenerateLuckyStarLines(snapshot, count);
        var starScores = snapshot.LuckyStarFeatures is null
            ? []
            : PredictionEngine.ScoreNumbers(snapshot.LuckyStarFeatures, strategy);
        var (stars, starFrequencies) = starLines.Count > 0
            ? PredictionEngine.Consensus(starLines, starScores)
            : (Array.Empty<int>(), Array.Empty<int>());
        return new BestOfLinesDto(
            numbers, frequencies, stars, starFrequencies, lines.Count,
            snapshot.ActiveStrategy.Name, snapshot.Draws[^1].DrawNumber);
    }

    private static IReadOnlyList<PredictionResult> GenerateLuckyStarLines(
        AnalysisSnapshot snapshot, int count)
    {
        if (snapshot.LuckyStarFeatures is null) return [];
        var (scores, lineStrategy) = ActiveScores(snapshot, snapshot.LuckyStarFeatures);
        return PredictionEngine.GenerateTopLines(
            snapshot.LuckyStarFeatures, scores, lineStrategy, count);
    }

    private static PredictionResult GenerateForFeatures(
        AnalysisSnapshot snapshot, FeatureSet features)
    {
        var strategy = snapshot.ActiveStrategy;
        return strategy.Name == Backtester.EnsembleName
            ? PredictionEngine.GenerateEnsemble(
                features, snapshot.AllStrategies, snapshot.HedgeWeights, Backtester.EnsembleName)
            : PredictionEngine.Generate(features, strategy);
    }

    /// <summary>Score map and combination weights for the currently active strategy,
    /// handling the hedge ensemble the same way as single prediction generation.</summary>
    private static (Dictionary<int, double> Scores, ScoringStrategy Strategy) ActiveScores(
        AnalysisSnapshot snapshot) => ActiveScores(snapshot, snapshot.Features);

    private static (Dictionary<int, double> Scores, ScoringStrategy Strategy) ActiveScores(
        AnalysisSnapshot snapshot, FeatureSet features)
    {
        var strategy = snapshot.ActiveStrategy;
        if (strategy.Name != Backtester.EnsembleName)
            return (PredictionEngine.ScoreNumbers(features, strategy), strategy);

        double total = snapshot.AllStrategies.Sum(s => snapshot.HedgeWeights.GetValueOrDefault(s.Name));
        var parts = snapshot.AllStrategies
            .Select(s => (PredictionEngine.ScoreNumbers(features, s),
                total > 0 ? snapshot.HedgeWeights.GetValueOrDefault(s.Name) / total : 1.0 / snapshot.AllStrategies.Count))
            .ToList();
        var blended = PredictionEngine.BlendScores(parts);
        double pairW = snapshot.AllStrategies.Sum(s =>
            (total > 0 ? snapshot.HedgeWeights.GetValueOrDefault(s.Name) / total : 1.0 / snapshot.AllStrategies.Count) * s.PairWeight);
        double penW = snapshot.AllStrategies.Sum(s =>
            (total > 0 ? snapshot.HedgeWeights.GetValueOrDefault(s.Name) / total : 1.0 / snapshot.AllStrategies.Count) * s.PenaltyWeight);
        return (blended, new ScoringStrategy(Backtester.EnsembleName, 0, 0, 0, 0, pairW, penW));
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
        p.Id, p.CreatedUtc, p.Numbers(), p.LuckyStars(), p.CutoffSequence, p.CutoffDrawNumber,
        p.ModelVersion, p.StrategyName,
        p.ActualNumbersCsv?.Split(',').Select(int.Parse).ToArray(),
        p.Matches,
        p.ActualLuckyStarsCsv?.Split(',').Select(int.Parse).ToArray(),
        p.LuckyStarMatches,
        explanation);
}
