namespace LottoPredictor.Core.Analysis;

public sealed record StrategyBacktest(
    ScoringStrategy Strategy,
    int Evaluated,
    double AvgMatches,
    double StdMatches,
    double RecencyWeightedAvg, // exponential-decay average (half-life 50 draws): recent form
    int[] MatchCounts,
    int FourPlusHits,
    double FourPlusRate,
    double FourPlusCiLow,
    double FourPlusCiHigh); // index = number of matches 0..6

public sealed class BacktestReport
{
    public required IReadOnlyList<StrategyBacktest> Strategies { get; init; }
    public required StrategyBacktest Best { get; init; }
    public required double RandomExpectedMatches { get; init; }
    public required double RandomFourPlusProbability { get; init; }
    public required double RandomExpectedFourPlusHits { get; init; }
    public required double[] RandomMatchDistribution { get; init; } // P(k matches), k=0..6
    public required StrategyBacktest RandomSimulated { get; init; }
    public required string Verdict { get; init; }
    public required int WarmupDraws { get; init; }
    /// <summary>Number of chronological evaluation draws used to choose the strategy.</summary>
    public required int SelectionEvaluated { get; init; }
    /// <summary>Number of later, untouched evaluation draws used for the reported result.</summary>
    public required int HoldoutEvaluated { get; init; }

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
        int randomSeed = 20260831,
        int? configuredPoolSize = null,
        DateOnly? poolExpansionDate = null)
    {
        if (draws.Count <= warmup + 2)
            throw new InvalidOperationException($"Need more than {warmup + 2} draws to backtest.");

        int start = Math.Max(warmup, draws.Count - evalWindow);
        int evaluated = draws.Count - start;
        int selectionEvaluated = Math.Max(1, evaluated * 2 / 3);
        int holdoutEvaluated = evaluated - selectionEvaluated;
        if (holdoutEvaluated < 1)
            throw new InvalidOperationException("Need at least two evaluation draws to reserve a chronological holdout.");

        var matchLists = strategies.ToDictionary(s => s.Name, _ => new List<int>(evaluated));
        var ensembleMatches = new List<int>(evaluated);
        var hedge = strategies.ToDictionary(s => s.Name, _ => 1.0 / strategies.Count);
        var randomMatches = new List<int>(evaluated);
        var rng = new Random(randomSeed);
        var expectedMatches = new List<double>(evaluated);
        var expectedFourPlus = new List<double>(evaluated);

        for (int i = start; i < draws.Count; i++)
        {
            var prefix = new PrefixView(draws, i); // draws[0..i) only — the target draw is invisible
            var actual = draws[i].Numbers;
            var fs = FeatureCalculator.Compute(
                prefix, configuredPoolSize, poolExpansionDate, draws[i].Date);

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

            int pool = fs.Pool.NextPoolSize;
            int pickCount = fs.PickCount;
            expectedMatches.Add((double)(pickCount * pickCount) / pool);
            expectedFourPlus.Add(FourPlusProbability(pool, pickCount));
            randomMatches.Add(CountMatches(RandomSet(rng, pool, pickCount), actual));
        }

        // Select a strategy using only the earlier part of the walk-forward window. The later
        // chronological holdout is never used to choose a winner, so it remains an honest
        // estimate of the chosen strategy's performance.
        var selectionResults = strategies
            .Select(s => Summarise(s, matchLists[s.Name].Take(selectionEvaluated).ToList()))
            .ToList();
        selectionResults.Add(Summarise(
            new ScoringStrategy(EnsembleName, 0, 0, 0, 0, 0, 0),
            ensembleMatches.Take(selectionEvaluated).ToList()));

        var selected = selectionResults
            .OrderByDescending(r => r.FourPlusRate)
            .ThenByDescending(r => r.RecencyWeightedAvg)
            .ThenBy(r => r.Strategy.Name)
            .First();

        var results = strategies
            .Select(s => Summarise(s, matchLists[s.Name].Skip(selectionEvaluated).ToList()))
            .ToList();
        results.Add(Summarise(
            new ScoringStrategy(EnsembleName, 0, 0, 0, 0, 0, 0),
            ensembleMatches.Skip(selectionEvaluated).ToList()));
        var best = results.Single(r => r.Strategy.Name == selected.Strategy.Name);

        double randomExpected = expectedMatches.Skip(selectionEvaluated).Average();
        double randomFourPlusProbability = expectedFourPlus.Skip(selectionEvaluated).Average();
        double randomExpectedFourPlusHits = expectedFourPlus.Skip(selectionEvaluated).Sum();
        var randomSummary = Summarise(
            new ScoringStrategy("random-baseline", 0, 0, 0, 0, 0, 0),
            randomMatches.Skip(selectionEvaluated).ToList());

        // Honest verdict with a Bonferroni correction: picking the best of N tested strategies
        // inflates apparent skill, so the significance threshold widens with N.
                double se = Math.Sqrt(randomFourPlusProbability * (1.0 - randomFourPlusProbability) / best.Evaluated);
                double diff = best.FourPlusRate - randomFourPlusProbability;
        double zCrit = StatFunctions.InverseNormalCdf(1.0 - 0.025 / Math.Max(1, selectionResults.Count));
                string verdict = diff <= zCrit * se
                        ? $"No measurable advantage over random selection for the four-plus objective. Best strategy '{best.Strategy.Name}' hit " +
                            $"4+ main numbers {best.FourPlusHits} time(s) in {best.Evaluated} held-out draws " +
                            $"({100.0 * best.FourPlusRate:0.###}% vs {100.0 * randomFourPlusProbability:0.###}% exact random probability per line). " +
                            $"The observed difference is within the Bonferroni-corrected noise band for {selectionResults.Count} strategies selected on earlier draws. " +
                            $"Average matches remain secondary: {best.AvgMatches:0.000} vs {randomExpected:0.000} expected."
                        : $"Strategy '{best.Strategy.Name}' hit 4+ main numbers {best.FourPlusHits} time(s) in {best.Evaluated} " +
                            $"held-out draws ({100.0 * best.FourPlusRate:0.###}% vs {100.0 * randomFourPlusProbability:0.###}% exact random probability per line). " +
                            "This exceeds the current correction after chronological strategy selection only; repeated experiments and future prospective tracking still need to confirm it.";

        var pool2 = PoolInfo.Detect(draws, configuredPoolSize, poolExpansionDate);
        return new BacktestReport
        {
            Strategies = results,
            Best = best,
            RandomExpectedMatches = randomExpected,
            RandomFourPlusProbability = randomFourPlusProbability,
            RandomExpectedFourPlusHits = randomExpectedFourPlusHits,
            RandomMatchDistribution = HypergeometricMatchDistribution(pool2.PoolSize, draws[0].Numbers.Length),
            RandomSimulated = randomSummary,
            Verdict = verdict,
            WarmupDraws = start,
            SelectionEvaluated = selectionEvaluated,
            HoldoutEvaluated = holdoutEvaluated,
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
    public static double[] HypergeometricMatchDistribution(int poolSize, int pickCount = 6)
    {
        var dist = new double[pickCount + 1];
        double denom = Choose(poolSize, pickCount);
        for (int k = 0; k <= pickCount; k++)
            dist[k] = Choose(pickCount, k) * Choose(poolSize - pickCount, pickCount - k) / denom;
        return dist;
    }

    public static double FourPlusProbability(int poolSize, int pickCount) =>
        HypergeometricMatchDistribution(poolSize, pickCount).Skip(4).Sum();

    private static double Choose(int n, int k)
    {
        if (k < 0 || k > n) return 0;
        double r = 1;
        for (int i = 1; i <= k; i++) r = r * (n - k + i) / i;
        return r;
    }

    private static int[] RandomSet(Random rng, int poolSize, int pickCount)
    {
        var set = new HashSet<int>();
        while (set.Count < pickCount) set.Add(rng.Next(1, poolSize + 1));
        return [.. set.OrderBy(x => x)];
    }

    private const double RecencyHalfLife = 50.0;

    public static (double Low, double High) WilsonInterval(int hits, int total, double z = 1.959963984540054)
    {
        if (total <= 0) return (0, 0);
        if (hits == 0)
        {
            double high = z * z / (total + z * z);
            return (0, high);
        }
        double phat = (double)hits / total;
        double denom = 1 + z * z / total;
        double centre = phat + z * z / (2 * total);
        double margin = z * Math.Sqrt((phat * (1 - phat) + z * z / (4 * total)) / total);
        return (Math.Max(0, (centre - margin) / denom), Math.Min(1, (centre + margin) / denom));
    }

    private static StrategyBacktest Summarise(ScoringStrategy strategy, List<int> matches)
    {
        var counts = new int[7];
        foreach (var m in matches) counts[m]++;
        int fourPlusHits = counts.Skip(4).Sum();
        var (ciLow, ciHigh) = WilsonInterval(fourPlusHits, matches.Count);
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

        return new StrategyBacktest(
            strategy, matches.Count, avg, Math.Sqrt(var), recency, counts,
            fourPlusHits, matches.Count > 0 ? (double)fourPlusHits / matches.Count : 0, ciLow, ciHigh);
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
