namespace LottoPredictor.Core.Analysis;

public sealed record StrategyBacktest(
    ScoringStrategy Strategy,
    int Evaluated,
    double AvgMatches,
    double StdMatches,
    double RecencyWeightedAvg, // exponential-decay average (half-life 50 draws): recent form
    int[] MatchCounts,
    int ThreePlusHits,
    double ThreePlusRate,
    double ThreePlusCiLow,
    double ThreePlusCiHigh); // index = number of matches 0..6

public sealed class BacktestReport
{
    public required IReadOnlyList<StrategyBacktest> Strategies { get; init; }
    public required IReadOnlyList<StrategyBacktest> SelectionStrategies { get; init; }
    public required StrategyBacktest Best { get; init; }
    public required double RandomExpectedMatches { get; init; }
    public required double RandomThreePlusProbability { get; init; }
    public required double RandomExpectedThreePlusHits { get; init; }
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

    /// <summary>Fixed-size set probabilities proportional to exp(temperature * sum of scores).
    /// Temperature is fitted on selection draws only; the holdout log score is relative to
    /// uniform sampling without replacement. This provides denser feedback than sparse hits.</summary>
    public required double InformationTemperature { get; init; }
    public required double InformationLogScore { get; init; }
    public required double InformationZ { get; init; }
    /// <summary>z-score of the chosen strategy's held-out average matches against the
    /// uniform expectation pick²/pool.</summary>
    public required double AvgMatchesZ { get; init; }
}

/// <summary>Walk-forward feature histories contain only strict prefixes. Callers must also
/// train candidate weights before the holdout, as AnalysisService does.</summary>
public static class Backtester
{
    public const string EnsembleName = "hedge-ensemble";

    /// <summary>Multiplicative-weights learning rate for the three-plus hit indicator.</summary>
    private const double HedgeEta = 0.10;

    public static BacktestReport Run(
        IReadOnlyList<DrawEvent> draws,
        IReadOnlyList<ScoringStrategy> strategies,
        int evalWindow = 1000,
        int warmup = 150,
        int randomSeed = 20260831,
        int? configuredPoolSize = null,
        DateOnly? poolExpansionDate = null)
    {
        if (draws.Count <= warmup + 2)
            throw new InvalidOperationException($"Need more than {warmup + 2} draws to backtest.");

        int start = Math.Max(warmup, draws.Count - evalWindow);
        int evaluated = draws.Count - start;
        int selectionEvaluated = SelectionCutoff(draws.Count, evalWindow, warmup) - start;
        int holdoutEvaluated = evaluated - selectionEvaluated;
        if (holdoutEvaluated < 1)
            throw new InvalidOperationException("Need at least two evaluation draws to reserve a chronological holdout.");

        var matchLists = strategies.ToDictionary(s => s.Name, _ => new List<int>(evaluated));
        var ensembleMatches = new List<int>(evaluated);
        // Per step, per strategy: the score of every eligible ball and which balls were drawn,
        // kept so the information test can be run on whichever strategy is selected afterwards.
        var scoreSteps = strategies.ToDictionary(s => s.Name, _ => new List<InformationStep>(evaluated));
        scoreSteps[EnsembleName] = new List<InformationStep>(evaluated);
        var hedge = strategies.ToDictionary(s => s.Name, _ => 1.0 / strategies.Count);
        var randomMatches = new List<int>(evaluated);
        var rng = new Random(randomSeed);
        var expectedMatches = new List<double>(evaluated);
        var expectedThreePlus = new List<double>(evaluated);

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
                scoreSteps[strategy.Name].Add(InformationStep.From(scores, actual));
            }

            // Hedge ensemble: predict with the weights learned from PAST draws only, then update.
            var blended = PredictionEngine.BlendScores(
                stepScores.Select(t => (t.Scores, hedge[t.Strategy.Name])));
            double pairW = stepScores.Sum(t => hedge[t.Strategy.Name] * t.Strategy.PairWeight);
            double penW = stepScores.Sum(t => hedge[t.Strategy.Name] * t.Strategy.PenaltyWeight);
            var ensemblePrediction = PredictionEngine.GenerateFromScores(
                fs, blended, new ScoringStrategy(EnsembleName, 0, 0, 0, 0, pairW, penW));
            ensembleMatches.Add(CountMatches(ensemblePrediction.Numbers, actual));
            scoreSteps[EnsembleName].Add(InformationStep.From(blended, actual));

