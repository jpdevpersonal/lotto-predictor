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
    public static Dictionary<int, double> ScoreNumbers(FeatureSet fs, ScoringStrategy s) =>
        ScoreNumbers(FeatureMatrix(fs), s);

    /// <summary>Per-number scores from a precomputed feature matrix. Key = ball number.</summary>
    public static Dictionary<int, double> ScoreNumbers((int[] Numbers, double[][] X) matrix, ScoringStrategy s)
    {
        var (numbers, x) = matrix;
        double w0 = s.WLongTerm, w1 = s.WRecent, w2 = s.WGap,
               w3 = s.WMomentum, w4 = s.WBias, w5 = s.WBonus;
        var scores = new Dictionary<int, double>(numbers.Length);
        for (int i = 0; i < numbers.Length; i++)
        {
            var xi = x[i];
            scores[numbers[i]] = w0 * xi[0] + w1 * xi[1] + w2 * xi[2] +
                                 w3 * xi[3] + w4 * xi[4] + w5 * xi[5];
        }
        return scores;
    }

    public const int FeatureCount = 6;

    /// <summary>Order of the columns returned by <see cref="FeatureMatrix"/>; it matches the
    /// strategy weights long-term, recent, gap, momentum, bias, bonus.</summary>
    public static IReadOnlyList<string> FeatureNames { get; } =
        ["long-term", "recent", "gap", "momentum", "bias", "bonus"];

    /// <summary>The standardised per-ball feature vectors every strategy scores against.</summary>
    public static (int[] Numbers, double[][] X) FeatureMatrix(FeatureSet fs)
    {
        var eligible = fs.Numbers.Where(f => f.Number <= fs.Pool.NextPoolSize).ToArray();

        var zFreq = ZScores(eligible, f => f.FreqRateShrunk);
        var zRecent = ZScores(eligible, f => 0.70 * f.RecentRateShrunk + 0.30 * f.EwmaRate);
        var rawMomentum = ZScores(eligible, f => f.RecentVsLongTerm);
        var orthoMomentum = Orthogonalise(rawMomentum, zRecent, zFreq);
        bool hasMainBonus = false;
        for (int i = 0; i < eligible.Length; i++)
        {
            if (eligible[i].BonusCount > 0) { hasMainBonus = true; break; }
        }
        var zBonus = hasMainBonus
            ? ZScores(eligible, f => f.BonusRate)
            : ZScores(eligible, f => f.SecondarySignal);

        var numbers = new int[eligible.Length];
        var x = new double[eligible.Length][];
        for (int i = 0; i < eligible.Length; i++)
        {
            var f = eligible[i];
            numbers[i] = f.Number;
            double gapSignal = Math.Clamp(0.75 * (f.GapRatio - 1.0) + 0.25 * f.GapHazardZ, -2.0, 2.0);
            double momentumSignal = Math.Clamp(0.65 * orthoMomentum[i] + 0.35 * rawMomentum[i], -3.0, 3.0);
            double softBias = SoftThreshold(f.BiasZ, 0.35) + 0.45 * f.PhysicalAffinity;
            double bonusOrSecondary = hasMainBonus
                ? zBonus[i] + 0.25 * f.PhysicalAffinity
                : zBonus[i];

            x[i] =
            [
                zFreq[i],
                zRecent[i],
                gapSignal,
                momentumSignal,
                Math.Clamp(softBias, -3.0, 3.0),
                Math.Clamp(bonusOrSecondary, -3.0, 3.0),
            ];
        }
        return (numbers, x);
    }

    private static double SoftThreshold(double z, double threshold) =>
        Math.Abs(z) <= threshold ? 0.25 * z : Math.CopySign(Math.Abs(z) - 0.75 * threshold, z);

    /// <summary>Projects out the components of <paramref name="target"/> that are collinear with
    /// <paramref name="basis1"/> and <paramref name="basis2"/>, then re-standardises to unit variance.</summary>
    private static double[] Orthogonalise(double[] target, double[] basis1, double[] basis2)
    {
        int n = target.Length;
        if (n <= 2) return target;
        var res = (double[])target.Clone();
        ProjectOut(res, basis1);
        ProjectOut(res, basis2);
        double mean = 0;
        for (int i = 0; i < n; i++) mean += res[i];
        mean /= n;
        double var = 0;
        for (int i = 0; i < n; i++)
        {
            double d = res[i] - mean;
            var += d * d;
        }
        double std = Math.Sqrt(var / (n - 1));
        if (std <= 1e-9) return target;
        for (int i = 0; i < n; i++) res[i] = (res[i] - mean) / std;
        return res;
    }

    private static void ProjectOut(double[] target, double[] basis)
    {
        double dot = 0, norm = 0;
        for (int i = 0; i < target.Length; i++)
        {
            dot += target[i] * basis[i];
            norm += basis[i] * basis[i];
        }
        if (norm <= 1e-9) return;
        double coeff = dot / norm;
        for (int i = 0; i < target.Length; i++)
            target[i] -= coeff * basis[i];
    }

    public static PredictionResult Generate(
        FeatureSet fs,
        ScoringStrategy strategy,
        IReadOnlySet<int>? excludedNumbers = null) =>
        GenerateFromScores(fs, ScoreNumbers(fs, strategy), strategy, excludedNumbers);

    /// <summary>Combination search over externally supplied per-number scores. Used directly by
    /// the hedge ensemble, which blends the score maps of several strategies. Searches all
    /// combinations of the top candidate pool, then applies 1-opt full-pool ball swap refinement
    /// so balls outside the top 18 with strong pair synergy or typicality balance can be chosen.</summary>
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
        int candidateCount = Math.Min(CandidatePoolSize, ranked.Length);
        var candidates = ranked.Take(candidateCount).ToArray();
        Array.Sort(candidates); // Sorted ascending so every generated combination is already sorted.

        var candScores = new double[candidateCount];
        for (int i = 0; i < candidateCount; i++)
            candScores[i] = eligibleScores[candidates[i]];

        var synergyMatrix = fs.GetPairSynergyMatrix();
        double pairFactor = pickCount > 1 ? 2.0 / (pickCount * (pickCount - 1)) : 0.0;
        double pairW = strategy.PairWeight;
        double penW = strategy.PenaltyWeight;

        int[] best = new int[pickCount];
        var work = new int[pickCount];
        double bestScore = double.NegativeInfinity;
        double bestPenalty = 0, bestSynergy = 0;

        foreach (var combo in Combinations(candidateCount, pickCount))
        {
            double numberScore = 0;
            for (int i = 0; i < pickCount; i++)
            {
                int cIdx = combo[i];
                work[i] = candidates[cIdx];
                numberScore += candScores[cIdx];
            }

            double synergy = 0;
            if (pairFactor > 0)
            {
                for (int a = 0; a < pickCount; a++)
                {
                    int va = work[a];
                    for (int b = a + 1; b < pickCount; b++)
                        synergy += synergyMatrix[va, work[b]];
                }
                synergy *= pairFactor;
            }

            double penalty = TypicalityPenalty(fs, work);
            double total = numberScore + pairW * synergy - penW * penalty;

            if (total > bestScore || (total == bestScore && CompareLex(work, best) < 0))
            {
                bestScore = total;
                Array.Copy(work, best, pickCount);
                bestPenalty = penalty;
                bestSynergy = synergy;
            }
        }

        // 1-opt full-pool ball swap refinement: allow balls ranked beyond CandidatePoolSize
        // to enter the line when pair synergy or typicality balance improves total set score.
        if (ranked.Length > candidateCount && (pairW > 0 || penW > 0))
        {
            (best, bestScore, bestPenalty, bestSynergy) = RefineWithFullPoolSwaps(
                fs, best, bestScore, bestPenalty, bestSynergy,
                ranked, candidateCount, eligibleScores, strategy);
        }

        var selected = best.Select(v => new ScoredNumber(fs.For(v), eligibleScores[v])).ToList();
        return new PredictionResult(best, strategy, selected, bestScore, bestPenalty, bestSynergy);
    }

    private static (int[] Set, double Score, double Penalty, double Synergy) RefineWithFullPoolSwaps(
        FeatureSet fs,
        int[] currentSet,
        double currentScore,
        double currentPenalty,
        double currentSynergy,
        int[] ranked,
        int startPoolIndex,
        Dictionary<int, double> eligibleScores,
        ScoringStrategy strategy)
    {
        int pickCount = currentSet.Length;
        var best = (int[])currentSet.Clone();
        double bestScore = currentScore;
        double bestPenalty = currentPenalty;
        double bestSynergy = currentSynergy;
        var trial = new int[pickCount];

        for (int pass = 0; pass < pickCount; pass++)
        {
            bool improved = false;
            for (int r = startPoolIndex; r < ranked.Length; r++)
            {
                int challenger = ranked[r];
                if (Array.BinarySearch(best, challenger) >= 0) continue;

                for (int pos = 0; pos < pickCount; pos++)
                {
                    Array.Copy(best, trial, pickCount);
                    trial[pos] = challenger;
                    Array.Sort(trial);

                    double numScore = 0;
                    for (int i = 0; i < pickCount; i++) numScore += eligibleScores[trial[i]];
                    double syn = PairSynergy(fs, trial);
                    double pen = TypicalityPenalty(fs, trial);
                    double total = numScore + strategy.PairWeight * syn - strategy.PenaltyWeight * pen;

                    if (total > bestScore + 1e-12 ||
                        (Math.Abs(total - bestScore) <= 1e-12 && CompareLex(trial, best) < 0))
                    {
                        bestScore = total;
                        bestPenalty = pen;
                        bestSynergy = syn;
                        Array.Copy(trial, best, pickCount);
                        improved = true;
                    }
                }
            }
            if (!improved) break;
        }

        return (best, bestScore, bestPenalty, bestSynergy);
    }

    /// <summary>The top-ranked distinct lines under a strategy, best first. Same deterministic
    /// combination search as the single prediction, plus full-pool 1-opt swap candidates from the
    /// leading seeds so balls outside the top 18 can participate in top lines and consensus.</summary>
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
        int candidateCount = Math.Min(CandidatePoolSize, ranked.Length);
        var candidates = ranked.Take(candidateCount).ToArray();
        Array.Sort(candidates);

        var byKey = new Dictionary<string, (int[] Set, double Total, double Penalty, double Synergy)>();
        foreach (var combo in Combinations(candidates.Length, pickCount))
        {
            var set = new int[pickCount];
            double numberScore = 0;
            for (int i = 0; i < pickCount; i++)
            {
                int v = candidates[combo[i]];
                set[i] = v;
                numberScore += eligibleScores[v];
            }

            double synergy = PairSynergy(fs, set);
            double penalty = TypicalityPenalty(fs, set);
            double total = numberScore + strategy.PairWeight * synergy - strategy.PenaltyWeight * penalty;
            byKey[string.Join(',', set)] = (set, total, penalty, synergy);
        }

        if (ranked.Length > candidateCount && (strategy.PairWeight > 0 || strategy.PenaltyWeight > 0))
        {
            var topSeeds = byKey.Values
                .OrderByDescending(l => l.Total)
                .ThenBy(l => l.Set, Comparer<int[]>.Create(CompareLex))
                .Take(Math.Min(12, Math.Max(4, count / 4)))
                .ToList();

            foreach (var seed in topSeeds)
            {
                var refined = RefineWithFullPoolSwaps(
                    fs, seed.Set, seed.Total, seed.Penalty, seed.Synergy,
                    ranked, candidateCount, eligibleScores, strategy);
                byKey[string.Join(',', refined.Set)] = refined;
            }
        }

        return byKey.Values
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
        var (blended, pseudo) = EnsembleScores(fs, strategies, weights, ensembleName);
        return GenerateFromScores(fs, blended, pseudo, excludedNumbers);
    }

    /// <summary>Blended score map plus the weight-averaged pair and penalty settings.</summary>
    public static (Dictionary<int, double> Scores, ScoringStrategy Strategy) EnsembleScores(
        FeatureSet fs,
        IReadOnlyList<ScoringStrategy> strategies,
        IReadOnlyDictionary<string, double> weights,
        string ensembleName)
    {
        double total = strategies.Sum(s => weights.GetValueOrDefault(s.Name));
        double WeightOf(ScoringStrategy s) =>
            total > 0 ? weights.GetValueOrDefault(s.Name) / total : 1.0 / strategies.Count;
        var parts = strategies.Select(s => (ScoreNumbers(fs, s), WeightOf(s))).ToList();
        var blended = BlendScores(parts);
        double pairW = strategies.Sum(s => WeightOf(s) * s.PairWeight);
        double penW = strategies.Sum(s => WeightOf(s) * s.PenaltyWeight);
        return (blended, new ScoringStrategy(ensembleName, 0, 0, 0, 0, pairW, penW));
    }

    /// <summary>Mean clipped deviation of observed pair co-occurrence from uniform expectation.</summary>
    public static double PairSynergy(FeatureSet fs, int[] sortedSet)
    {
        int len = sortedSet.Length;
        if (len < 2) return 0;
        var matrix = fs.GetPairSynergyMatrix();
        double sum = 0;
        int pairs = 0;
        for (int a = 0; a < len; a++)
        {
            int x = sortedSet[a];
            for (int b = a + 1; b < len; b++)
            {
                sum += matrix[x, sortedSet[b]];
                pairs++;
            }
        }
        return pairs > 0 ? sum / pairs : 0;
    }

    /// <summary>Soft penalty for combinations whose sum / odd-even balance / range / clustering
    /// would be historically extreme. Quadratic in the z-score, clipped, never infinite.</summary>
    public static double TypicalityPenalty(FeatureSet fs, int[] sortedSet)
    {
        int len = sortedSet.Length;
        if (len == 0) return 0;
        int sumInt = 0, oddInt = 0, consec = 0;
        for (int i = 0; i < len; i++)
        {
            int v = sortedSet[i];
            sumInt += v;
            if ((v & 1) == 1) oddInt++;
            if (i < len - 1 && sortedSet[i + 1] == v + 1) consec++;
        }
        double sum = sumInt;
        double range = sortedSet[len - 1] - sortedSet[0];
        double odd = oddInt;

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
