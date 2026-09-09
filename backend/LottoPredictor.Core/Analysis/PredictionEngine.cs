namespace LottoPredictor.Core.Analysis;

public sealed record ScoredNumber(NumberFeatures Features, double Score);

public sealed record PredictionResult(
    int[] Numbers,
    ScoringStrategy Strategy,
    IReadOnlyList<ScoredNumber> SelectedNumbers,
    double SetScore,
    double TypicalityPenalty,
    double PairSynergy);

/// <summary>Deterministic scoring engine. Scores every eligible ball number from a FeatureSet,
/// then searches combinations of the top-ranked numbers, adjusting for pair synergy and for
/// combinations that would be historically atypical (extreme sum, odd/even balance, range,
/// clustering). Atypical sets are penalised, never excluded.</summary>
public static class PredictionEngine
{
    private const int CandidatePoolSize = 18;

    /// <summary>Per-number scores under a strategy. Key = ball number.</summary>
    public static Dictionary<int, double> ScoreNumbers(FeatureSet fs, ScoringStrategy s)
    {
        var eligible = fs.Numbers.Where(f => f.EligibleDraws >= 10).ToArray();
        if (eligible.Length < fs.PickCount)
            eligible = fs.Numbers.Where(f => f.EligibleDraws > 0).ToArray();

        var zFreq = ZScores(eligible, f => f.FreqRateShrunk);
        var zRecent = ZScores(eligible, f => f.RecentRateShrunk);
        var zMomentum = ZScores(eligible, f => f.RecentVsLongTerm);
        var zBonus = ZScores(eligible, f => f.BonusRate);

        var scores = new Dictionary<int, double>(eligible.Length);
        for (int i = 0; i < eligible.Length; i++)
        {
            var f = eligible[i];
            double gapTerm = Math.Clamp(f.GapRatio - 1.0, -2.0, 2.0);
            scores[f.Number] =
                s.WLongTerm * zFreq[i] +
                s.WRecent * zRecent[i] +
                s.WGap * gapTerm +
                s.WMomentum * Math.Clamp(zMomentum[i], -3.0, 3.0) +
                s.WBias * Math.Clamp(f.BiasZ, -3.0, 3.0) +
                s.WBonus * Math.Clamp(zBonus[i], -3.0, 3.0);
        }
        return scores;
    }

    public static PredictionResult Generate(
        FeatureSet fs,
        ScoringStrategy strategy,
        IReadOnlySet<int>? excludedNumbers = null) =>
        GenerateFromScores(fs, ScoreNumbers(fs, strategy), strategy, excludedNumbers);

