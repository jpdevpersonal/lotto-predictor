using LottoPredictor.Core.Models;

namespace LottoPredictor.Core.Analysis;

/// <summary>Generates mutations, crossovers and random candidates from training-only seeds,
/// and supports multi-generation tournament evolution strictly over pre-holdout training prefixes.</summary>
public static class StrategyOptimizer
{
    public const int MaxLearnedKept = 5;
    public const int DefaultPrefixEvolutionRounds = 3;

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

        // Crossover: blend (BLX-alpha) and uniform gene mixing of two distinct parents from the top 6.
        var parents = seeds.Take(6).ToArray();
        for (int c = 0; c < 3 && parents.Length >= 2; c++)
        {
            var a = parents[rng.Next(parents.Length)];
            ScoringStrategy b;
            do { b = parents[rng.Next(parents.Length)]; } while (ReferenceEquals(a, b) && parents.Length > 1);
            var child = Normalise(
                $"learned-g{generation}-{index++}",
                Blend(rng, a.WLongTerm, b.WLongTerm, sigma * 0.4),
                Blend(rng, a.WRecent, b.WRecent, sigma * 0.4),
                Blend(rng, a.WGap, b.WGap, sigma * 0.4),
                Blend(rng, a.WMomentum, b.WMomentum, sigma * 0.4),
                Blend(rng, a.WBias, b.WBias, sigma * 0.4),
                Blend(rng, a.WBonus, b.WBonus, sigma * 0.4),
                Blend(rng, a.PairWeight, b.PairWeight, sigma * 0.25),
                Blend(rng, a.PenaltyWeight, b.PenaltyWeight, sigma * 0.5));
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

    /// <summary>Runs a multi-generation tournament evolution strictly over the training prefix
    /// (draws before the holdout), computing prefix feature matrices once and evolving a population
    /// across <paramref name="evolutionRounds"/> generations using variance-reduced composite fitness.</summary>
    public static IReadOnlyList<ScoringStrategy> EvolveOnPrefix(
        IReadOnlyList<DrawEvent> trainingPrefix,
        IReadOnlyList<ScoringStrategy> initialSeeds,
        int generation,
        int evalWindow = 1000,
        int warmup = 150,
        int? configuredPoolSize = null,
        DateOnly? poolExpansionDate = null,
        IReadOnlyList<PoolRule>? ruleEras = null,
        int evolutionRounds = DefaultPrefixEvolutionRounds,
        Action<int, int>? onProgress = null,
        CancellationToken ct = default)
    {
        if (trainingPrefix.Count <= warmup + 2)
            return GenerateCandidates(initialSeeds, generation);

        int maxTrainSteps = Math.Min(evalWindow, 300);
        int start = Math.Max(warmup, trainingPrefix.Count - maxTrainSteps);
        int stepCount = trainingPrefix.Count - start;
        if (stepCount < 2)
            return GenerateCandidates(initialSeeds, generation);

        var steps = new List<(FeatureSet Fs, (int[] Numbers, double[][] X) Matrix, int[] Actual, double StepP3)>(stepCount);
        for (int i = start; i < trainingPrefix.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var prefix = Backtester.Prefix(trainingPrefix, i);
            var fs = FeatureCalculator.Compute(
                prefix, configuredPoolSize, poolExpansionDate, trainingPrefix[i].Date, ruleEras, includeTriples: false);
            var matrix = PredictionEngine.FeatureMatrix(fs);
            double p3 = Backtester.ThreePlusProbability(fs.Pool.NextPoolSize, fs.PickCount);
            steps.Add((fs, matrix, trainingPrefix[i].Numbers, p3));
            onProgress?.Invoke(i - start + 1, stepCount);
        }

        var population = initialSeeds.ToList();
        var fitnessByStrategy = new Dictionary<string, double>();

        double EvaluateStrategy(ScoringStrategy strategy)
        {
            string key = strategy.Describe() + $"|pair={strategy.PairWeight:0.####}|pen={strategy.PenaltyWeight:0.####}";
            if (fitnessByStrategy.TryGetValue(key, out double cached)) return cached;

            int threePlus = 0;
            int totalMatches = 0;
            double recencyWeighted = 0, recencyWeightTotal = 0;
            double sumScoreZ = 0;

            for (int t = 0; t < steps.Count; t++)
            {
                var (fs, matrix, actual, _) = steps[t];
                var scores = PredictionEngine.ScoreNumbers(matrix, strategy);
                double sz = LearningSignals.ScoreZ(LearningSignals.Standardise(scores), actual);
                sumScoreZ += sz;

                // Top-pick match evaluation using fast combination search on the top candidates.
                var pred = PredictionEngine.GenerateFromScores(fs, scores, strategy);
                int m = Backtester.CountMatches(pred.Numbers, actual);
                totalMatches += m;
                if (m >= 3) threePlus++;

                double w = Math.Pow(0.5, (steps.Count - 1 - t) / 50.0);
                recencyWeighted += w * m;
                recencyWeightTotal += w;
            }

            double threePlusRate = (double)threePlus / steps.Count;
            double avgMatches = (double)totalMatches / steps.Count;
            double recencyAvg = recencyWeightTotal > 0 ? recencyWeighted / recencyWeightTotal : 0;
            double meanScoreZ = sumScoreZ / steps.Count;

            // Variance-reduced composite fitness: combines 3+ hit rate, average matches, recent form, and dense ScoreZ.
            double fitness = 25.0 * threePlusRate + 1.5 * avgMatches + 0.8 * recencyAvg + 0.5 * meanScoreZ;
            fitnessByStrategy[key] = fitness;
            return fitness;
        }

        int rounds = Math.Max(1, evolutionRounds);
        for (int r = 1; r < rounds; r++)
        {
            ct.ThrowIfCancellationRequested();
            var ranked = population
                .OrderByDescending(EvaluateStrategy)
                .ThenBy(s => s.Name)
                .Take(Math.Max(6, initialSeeds.Count))
                .ToList();
            var roundChildren = GenerateCandidates(ranked, generation * 17 + r);
            population = ranked.Concat(roundChildren).DistinctBy(s => s.Describe()).ToList();
        }

        var finalElites = population
            .OrderByDescending(EvaluateStrategy)
            .ThenBy(s => s.Name)
            .Take(Math.Max(6, initialSeeds.Count))
            .ToList();

        return GenerateCandidates(finalElites, generation);
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

    private static double Blend(Random rng, double a, double b, double jitterSigma)
    {
        if (rng.Next(2) == 0)
            return (rng.Next(2) == 0 ? a : b) + Gaussian(rng, jitterSigma);
        double lo = Math.Min(a, b), hi = Math.Max(a, b);
        double span = hi - lo;
        double alpha = 0.25 * span;
        double val = (lo - alpha) + rng.NextDouble() * (span + 2.0 * alpha);
        return val + Gaussian(rng, jitterSigma * 0.5);
    }

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
