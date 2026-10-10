using LottoPredictor.Core.Analysis;
using LottoPredictor.Core.Data;
using LottoPredictor.Core.Dtos;
using LottoPredictor.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LottoPredictor.Core.Services;

public interface IPredictionService
{
    Task<PredictionDto> GenerateAsync(bool excludeLastDrawNumbers = false, CancellationToken ct = default);
    Task<PredictionDto?> GetLatestAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PredictionDto>> GetHistoryAsync(int limit = 100, CancellationToken ct = default);
    /// <summary>Coverage-oriented K-line portfolio, computed on demand and never persisted.</summary>
    Task<PortfolioDto> GenerateLinesAsync(
        int count = 1,
        bool excludeLastDrawNumbers = false,
        CancellationToken ct = default);
    /// <summary>Coverage-oriented K-line portfolio persisted as K predictions, so every line is evaluated
    /// against each round of the next draw.</summary>
    Task<PortfolioDto> GeneratePortfolioAsync(
        int count = 10,
        bool excludeLastDrawNumbers = false,
        CancellationToken ct = default);
    Task<PortfolioDto?> GetLatestPortfolioAsync(CancellationToken ct = default);
    /// <summary>Consensus line over the same top-N lines (screen-only).</summary>
    Task<BestOfLinesDto> GenerateBestOfLinesAsync(int count = 50, CancellationToken ct = default);
}