    /// <summary>Combination search over externally supplied per-number scores. Used directly by
    /// the hedge ensemble, which blends the score maps of several strategies.</summary>
    public static PredictionResult GenerateFromScores(
        FeatureSet fs,
        Dictionary<int, double> scores,
        ScoringStrategy strategy,
        IReadOnlySet<int>? excludedNumbers = null)
    {
        int pickCount = fs.PickCount;
        var eligibleScores = excludedNumbers is null || excludedNumbers.Count == 0
            ? scores
            : scores.Where(kv => !excludedNumbers.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (eligibleScores.Count < pickCount)
            throw new InvalidOperationException(
                $"Not enough eligible numbers to pick {pickCount} after exclusions.");

        // Deterministic ranking: score desc, then lower number first.
        var ranked = eligibleScores.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
            .Select(kv => kv.Key).ToArray();
        var candidates = ranked.Take(Math.Min(CandidatePoolSize, ranked.Length)).ToArray();

        int[]? best = null;
        double bestScore = double.NegativeInfinity;
        double bestPenalty = 0, bestSynergy = 0;

        foreach (var combo in Combinations(candidates.Length, pickCount))
        {
            var set = new int[pickCount];
            for (int i = 0; i < pickCount; i++) set[i] = candidates[combo[i]];
            Array.Sort(set);

            double numberScore = set.Sum(v => eligibleScores[v]);
            double synergy = PairSynergy(fs, set);
            double penalty = TypicalityPenalty(fs, set);
            double total = numberScore + strategy.PairWeight * synergy - strategy.PenaltyWeight * penalty;

            if (total > bestScore || (total == bestScore && best != null && CompareLex(set, best) < 0))
            {
                bestScore = total;
                best = set;
                bestPenalty = penalty;
                bestSynergy = synergy;
            }
        }

        var selected = best!.Select(v => new ScoredNumber(fs.For(v), eligibleScores[v])).ToList();
        return new PredictionResult(best!, strategy, selected, bestScore, bestPenalty, bestSynergy);
    }

    /// <summary>The top-ranked distinct lines under a strategy, best first. Same deterministic
    /// combination search as the single prediction, but keeping the N best sets.</summary>
    public static IReadOnlyList<PredictionResult> GenerateTopLines(
        FeatureSet fs,
        Dictionary<int, double> scores,
        ScoringStrategy strategy,
        int count,
        IReadOnlySet<int>? excludedNumbers = null)
    {
        int pickCount = fs.PickCount;
        var eligibleScores = excludedNumbers is null || excludedNumbers.Count == 0
            ? scores
            : scores.Where(kv => !excludedNumbers.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (eligibleScores.Count < pickCount)
            throw new InvalidOperationException(
                $"Not enough eligible numbers to pick {pickCount} after exclusions.");

        var ranked = eligibleScores.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
            .Select(kv => kv.Key).ToArray();
        var candidates = ranked.Take(Math.Min(CandidatePoolSize, ranked.Length)).ToArray();

        var all = new List<(int[] Set, double Total, double Penalty, double Synergy)>();
        foreach (var combo in Combinations(candidates.Length, pickCount))
        {
            var set = new int[pickCount];
            for (int i = 0; i < pickCount; i++) set[i] = candidates[combo[i]];
            Array.Sort(set);

            double numberScore = set.Sum(v => eligibleScores[v]);
            double synergy = PairSynergy(fs, set);
            double penalty = TypicalityPenalty(fs, set);
            all.Add((set, numberScore + strategy.PairWeight * synergy - strategy.PenaltyWeight * penalty,
                penalty, synergy));
        }

        return all
            .OrderByDescending(l => l.Total)
            .ThenBy(l => l.Set, Comparer<int[]>.Create(CompareLex))
            .Take(count)
            .Select(l => new PredictionResult(
                l.Set, strategy,
                l.Set.Select(v => new ScoredNumber(fs.For(v), eligibleScores[v])).ToList(),
                l.Total, l.Penalty, l.Synergy))
            .ToList();
    }

    /// <summary>Consensus line over a set of lines: the six numbers that appear in the most
    /// lines, tie-broken by per-number score. A different aggregation from the line ranking,
    /// so it can differ from line 1.</summary>
    public static (int[] Numbers, int[] Frequencies) Consensus(
        IReadOnlyList<PredictionResult> lines, Dictionary<int, double> scores)
    {
        var freq = new Dictionary<int, int>();
        foreach (var line in lines)
            foreach (var n in line.Numbers)
                freq[n] = freq.GetValueOrDefault(n) + 1;

        var chosen = freq
            .OrderByDescending(kv => kv.Value)
            .ThenByDescending(kv => scores.GetValueOrDefault(kv.Key))
            .ThenBy(kv => kv.Key)
            .Take(lines[0].Numbers.Length)
            .Select(kv => kv.Key)
            .OrderBy(n => n)
            .ToArray();
        return (chosen, chosen.Select(n => freq[n]).ToArray());
    }

    /// <summary>Standardises each strategy's score map to N(0,1) across numbers, then combines
    /// them with the given weights, so no single strategy dominates through raw scale.</summary>
    public static Dictionary<int, double> BlendScores(
        IEnumerable<(Dictionary<int, double> Scores, double Weight)> parts)
    {
        var blended = new Dictionary<int, double>();
        foreach (var (scores, weight) in parts)
        {
            if (scores.Count == 0 || weight <= 0) continue;
            double mean = scores.Values.Average();
            double std = Math.Sqrt(scores.Values.Sum(v => (v - mean) * (v - mean)) / scores.Count);
            foreach (var (number, score) in scores)
            {
                double z = std > 1e-9 ? (score - mean) / std : 0;
                blended[number] = blended.GetValueOrDefault(number) + weight * z;
            }
        }
        return blended;
    }

    /// <summary>Prediction from a weighted blend of several strategies (the hedge ensemble).</summary>
    public static PredictionResult GenerateEnsemble(
        FeatureSet fs,
        IReadOnlyList<ScoringStrategy> strategies,
        IReadOnlyDictionary<string, double> weights,
        string ensembleName,
        IReadOnlySet<int>? excludedNumbers = null)
    {
        double total = strategies.Sum(s => weights.GetValueOrDefault(s.Name));
        var parts = strategies
            .Select(s => (ScoreNumbers(fs, s), total > 0 ? weights.GetValueOrDefault(s.Name) / total : 1.0 / strategies.Count))
            .ToList();
        var blended = BlendScores(parts);
        double pairW = strategies.Sum(s => (total > 0 ? weights.GetValueOrDefault(s.Name) / total : 1.0 / strategies.Count) * s.PairWeight);
        double penW = strategies.Sum(s => (total > 0 ? weights.GetValueOrDefault(s.Name) / total : 1.0 / strategies.Count) * s.PenaltyWeight);
        var pseudo = new ScoringStrategy(ensembleName, 0, 0, 0, 0, pairW, penW);
        return GenerateFromScores(fs, blended, pseudo, excludedNumbers);
    }

    /// <summary>Mean clipped deviation of observed pair co-occurrence from uniform expectation.</summary>
    public static double PairSynergy(FeatureSet fs, int[] sortedSet)
    {
        double sum = 0;
        int pairs = 0;
        for (int a = 0; a < sortedSet.Length; a++)
            for (int b = a + 1; b < sortedSet.Length; b++)
            {
                int x = sortedSet[a], y = sortedSet[b];
                double observed = fs.PairCounts[x, y];
                double expected = fs.ExpectedPairCount(x, y);
                sum += Math.Clamp((observed - expected) / Math.Sqrt(expected + 1.0), -2.0, 2.0);
                pairs++;
            }
        return pairs > 0 ? sum / pairs : 0;
    }

    /// <summary>Soft penalty for combinations whose sum / odd-even balance / range / clustering
    /// would be historically extreme. Quadratic in the z-score, clipped, never infinite.</summary>
    public static double TypicalityPenalty(FeatureSet fs, int[] sortedSet)
    {
        double sum = sortedSet.Sum();
        double range = sortedSet[^1] - sortedSet[0];
        double odd = sortedSet.Count(v => v % 2 == 1);
        int consec = 0;
        for (int i = 0; i < sortedSet.Length - 1; i++)
            if (sortedSet[i + 1] == sortedSet[i] + 1) consec++;

        double zSum = SafeZ(sum, fs.SumMean, fs.SumStd);
        double zRange = SafeZ(range, fs.RangeMean, fs.RangeStd);
        double zOdd = SafeZ(odd, fs.OddMean, fs.OddStd);
        double zConsec = SafeZ(consec, fs.ConsecMean, fs.ConsecStd);

        return 0.35 * zSum * zSum + 0.20 * zRange * zRange + 0.25 * zOdd * zOdd + 0.20 * zConsec * zConsec;
    }

    private static double SafeZ(double value, double mean, double std) =>
        std > 1e-9 ? Math.Clamp((value - mean) / std, -3.0, 3.0) : 0.0;

    private static int CompareLex(int[] a, int[] b)
    {
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return 0;
    }

    private static double[] ZScores(NumberFeatures[] items, Func<NumberFeatures, double> selector)
    {
        var values = items.Select(selector).ToArray();
        double mean = values.Average();
        double var = values.Length > 1
            ? values.Sum(v => (v - mean) * (v - mean)) / (values.Length - 1)
            : 0;
        double std = Math.Sqrt(var);
        return values.Select(v => std > 1e-9 ? (v - mean) / std : 0.0).ToArray();
    }

    private static IEnumerable<int[]> Combinations(int n, int k)
    {
        var idx = new int[k];
        for (int i = 0; i < k; i++) idx[i] = i;
        while (true)
        {
            yield return idx;
            int pos = k - 1;
            while (pos >= 0 && idx[pos] == n - k + pos) pos--;
            if (pos < 0) yield break;
            idx[pos]++;
            for (int i = pos + 1; i < k; i++) idx[i] = idx[i - 1] + 1;
        }
    }
}
