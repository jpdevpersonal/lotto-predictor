using LottoPredictor.Core.Data;
using LottoPredictor.Core.Dtos;
using Microsoft.EntityFrameworkCore;

namespace LottoPredictor.Core.Services;

public interface ILearningService
{
    Task<LearningDto> GetLearningAsync(int historyLimit = 500, CancellationToken ct = default);
}

public class LearningService(IDbContextFactory<LottoDbContext> contextFactory, IAnalysisService analysis)
    : ILearningService
{
    public async Task<LearningDto> GetLearningAsync(int historyLimit = 500, CancellationToken ct = default)
    {
        var snapshot = await analysis.GetSnapshotAsync(ct);

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var learned = await db.LearnedStrategies.AsNoTracking()
            .OrderByDescending(s => s.RecencyWeightedAvg)
            .ToListAsync(ct);
        var history = await db.StrategyPerformanceLogs.AsNoTracking()
            .OrderByDescending(l => l.DrawCount).ThenByDescending(l => l.RecencyWeightedAvg)
            .Take(Math.Clamp(historyLimit, 1, 2000))
            .ToListAsync(ct);

        var active = snapshot.ActiveStrategy;
        var fs = snapshot.Features;

        string assessment = fs.ChiSquareDf == 0
            ? "Not enough current-era draws for a uniformity test."
            : fs.ChiSquarePValue < 0.01
                ? $"Ball frequencies deviate significantly from uniform (p={fs.ChiSquarePValue:0.0000}) — " +
                  "a possible physical bias the bias-detector strategies can exploit."
                : fs.ChiSquarePValue < 0.05
                    ? $"Weak evidence of non-uniformity (p={fs.ChiSquarePValue:0.0000}); likely noise, monitored each draw."
                    : $"Ball frequencies are consistent with a fair machine (p={fs.ChiSquarePValue:0.0000}); " +
                      "no exploitable bias exists in the current era.";

        var hedgeWeights = snapshot.HedgeWeights
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new HedgeWeightDto(kv.Key, Math.Round(kv.Value, 4)))
            .ToList();

        return new LearningDto(
            snapshot.LearningGeneration,
            active.Name,
            active.Name == Analysis.Backtester.EnsembleName
                ? "online multiplicative-weights blend of all strategies"
                : active.Describe(),
            active.Name.StartsWith("learned-") || active.Name == Analysis.Backtester.EnsembleName,
            new UniformityDto(
                Math.Round(fs.ChiSquare, 2),
                fs.ChiSquareDf,
                Math.Round(fs.ChiSquarePValue, 6),
                fs.ChiSquareWindowDraws,
                assessment),
            hedgeWeights,
            learned.Select(s => new LearnedStrategyDto(
                s.Name,
                Analysis.StrategyOptimizer.ToStrategy(s).Describe(),
                s.Generation,
                Math.Round(s.AvgMatches, 4),
                Math.Round(s.RecencyWeightedAvg, 4),
                s.EvaluatedDraws,
                s.CreatedUtc)).ToList(),
            history.Select(l => new PerformancePointDto(
                l.DrawCount,
                l.LoggedUtc,
                l.StrategyName,
                Math.Round(l.AvgMatches, 4),
                Math.Round(l.RecencyWeightedAvg, 4),
                Math.Round(l.RandomExpected, 4),
                l.WasActive)).ToList());
    }
}