public class PredictionService(IDbContextFactory<LottoDbContext> contextFactory, IAnalysisService analysis)
    : IPredictionService
{
    public async Task<PredictionDto> GenerateAsync(bool excludeLastDrawNumbers = false, CancellationToken ct = default)
    {
        var snapshot = await analysis.GetSnapshotAsync(ct);
        var strategy = snapshot.ActiveStrategy;
        HashSet<int>? excludedMain = null;
        HashSet<int>? excludedLuckyStars = null;
        if (excludeLastDrawNumbers)
        {
            var lastDrawNumbers = snapshot.Draws[^1].Numbers;
            excludedMain = lastDrawNumbers.Length > 0 ? [.. lastDrawNumbers] : null;

            if (snapshot.LuckyStarFeatures is not null)
            {
                var lastLuckyStars = snapshot.LatestLuckyStars;
                excludedLuckyStars = lastLuckyStars.Length > 0 ? [.. lastLuckyStars] : null;
            }
        }

        var result = strategy.Name == Backtester.EnsembleName
            ? PredictionEngine.GenerateEnsemble(
                snapshot.Features, snapshot.AllStrategies, snapshot.HedgeWeights, Backtester.EnsembleName, excludedMain)
            : PredictionEngine.Generate(snapshot.Features, strategy, excludedMain);
        var luckyStars = snapshot.LuckyStarFeatures is null
            ? null
            : GenerateForFeatures(snapshot, snapshot.LuckyStarFeatures, excludedLuckyStars).Numbers;
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

    public Task<PortfolioDto> GenerateLinesAsync(
        int count = 1,
        bool excludeLastDrawNumbers = false,
        CancellationToken ct = default) =>
        BuildPortfolioAsync(count, excludeLastDrawNumbers, persist: false, ct);

    public Task<PortfolioDto> GeneratePortfolioAsync(
        int count = 10,
        bool excludeLastDrawNumbers = false,
        CancellationToken ct = default) =>
        BuildPortfolioAsync(count, excludeLastDrawNumbers, persist: true, ct);

    private async Task<PortfolioDto> BuildPortfolioAsync(
        int count, bool excludeLastDrawNumbers, bool persist, CancellationToken ct)
    {
        count = Math.Clamp(count, 1, 200);
        var snapshot = await analysis.GetSnapshotAsync(ct);
        HashSet<int>? excludedMain = null;
        HashSet<int>? excludedLuckyStars = null;
        if (excludeLastDrawNumbers)
        {
            var lastDrawNumbers = snapshot.Draws[^1].Numbers;
            excludedMain = lastDrawNumbers.Length > 0 ? [.. lastDrawNumbers] : null;

            if (snapshot.LuckyStarFeatures is not null)
            {
                var lastLuckyStars = snapshot.LatestLuckyStars;
                excludedLuckyStars = lastLuckyStars.Length > 0 ? [.. lastLuckyStars] : null;
            }
        }

        var (scores, strategy) = ActiveScores(snapshot);
        var portfolio = PortfolioOptimizer.BuildCoveragePortfolio(
            snapshot.Features, scores, strategy, count, excludedMain);
        var starLines = GenerateLuckyStarLines(snapshot, count, excludedLuckyStars);
        int[] StarsFor(int index) => starLines.Count > 0 ? starLines[index % starLines.Count].Numbers : [];
        var lastDraw = snapshot.Draws[^1];
        var createdUtc = DateTime.UtcNow;

        string? portfolioId = null;
        var persisted = new PredictionDto?[portfolio.Lines.Count];
        if (persist)
        {
            portfolioId = Guid.NewGuid().ToString("N");
            var rows = portfolio.Lines.Select((line, index) =>
            {
                var row = new Prediction
                {
                    CreatedUtc = createdUtc,
                    CutoffSequence = lastDraw.Sequence,
                    CutoffDrawNumber = lastDraw.DrawNumber,
                    ModelVersion = snapshot.ActiveStrategy.Version,
                    StrategyName = snapshot.ActiveStrategy.Name,
                    PortfolioId = portfolioId,
                    PortfolioRank = line.Rank,
                };
                row.SetNumbers(line.Numbers);
                var stars = StarsFor(index);
                row.LuckyStarsCsv = stars.Length > 0 ? string.Join(",", stars) : null;
                return row;
            }).ToList();

            await using var db = await contextFactory.CreateDbContextAsync(ct);
            db.Predictions.AddRange(rows);
            await db.SaveChangesAsync(ct);
            for (int i = 0; i < rows.Count; i++) persisted[i] = ToDto(rows[i], null);
        }

        var lines = portfolio.Lines.Select((line, index) => new PortfolioLineDto(
            line.Rank, line.Numbers, StarsFor(index), line.Score, line.MaxOverlapWithEarlier, persisted[index]))
            .ToList();
        var simulation = new PortfolioSimulationDto(
            portfolio.CoverageOptimized.Trials,
            Math.Round(portfolio.CoverageOptimized.Probability, 8),
            Math.Round(portfolio.CoverageOptimized.CiLow, 8),
            Math.Round(portfolio.CoverageOptimized.CiHigh, 8),
            Math.Round(portfolio.RandomDistinct.Probability, 8));
        return ToPortfolioDto(
            portfolioId, createdUtc, snapshot.ActiveStrategy.Name, snapshot.ActiveStrategy.Version,
            lastDraw.DrawNumber, snapshot.Lottery.RoundCount, portfolio.Objective, portfolio.Odds,
            simulation, lines);
    }

    public async Task<PortfolioDto?> GetLatestPortfolioAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var latestId = await db.Predictions.AsNoTracking()
            .Where(p => p.PortfolioId != null)
            .OrderByDescending(p => p.Id)
            .Select(p => p.PortfolioId)
            .FirstOrDefaultAsync(ct);
        if (latestId is null) return null;

        var rows = await db.Predictions.AsNoTracking()
            .Include(p => p.Evaluations)
            .ThenInclude(e => e.EvaluatedDraw)
            .Where(p => p.PortfolioId == latestId)
            .OrderBy(p => p.PortfolioRank)
            .ToListAsync(ct);

        var snapshot = await analysis.GetSnapshotAsync(ct);
        var sets = rows.Select(r => r.Numbers()).ToList();
        var simulation = PortfolioOptimizer.Simulate(
            sets, snapshot.Features.Pool.NextPoolSize, snapshot.Features.PickCount);
        var odds = PortfolioOptimizer.ComputeOdds(
            sets, snapshot.Features.Pool.NextPoolSize, snapshot.Features.PickCount, simulation);
        var lines = rows.Select((row, index) => new PortfolioLineDto(
            row.PortfolioRank ?? index + 1,
            row.Numbers(),
            row.LuckyStars(),
            null,
            index == 0 ? 0 : sets.Take(index).Max(s => s.Intersect(sets[index]).Count()),
            ToDto(row, null))).ToList();
        return ToPortfolioDto(
            latestId, rows[0].CreatedUtc, rows[0].StrategyName, rows[0].ModelVersion,
            rows[0].CutoffDrawNumber, snapshot.Lottery.RoundCount,
            $"P(at least one of K={rows.Count} fixed lines matches at least three main numbers in a round)",
            odds, new PortfolioSimulationDto(
                simulation.Trials, Math.Round(simulation.Probability, 8),
                Math.Round(simulation.CiLow, 8), Math.Round(simulation.CiHigh, 8), null), lines);
    }

    private static PortfolioDto ToPortfolioDto(
        string? portfolioId, DateTime createdUtc, string strategyName, string modelVersion,
        int cutoffDrawNumber, int roundCount, string objective, PortfolioOdds odds,
        PortfolioSimulationDto? simulation, IReadOnlyList<PortfolioLineDto> lines)
    {
        double perRound = odds.ThreePlusProbability;
        double anyRound = 1 - Math.Pow(1 - perRound, roundCount);

        var best = lines
            .Where(l => l.Prediction is not null)
            .SelectMany(l => l.Prediction!.Evaluations.Select(e => (l.Rank, e.Round, e.Matches)))
            .OrderByDescending(t => t.Matches).ThenBy(t => t.Rank).ThenBy(t => t.Round)
            .FirstOrDefault();
        bool evaluated = lines.Any(l => l.Prediction?.Evaluations.Count > 0);

        return new PortfolioDto(
            portfolioId, createdUtc, strategyName, modelVersion, cutoffDrawNumber, lines.Count,
            roundCount, objective,
            Math.Round(odds.SingleLineThreePlusProbability, 10),
            Math.Round(perRound, 10),
            odds.IsExact,
            Math.Round(anyRound, 10),
            odds.MaxPairwiseOverlap,
            PortfolioOptimizer.LinesForTarget(0.5, odds.SingleLineThreePlusProbability, roundCount),
            simulation,
            evaluated ? best.Matches : null,
            evaluated ? best.Round : null,
            evaluated ? best.Rank : null,
            lines);
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
        AnalysisSnapshot snapshot, int count, IReadOnlySet<int>? excludedNumbers = null)
    {
        if (snapshot.LuckyStarFeatures is null) return [];
        var (scores, lineStrategy) = ActiveScores(snapshot, snapshot.LuckyStarFeatures);
        return PredictionEngine.GenerateTopLines(
            snapshot.LuckyStarFeatures, scores, lineStrategy, count, excludedNumbers);
    }

    private static PredictionResult GenerateForFeatures(
        AnalysisSnapshot snapshot, FeatureSet features, IReadOnlySet<int>? excludedNumbers = null)
    {
        var strategy = snapshot.ActiveStrategy;
        return strategy.Name == Backtester.EnsembleName
            ? PredictionEngine.GenerateEnsemble(
                features, snapshot.AllStrategies, snapshot.HedgeWeights, Backtester.EnsembleName, excludedNumbers)
            : PredictionEngine.Generate(features, strategy, excludedNumbers);
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
            .Include(item => item.Evaluations)
            .ThenInclude(evaluation => evaluation.EvaluatedDraw)
            .Where(p => p.PortfolioId == null)
            .OrderByDescending(p => p.Id)
            .FirstOrDefaultAsync(ct);
        if (prediction is null) return null;
        return ToDto(prediction, await BuildExplanationAsync(prediction, ct));
    }

    public async Task<IReadOnlyList<PredictionDto>> GetHistoryAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var predictions = await db.Predictions.AsNoTracking()
            .Include(prediction => prediction.Evaluations)
            .ThenInclude(evaluation => evaluation.EvaluatedDraw)
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
        ToEvaluationDtos(p),
        explanation,
        p.PortfolioId,
        p.PortfolioRank);

    private static IReadOnlyList<PredictionEvaluationDto> ToEvaluationDtos(Prediction prediction)
    {
        if (prediction.Evaluations.Count == 0)
            return [];

        return prediction.Evaluations
            .OrderBy(evaluation => evaluation.EvaluatedDraw.Sequence)
            .Select(evaluation => new PredictionEvaluationDto(
                evaluation.EvaluatedDrawId,
                evaluation.EvaluatedDraw.DrawNumber,
                prediction.Evaluations.Count(other =>
                    other.EvaluatedDraw.DrawNumber == evaluation.EvaluatedDraw.DrawNumber &&
                    other.EvaluatedDraw.Sequence <= evaluation.EvaluatedDraw.Sequence),
                evaluation.ActualNumbersCsv.Split(',').Select(int.Parse).ToArray(),
                evaluation.Matches,
                evaluation.EvaluatedDraw.Bonus,
                evaluation.BonusMatches,
                string.IsNullOrWhiteSpace(evaluation.ActualLuckyStarsCsv)
                    ? []
                    : evaluation.ActualLuckyStarsCsv.Split(',').Select(int.Parse).ToArray(),
                evaluation.LuckyStarMatches,
                evaluation.EvaluatedUtc))
            .ToList();
    }
}
