namespace LottoPredictor.Core.Analysis;

public sealed record StrategyBacktest(
    ScoringStrategy Strategy,
    int Evaluated,
    double AvgMatches,
    double StdMatches,
    double RecencyWeightedAvg, // exponential-decay average (half-life 50 draws): recent form
    int[] MatchCounts); // index = number of matches 0..6

public sealed class BacktestReport
{
    public required IReadOnlyList<StrategyBacktest> Strategies { get; init; }
    public required StrategyBacktest Best { get; init; }
    public required double RandomExpectedMatches { get; init; }
    public required double[] RandomMatchDistribution { get; init; } // P(k matches), k=0..6
    public required StrategyBacktest RandomSimulated { get; init; }
    public required string Verdict { get; init; }
    public required int WarmupDraws { get; init; }

    /// <summary>Final multiplicative-weights distribution over strategies after the walk-forward
    /// run. This is what the hedge ensemble would use for the next real prediction.</summary>
    public required IReadOnlyDictionary<string, double> HedgeWeights { get; init; }
}

/// <summary>Walk-forward backtesting. For every evaluation draw the engine only ever receives the
/// draws that came strictly before it — the prefix list is materialised before the target draw is
/// touched, so future data cannot leak into a historical prediction.</summary>
public static class Backtester
{
    public const string EnsembleName = "hedge-ensemble";

    /// <summary>Multiplicative-weights learning rate: weight *= exp(eta * matches) per draw.</summary>
    private const double HedgeEta = 0.10;

    public static BacktestReport Run(
        IReadOnlyList<DrawEvent> draws,
        IReadOnlyList<ScoringStrategy> strategies,
        int evalWindow = 200,
        int warmup = 150,
        int randomSeed = 20260831)
    {
        if (draws.Count <= warmup + 1)
            throw new InvalidOperationException($"Need more than {warmup + 1} draws to backtest.");

        int start = Math.Max(warmup, draws.Count - evalWindow);
        int evaluated = draws.Count - start;

        var matchLists = strategies.ToDictionary(s => s.Name, _ => new List<int>(evaluated));
        var ensembleMatches = new List<int>(evaluated);
        var hedge = strategies.ToDictionary(s => s.Name, _ => 1.0 / strategies.Count);
        var randomMatches = new List<int>(evaluated);
        var rng = new Random(randomSeed);
        double expectedSum = 0;

        for (int i = start; i < draws.Count; i++)
        {
            var prefix = new PrefixView(draws, i); // draws[0..i) only — the target draw is invisible
            var actual = draws[i].Numbers;
            var fs = FeatureCalculator.Compute(prefix);

            var stepScores = new List<(ScoringStrategy Strategy, Dictionary<int, double> Scores, int Matches)>(strategies.Count);
            foreach (var strategy in strategies)
            {
                var scores = PredictionEngine.ScoreNumbers(fs, strategy);
                var result = PredictionEngine.GenerateFromScores(fs, scores, strategy);
                int m = CountMatches(result.Numbers, actual);
                matchLists[strategy.Name].Add(m);
                stepScores.Add((strategy, scores, m));
            }

            // Hedge ensemble: predict with the weights learned from PAST draws only, then update.
            var blended = PredictionEngine.BlendScores(
                stepScores.Select(t => (t.Scores, hedge[t.Strategy.Name])));
            double pairW = stepScores.Sum(t => hedge[t.Strategy.Name] * t.Strategy.PairWeight);
            double penW = stepScores.Sum(t => hedge[t.Strategy.Name] * t.Strategy.PenaltyWeight);
            var ensemblePrediction = PredictionEngine.GenerateFromScores(
                fs, blended, new ScoringStrategy(EnsembleName, 0, 0, 0, 0, pairW, penW));
            ensembleMatches.Add(CountMatches(ensemblePrediction.Numbers, actual));

            double norm = 0;
            foreach (var t in stepScores)
            {
                hedge[t.Strategy.Name] *= Math.Exp(HedgeEta * t.Matches);
                norm += hedge[t.Strategy.Name];
            }
            if (norm <= 0 || double.IsNaN(norm) || double.IsInfinity(norm))
                foreach (var s in strategies) hedge[s.Name] = 1.0 / strategies.Count;
            else
                foreach (var s in strategies) hedge[s.Name] /= norm;

            int pool = fs.Pool.PoolAt(prefix.Count - 1);
            expectedSum += 36.0 / pool;
            randomMatches.Add(CountMatches(RandomSet(rng, pool), actual));
        }

        var results = strategies
            .Select(s => Summarise(s, matchLists[s.Name]))
            .ToList();
        results.Add(Summarise(new ScoringStrategy(EnsembleName, 0, 0, 0, 0, 0, 0), ensembleMatches));

        var best = results.OrderByDescending(r => r.RecencyWeightedAvg).ThenBy(r => r.Strategy.Name).First();
        double randomExpected = expectedSum / evaluated;
        var randomSummary = Summarise(new ScoringStrategy("random-baseline", 0, 0, 0, 0, 0, 0), randomMatches);

        // Honest verdict with a Bonferroni correction: picking the best of N tested strategies
        // inflates apparent skill, so the significance threshold widens with N.
        double se = best.StdMatches / Math.Sqrt(best.Evaluated);
        double diff = best.AvgMatches - randomExpected;
        double zCrit = StatFunctions.InverseNormalCdf(1.0 - 0.025 / Math.Max(1, results.Count));
        string verdict = diff <= zCrit * se
            ? $"No measurable advantage over random selection. Best strategy '{best.Strategy.Name}' averaged " +
              $"{best.AvgMatches:0.000} matches vs {randomExpected:0.000} expected from random picks " +
              $"(difference {diff:+0.000;-0.000} is within the Bonferroni-corrected noise band ±{zCrit * se:0.000} " +
              $"for {results.Count} tested strategies). This is the expected outcome for a fair lottery."
            : $"Strategy '{best.Strategy.Name}' averaged {best.AvgMatches:0.000} matches vs {randomExpected:0.000} " +
              $"expected from random picks over {best.Evaluated} draws. The difference exceeds even the " +
              $"Bonferroni-corrected noise band (±{zCrit * se:0.000} across {results.Count} strategies). " +
              "A real, persistent effect like this would suggest physical bias — verify before trusting it.";

        var pool2 = PoolInfo.Detect(draws);
        return new BacktestReport
        {
            Strategies = results,
            Best = best,
            RandomExpectedMatches = randomExpected,
            RandomMatchDistribution = HypergeometricMatchDistribution(pool2.PoolSize),
            RandomSimulated = randomSummary,
            Verdict = verdict,
            WarmupDraws = start,
            HedgeWeights = hedge,
        };
    }

