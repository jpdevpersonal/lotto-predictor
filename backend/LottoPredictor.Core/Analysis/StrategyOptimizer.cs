using LottoPredictor.Core.Models;

namespace LottoPredictor.Core.Analysis;

/// <summary>Genetic search over the continuous strategy-weight space. Each analysis rebuild is one
/// generation: elite strategies are mutated with an annealed step size (exploitation), pairs of
/// parents are crossed over (recombination), and fresh random immigrants keep diversity
/// (exploration). All candidates are judged by the same leak-free walk-forward backtest as the
/// hand-written strategies, so selection stays honest.</summary>
public static class StrategyOptimizer
{
    public const int MaxLearnedKept = 5;

    /// <summary>Deterministic for a given generation so results are reproducible.</summary>
    public static IReadOnlyList<ScoringStrategy> GenerateCandidates(
        IReadOnlyList<ScoringStrategy> seeds,
        int generation)
    {
        var rng = new Random(unchecked(20260831 * 31 + generation));
        var candidates = new List<ScoringStrategy>();
        int index = 1;

        // Annealed mutation: broad early exploration, fine-tuning once lineages mature.
        double sigma = Math.Max(0.05, 0.25 * Math.Pow(0.97, generation));

        // Elitism: mutate the current best performers.
        foreach (var seed in seeds.Take(3))
        {
            for (int p = 0; p < 2; p++)
                candidates.Add(Mutate(seed, $"learned-g{generation}-{index++}", rng, sigma));
        }

        // Crossover: uniform gene mixing of two distinct parents from the top 6, lightly mutated.
        var parents = seeds.Take(6).ToArray();
        for (int c = 0; c < 3 && parents.Length >= 2; c++)
        {
            var a = parents[rng.Next(parents.Length)];
            ScoringStrategy b;
            do { b = parents[rng.Next(parents.Length)]; } while (ReferenceEquals(a, b) && parents.Length > 1);
            var child = Normalise(
                $"learned-g{generation}-{index++}",
                Pick(rng, a.WLongTerm, b.WLongTerm) + Gaussian(rng, sigma * 0.5),
                Pick(rng, a.WRecent, b.WRecent) + Gaussian(rng, sigma * 0.5),
                Pick(rng, a.WGap, b.WGap) + Gaussian(rng, sigma * 0.5),
                Pick(rng, a.WMomentum, b.WMomentum) + Gaussian(rng, sigma * 0.5),
                Pick(rng, a.WBias, b.WBias) + Gaussian(rng, sigma * 0.5),
                Pick(rng, a.WBonus, b.WBonus) + Gaussian(rng, sigma * 0.5),
                Pick(rng, a.PairWeight, b.PairWeight) + Gaussian(rng, sigma * 0.3),
                Pick(rng, a.PenaltyWeight, b.PenaltyWeight) + Gaussian(rng, sigma * 0.6));
            candidates.Add(child);
        }

        // Random immigrants: unbiased points to escape local optima.
        for (int r = 0; r < 2; r++)
            candidates.Add(Normalise(
                $"learned-g{generation}-{index++}",
                rng.NextDouble(), rng.NextDouble(), rng.NextDouble(), rng.NextDouble(),
                rng.NextDouble(), rng.NextDouble(), rng.NextDouble(), rng.NextDouble() * 2.0));

        return candidates;
    }

    public static ScoringStrategy ToStrategy(LearnedStrategy s) => new(
        s.Name, s.WLongTerm, s.WRecent, s.WGap, s.WMomentum, s.PairWeight, s.PenaltyWeight, s.WBias, s.WBonus);

    private static ScoringStrategy Mutate(ScoringStrategy seed, string name, Random rng, double sigma) =>
        Normalise(
            name,
            seed.WLongTerm + Gaussian(rng, sigma),
            seed.WRecent + Gaussian(rng, sigma),
            seed.WGap + Gaussian(rng, sigma),
            seed.WMomentum + Gaussian(rng, sigma),
            seed.WBias + Gaussian(rng, sigma),
            seed.WBonus + Gaussian(rng, sigma),
            seed.PairWeight + Gaussian(rng, sigma * 0.6),
            seed.PenaltyWeight + Gaussian(rng, sigma * 1.2));

    private static double Pick(Random rng, double a, double b) => rng.Next(2) == 0 ? a : b;

    /// <summary>Clamp weights to sane ranges and normalise the six score weights to sum 1,
    /// so learned strategies stay comparable to the hand-written ones.</summary>
    private static ScoringStrategy Normalise(
        string name, double wLong, double wRecent, double wGap, double wMomentum, double wBias,
        double wBonus, double pair, double penalty)
    {
        wLong = Math.Max(0, wLong);
        wRecent = Math.Max(0, wRecent);
        wGap = Math.Max(0, wGap);
        wMomentum = Math.Max(0, wMomentum);
        wBias = Math.Max(0, wBias);
        wBonus = Math.Max(0, wBonus);
        double sum = wLong + wRecent + wGap + wMomentum + wBias + wBonus;
        if (sum < 1e-9) { wLong = 1; sum = 1; }
        return new ScoringStrategy(
            name,
            Math.Round(wLong / sum, 4),
            Math.Round(wRecent / sum, 4),
            Math.Round(wGap / sum, 4),
            Math.Round(wMomentum / sum, 4),
            Math.Round(Math.Clamp(pair, 0.0, 1.0), 4),
            Math.Round(Math.Clamp(penalty, 0.0, 2.0), 4),
            Math.Round(wBias / sum, 4),
            Math.Round(wBonus / sum, 4));
    }

    private static double Gaussian(Random rng, double std)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return std * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