            double norm = 0;
            foreach (var t in stepScores)
            {
                hedge[t.Strategy.Name] *= Math.Exp(HedgeEta * (t.Matches >= 3 ? 1 : 0));
                norm += hedge[t.Strategy.Name];
            }
            if (norm <= 0 || double.IsNaN(norm) || double.IsInfinity(norm))
                foreach (var s in strategies) hedge[s.Name] = 1.0 / strategies.Count;
            else
                foreach (var s in strategies) hedge[s.Name] /= norm;

            int pool = fs.Pool.NextPoolSize;
            int pickCount = fs.PickCount;
            expectedMatches.Add((double)(pickCount * pickCount) / pool);
            expectedThreePlus.Add(ThreePlusProbability(pool, pickCount));
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
            .OrderByDescending(r => r.ThreePlusRate)
            .ThenByDescending(r => r.AvgMatches)
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
        double randomThreePlusProbability = expectedThreePlus.Skip(selectionEvaluated).Average();
        double randomExpectedThreePlusHits = expectedThreePlus.Skip(selectionEvaluated).Sum();
        var randomSummary = Summarise(
            new ScoringStrategy("random-baseline", 0, 0, 0, 0, 0, 0),
            randomMatches.Skip(selectionEvaluated).ToList());

        // The strategy was frozen before the holdout, so a single exact binomial test is valid.
        // A normal approximation is unsafe here because three-plus hits are very rare.
        double pValue = StatFunctions.BinomialUpperTail(
            best.Evaluated, best.ThreePlusHits, randomThreePlusProbability);

        var bestSteps = scoreSteps[best.Strategy.Name];
        var (temperature, logScore, informationZ) = InformationTest(
            bestSteps.Take(selectionEvaluated).ToList(), bestSteps.Skip(selectionEvaluated).ToList());
        double avgMatchesZ = best.Evaluated > 1 && best.StdMatches > 1e-9
            ? (best.AvgMatches - randomExpected) / (best.StdMatches / Math.Sqrt(best.Evaluated))
            : 0;
        string information = informationZ > 2.326
            ? $"Information test: the ranking's calibrated probabilities beat uniform by {logScore:0.0000} nats/draw " +
              $"on the holdout (z={informationZ:+0.00}, temperature {temperature:0.00}); confirm this prospectively before claiming an edge."
            : $"Information test: the ranking's calibrated probabilities score {logScore:+0.0000} nats/draw against uniform " +
              $"on the holdout (z={informationZ:+0.00}; average matches z={avgMatchesZ:+0.00}), i.e. the numbers chosen carry no " +
              "detectable information. Under fair independent draws, every single line has the same 3+ chance.";
        string verdict = pValue >= 0.05
            ? $"No measurable advantage over random selection for the three-plus objective. Best strategy '{best.Strategy.Name}' hit " +
              $"3+ main numbers {best.ThreePlusHits} time(s) in {best.Evaluated} held-out draws " +
              $"({100.0 * best.ThreePlusRate:0.###}% vs {100.0 * randomThreePlusProbability:0.###}% exact random probability per line; " +
              $"exact one-sided binomial p={pValue:0.####}). Average matches remain secondary: " +
              $"{best.AvgMatches:0.000} vs {randomExpected:0.000} expected."
            : $"Strategy '{best.Strategy.Name}' hit 3+ main numbers {best.ThreePlusHits} time(s) in {best.Evaluated} " +
              $"held-out draws ({100.0 * best.ThreePlusRate:0.###}% vs {100.0 * randomThreePlusProbability:0.###}% exact random probability per line; " +
              $"exact one-sided binomial p={pValue:0.####}). This is only a sparse historical signal; " +
              "future prospective tracking is still required before treating it as an edge.";
        verdict += " " + information;
        verdict += $" Selection used {selectionEvaluated} earlier draws; the holdout used {holdoutEvaluated}. " +
            "Repeated historical checks are exploratory, not independent evidence of an edge.";
        if (randomExpectedThreePlusHits < 10)
            verdict += " Fewer than 10 random three-plus hits are expected on this holdout; precision is limited.";