    public static int CountMatches(IReadOnlyList<int> predicted, IReadOnlyList<int> actual)
    {
        var set = new HashSet<int>(actual);
        return predicted.Count(set.Contains);
    }

    /// <summary>The exact read-only prefix view the backtest loop hands to the engine.
    /// Exposed so tests can prove future draws are invisible through it.</summary>
    public static IReadOnlyList<DrawEvent> Prefix(IReadOnlyList<DrawEvent> draws, int count) =>
        new PrefixView(draws, count);

    /// <summary>P(k of 6 picked match) for a uniform 6-of-poolSize draw.</summary>
    public static double[] HypergeometricMatchDistribution(int poolSize)
    {
        var dist = new double[7];
        double denom = Choose(poolSize, 6);
        for (int k = 0; k <= 6; k++)
            dist[k] = Choose(6, k) * Choose(poolSize - 6, 6 - k) / denom;
        return dist;
    }

    private static double Choose(int n, int k)
    {
        if (k < 0 || k > n) return 0;
        double r = 1;
        for (int i = 1; i <= k; i++) r = r * (n - k + i) / i;
        return r;
    }

    private static int[] RandomSet(Random rng, int poolSize)
    {
        var set = new HashSet<int>();
        while (set.Count < 6) set.Add(rng.Next(1, poolSize + 1));
        return [.. set.OrderBy(x => x)];
    }

    private const double RecencyHalfLife = 50.0;

    private static StrategyBacktest Summarise(ScoringStrategy strategy, List<int> matches)
    {
        var counts = new int[7];
        foreach (var m in matches) counts[m]++;
        double avg = matches.Count > 0 ? matches.Average() : 0;
        double var = matches.Count > 1
            ? matches.Sum(m => (m - avg) * (m - avg)) / (matches.Count - 1)
            : 0;

        // Matches are in chronological order; newer results get exponentially more weight.
        double weightedSum = 0, weightTotal = 0;
        for (int i = 0; i < matches.Count; i++)
        {
            double w = Math.Pow(0.5, (matches.Count - 1 - i) / RecencyHalfLife);
            weightedSum += w * matches[i];
            weightTotal += w;
        }
        double recency = weightTotal > 0 ? weightedSum / weightTotal : 0;

        return new StrategyBacktest(strategy, matches.Count, avg, Math.Sqrt(var), recency, counts);
    }

    /// <summary>Zero-copy read-only view of the first N items of a list.</summary>
    private sealed class PrefixView(IReadOnlyList<DrawEvent> source, int count) : IReadOnlyList<DrawEvent>
    {
        public DrawEvent this[int index] => index < count
            ? source[index]
            : throw new ArgumentOutOfRangeException(nameof(index));
        public int Count => count;
        public IEnumerator<DrawEvent> GetEnumerator()
        {
            for (int i = 0; i < count; i++) yield return source[i];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