        var pool2 = PoolInfo.Detect(draws, configuredPoolSize, poolExpansionDate);
        return new BacktestReport
        {
            Strategies = results,
            SelectionStrategies = selectionResults,
            Best = best,
            RandomExpectedMatches = randomExpected,
            RandomThreePlusProbability = randomThreePlusProbability,
            RandomExpectedThreePlusHits = randomExpectedThreePlusHits,
            RandomMatchDistribution = HypergeometricMatchDistribution(pool2.PoolSize, draws[0].Numbers.Length),
            RandomSimulated = randomSummary,
            Verdict = verdict,
            WarmupDraws = start,
            SelectionEvaluated = selectionEvaluated,
            HoldoutEvaluated = holdoutEvaluated,
            HedgeWeights = hedge,
            InformationTemperature = temperature,
            InformationLogScore = logScore,
            InformationZ = informationZ,
            AvgMatchesZ = avgMatchesZ,
        };
    }

    public static int SelectionCutoff(int drawCount, int evalWindow, int warmup)
    {
        if (evalWindow < 2) throw new ArgumentOutOfRangeException(nameof(evalWindow));
        if (warmup < 1) throw new ArgumentOutOfRangeException(nameof(warmup));
        if (drawCount <= warmup + 2)
            throw new InvalidOperationException($"Need more than {warmup + 2} draws to backtest.");
        int start = Math.Max(warmup, drawCount - evalWindow);
        return start + Math.Max(1, (drawCount - start) * 2 / 3);
    }

    /// <summary>One walk-forward step's inputs to the information test.</summary>
    public sealed record InformationStep(double[] Scores, bool[] Drawn)
    {
        public static InformationStep From(Dictionary<int, double> scores, int[] actual)
        {
            var ordered = scores.OrderBy(kv => kv.Key).ToArray();
            var drawnSet = new HashSet<int>(actual);
            return new InformationStep(
                ordered.Select(kv => kv.Value).ToArray(),
                ordered.Select(kv => drawnSet.Contains(kv.Key)).ToArray());
        }
    }

    /// <summary>Log-likelihood ratio of fixed-size set probabilities versus uniform.</summary>
    private static double[] LogLikelihoodRatios(IReadOnlyList<InformationStep> steps, double beta)
    {
        var result = new double[steps.Count];
        for (int t = 0; t < steps.Count; t++)
        {
            var s = steps[t].Scores;
            int pool = s.Length;
            int pick = steps[t].Drawn.Count(d => d);
            double max = s.Max();
            // Fixed-size sets are sampled without replacement. The elementary symmetric
            // polynomial normalises their product weights; a per-ball softmax does not.
            var normalizer = new double[pick + 1];
            normalizer[0] = 1;
            for (int i = 0; i < pool; i++)
            {
                double weight = Math.Exp(beta * (s[i] - max));
                for (int k = Math.Min(pick, i + 1); k >= 1; k--)
                    normalizer[k] += weight * normalizer[k - 1];
            }
            double llr = Math.Log(Choose(pool, pick)) - Math.Log(normalizer[pick]);
            for (int i = 0; i < pool; i++)
            {
                if (!steps[t].Drawn[i]) continue;
                llr += beta * (s[i] - max);
            }
            result[t] = llr;
        }
        return result;
    }

    /// <summary>Fits the softmax temperature on the selection steps (concave 1-D maximisation
    /// by golden section), then scores the holdout steps at that temperature.</summary>
    public static (double Temperature, double LogScore, double Z) InformationTest(
        IReadOnlyList<InformationStep> selection, IReadOnlyList<InformationStep> holdout)
    {
        if (selection.Count == 0 || holdout.Count < 2) return (0, 0, 0);

        double lo = 0, hi = 4;
        const double phi = 0.6180339887498949;
        double a = hi - phi * (hi - lo), b = lo + phi * (hi - lo);
        double fa = LogLikelihoodRatios(selection, a).Average();
        double fb = LogLikelihoodRatios(selection, b).Average();
        for (int iter = 0; iter < 60; iter++)
        {
            if (fa > fb) { hi = b; b = a; fb = fa; a = hi - phi * (hi - lo); fa = LogLikelihoodRatios(selection, a).Average(); }
            else { lo = a; a = b; fa = fb; b = lo + phi * (hi - lo); fb = LogLikelihoodRatios(selection, b).Average(); }
        }
        double beta = (lo + hi) / 2;
        if (Math.Abs(beta) < 1e-6) beta = 0;

        var llr = LogLikelihoodRatios(holdout, beta);
        double mean = llr.Average();
        double var = llr.Sum(v => (v - mean) * (v - mean)) / (llr.Length - 1);
        double se = Math.Sqrt(var / llr.Length);
        return (beta, mean, se > 1e-12 ? mean / se : 0);
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

    public static double ThreePlusProbability(int poolSize, int pickCount) =>
        HypergeometricMatchDistribution(poolSize, pickCount).Skip(3).Sum();

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
        int threePlusHits = counts.Skip(3).Sum();
        var (ciLow, ciHigh) = WilsonInterval(threePlusHits, matches.Count);
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
            threePlusHits, matches.Count > 0 ? (double)threePlusHits / matches.Count : 0, ciLow, ciHigh);
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
