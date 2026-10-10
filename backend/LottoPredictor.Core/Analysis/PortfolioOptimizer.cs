namespace LottoPredictor.Core.Analysis;

public sealed record PortfolioLine(
    int Rank,
    int[] Numbers,
    double Score,
    /// <summary>Largest number of balls this line shares with any earlier line.</summary>
    int MaxOverlapWithEarlier);

public sealed record PortfolioSimulation(
    int Trials,
    int Hits,
    double Probability,
    double CiLow,
    double CiHigh);

/// <summary>Analytic odds of a fixed portfolio for one uniform draw.</summary>
public sealed record PortfolioOdds(
    double SingleLineFourPlusProbability,
    /// <summary>P(at least one line matches 4+). Exact when <see cref="IsExact"/>; otherwise the
    /// second-order inclusion–exclusion value, which is a tight lower bound.</summary>
    double FourPlusProbability,
    bool IsExact,
    int MaxPairwiseOverlap);

public sealed record PortfolioResult(
    IReadOnlyList<PortfolioLine> Lines,
    PortfolioOdds Odds,
    PortfolioSimulation CoverageOptimized,
    PortfolioSimulation RandomDistinct,
    double SingleLineFourPlusProbability,
    string Objective);

/// <summary>Builds K lines that maximise P(at least one line matches 4+ main numbers) for a
/// uniform draw. Two lines can both match 4+ in the same 6-ball draw only when they share at
/// least two balls, so lines that pairwise share at most one ball make the 4+ events mutually
/// exclusive and the portfolio hits the union bound K·p exactly — the best any K lines can do.
/// When the pool can no longer supply such lines the constraint relaxes one ball at a time.
/// Model scores only break ties between equally valid lines; they cannot raise the odds.</summary>
public static class PortfolioOptimizer
{
    private const int ConstructionsPerLine = 400;
    private const int SimulationTrials = 100000;

    public static PortfolioResult BuildCoveragePortfolio(
        FeatureSet fs,
        Dictionary<int, double> scores,
        ScoringStrategy strategy,
        int lineCount,
        IReadOnlySet<int>? excludedNumbers = null,
        int randomSeed = 20260914)
    {
        if (lineCount < 1) throw new ArgumentOutOfRangeException(nameof(lineCount));

        int pickCount = fs.PickCount;
        int poolSize = fs.Pool.NextPoolSize;
        var legalNumbers = Enumerable.Range(1, poolSize)
            .Where(number => excludedNumbers is null || !excludedNumbers.Contains(number))
            .ToArray();
        if (legalNumbers.Length < pickCount)
            throw new InvalidOperationException($"Pool 1-{poolSize} cannot supply {pickCount} numbers.");
        if (CombinationCount(legalNumbers.Length, pickCount) < lineCount)
            throw new InvalidOperationException(
                $"Only {CombinationCount(legalNumbers.Length, pickCount)} distinct lines are available for the requested portfolio.");

        var rng = new Random(randomSeed + lineCount * 17 + poolSize * 31 + pickCount);
        var selected = new List<PortfolioLine>(lineCount);
        var selectedSets = new List<int[]>(lineCount);
        var selectedKeys = new HashSet<string>();
        var packing = new Packing(pickCount, poolSize);
        // Exact probability that a draw gives 4+ to both of two lines sharing s balls: the loss
        // each new line inflicts on the union bound, per earlier line it overlaps with.
        var pairTerm = Enumerable.Range(0, pickCount + 1)
            .Select(s => PairwiseFourPlusProbability(poolSize, pickCount, s)).ToArray();

        while (selected.Count < lineCount)
        {
            int[]? best = null;
            double bestLoss = double.PositiveInfinity;
            double bestScore = double.NegativeInfinity;
            // Tier t admits lines sharing at most t balls with every earlier line.
            for (int tier = 1; tier < pickCount && best is null; tier++)
            {
                for (int attempt = 0; attempt < ConstructionsPerLine; attempt++)
                {
                    var line = packing.Construct(legalNumbers, scores, rng, tier, attempt);
                    if (line is null || selectedKeys.Contains(LineKey(line))) continue;
                    double loss = packing.OverlapLoss(line, pairTerm);
                    if (best is not null && loss > bestLoss) continue;
                    double score = LineScore(fs, line, scores, strategy);
                    if (best is null || loss < bestLoss || score > bestScore ||
                        (score == bestScore && PredictionLexCompare(line, best) < 0))
                    {
                        best = line;
                        bestLoss = loss;
                        bestScore = score;
                    }
                }
            }

            if (best is null)
            {
                // Tiny pools: fall back to any unused distinct line.
                best = Combinations(legalNumbers.Length, pickCount)
                    .Select(combo => combo.Select(i => legalNumbers[i]).ToArray())
                    .First(line => !selectedKeys.Contains(LineKey(line)));
                bestScore = LineScore(fs, best, scores, strategy);
            }

            int overlap = selectedSets.Count == 0 ? 0 : selectedSets.Max(s => s.Intersect(best).Count());
            packing.Add(best);
            selectedSets.Add(best);
            selectedKeys.Add(LineKey(best));
            selected.Add(new PortfolioLine(selected.Count + 1, best, Math.Round(bestScore, 4), overlap));
        }

        var randomPortfolio = DistinctRandomLines(
            new Random(randomSeed + 991), legalNumbers, pickCount, lineCount);
        double single = Backtester.FourPlusProbability(poolSize, pickCount);
        return new PortfolioResult(
            selected,
            ComputeOdds(selectedSets, poolSize, pickCount),
            Simulate(selectedSets, poolSize, pickCount, randomSeed + 1009),
            Simulate(randomPortfolio, poolSize, pickCount, randomSeed + 2003),
            single,
            $"P(at least one of K={lineCount} fixed lines matches at least four main numbers in a round)");
    }

    /// <summary>P(at least one line matches 4+) by inclusion–exclusion truncated at pairs.
    /// Exact whenever no two lines share enough balls for one draw to give both 4+ matches.</summary>
    public static PortfolioOdds ComputeOdds(IReadOnlyList<int[]> lines, int poolSize, int pickCount)
    {
        double single = Backtester.FourPlusProbability(poolSize, pickCount);
        var pairTerm = new double?[pickCount + 1];
        double total = lines.Count * single;
        int maxOverlap = 0;
        for (int i = 0; i < lines.Count; i++)
            for (int j = i + 1; j < lines.Count; j++)
            {
                int shared = lines[i].Intersect(lines[j]).Count();
                maxOverlap = Math.Max(maxOverlap, shared);
                pairTerm[shared] ??= PairwiseFourPlusProbability(poolSize, pickCount, shared);
                total -= pairTerm[shared]!.Value;
            }

        // Two lines can only both match 4+ of the same pickCount balls if they share 8 - pickCount.
        bool exact = lines.Count < 2 || maxOverlap < Math.Max(0, 8 - pickCount);
        return new PortfolioOdds(single, Math.Max(0, total), exact, maxOverlap);
    }

    /// <summary>P(two specific lines that share <paramref name="shared"/> balls both match 4+
    /// main numbers in one uniform draw of <paramref name="pickCount"/> balls).</summary>
    public static double PairwiseFourPlusProbability(int poolSize, int pickCount, int shared)
    {
        int only = pickCount - shared;          // balls unique to each line
        int outside = poolSize - 2 * pickCount + shared;
        double count = 0;
        for (int a = 0; a <= shared; a++)
            for (int b = 0; b <= only; b++)
                for (int c = 0; c <= only; c++)
                {
                    int o = pickCount - a - b - c;
                    if (o < 0 || a + b < 4 || a + c < 4) continue;
                    count += Choose(shared, a) * Choose(only, b) * Choose(only, c) * Choose(outside, o);
                }
        return count / Choose(poolSize, pickCount);
    }

    /// <summary>Lines needed so P(at least one 4+ match on a draw night with
    /// <paramref name="rounds"/> independent rounds) reaches <paramref name="target"/>,
    /// assuming the optimal (pairwise overlap ≤ 1) design.</summary>
    public static int LinesForTarget(double target, double singleLineProbability, int rounds)
    {
        if (target <= 0) return 0;
        if (target >= 1) return int.MaxValue;
        double perRound = 1 - Math.Pow(1 - target, 1.0 / Math.Max(1, rounds));
        return (int)Math.Ceiling(perRound / singleLineProbability - 1e-9);
    }

    public static PortfolioSimulation Simulate(
        IReadOnlyList<int[]> portfolio,
        int poolSize,
        int pickCount,
        int randomSeed = 20260914,
        int trials = SimulationTrials)
    {
        var rng = new Random(randomSeed);
        int hits = 0;
        for (int i = 0; i < trials; i++)
        {
            var draw = RandomSet(rng, poolSize, pickCount);
            if (portfolio.Any(line => Backtester.CountMatches(line, draw) >= 4)) hits++;
        }

        var (low, high) = Backtester.WilsonInterval(hits, trials);
        return new PortfolioSimulation(trials, hits, (double)hits / trials, low, high);
    }

    /// <summary>Tracks which 2-, 3-, ... subsets of balls are already used by selected lines and
    /// builds new lines ball by ball so that no forbidden subset is reused.</summary>
    private sealed class Packing(int pickCount, int poolSize)
    {
        private const double UsagePenalty = 0.35;

        private readonly HashSet<long>[] used = Enumerable.Range(0, pickCount + 1)
            .Select(_ => new HashSet<long>()).ToArray();
        private readonly List<bool[]> presence = [];
        private readonly int[] uses = new int[poolSize + 1];

        public void Add(int[] line)
        {
            for (int size = 2; size <= pickCount; size++)
                foreach (var key in SubsetKeys(line, size)) used[size].Add(key);
            var mask = new bool[poolSize + 1];
            foreach (var n in line)
            {
                mask[n] = true;
                uses[n]++;
            }
            presence.Add(mask);
        }

        /// <summary>Sum over earlier lines of P(this line and that line both match 4+).</summary>
        public double OverlapLoss(int[] line, double[] pairTerm)
        {
            double loss = 0;
            foreach (var mask in presence)
            {
                int overlap = 0;
                foreach (var n in line) if (mask[n]) overlap++;
                loss += pairTerm[overlap];
            }
            return loss;
        }

        /// <summary>One randomised greedy construction: walk the balls in a score-leaning random
        /// order (penalising over-used balls) and keep every ball that does not complete a used
        /// (tier+1)-subset.</summary>
        public int[]? Construct(int[] legal, Dictionary<int, double> scores, Random rng, int tier, int attempt)
        {
            int subsetSize = tier + 1;
            // Attempt 0 is pure score order (the model's preferred line); later attempts add noise.
            double temperature = attempt == 0 ? 0 : 0.5 + 2.5 * attempt / ConstructionsPerLine;
            double meanUses = presence.Count * (double)pickCount / legal.Length;
            var order = legal
                .Select(n => (n, key: scores.GetValueOrDefault(n) + temperature * Gumbel(rng) -
                                     (attempt == 0 ? 0 : UsagePenalty * (uses[n] - meanUses))))
                .OrderByDescending(t => t.key).ThenBy(t => t.n)
                .Select(t => t.n).ToArray();

            var line = new List<int>(pickCount);
            foreach (var n in order)
            {
                if (line.Count + 1 < subsetSize || Compatible(line, n, subsetSize))
                    line.Add(n);
                if (line.Count == pickCount) break;
            }
            if (line.Count < pickCount) return null;
            line.Sort();
            return [.. line];
        }

        private bool Compatible(List<int> partial, int candidate, int subsetSize)
        {
            // Every (subsetSize-1)-subset of the partial line, plus the candidate, must be unused.
            foreach (var combo in Combinations(partial.Count, subsetSize - 1))
            {
                var subset = new int[subsetSize];
                for (int i = 0; i < subsetSize - 1; i++) subset[i] = partial[combo[i]];
                subset[^1] = candidate;
                Array.Sort(subset);
                if (used[subsetSize].Contains(Encode(subset))) return false;
            }
            return true;
        }

        private static IEnumerable<long> SubsetKeys(int[] sortedLine, int size)
        {
            foreach (var combo in Combinations(sortedLine.Length, size))
            {
                var subset = new int[size];
                for (int i = 0; i < size; i++) subset[i] = sortedLine[combo[i]];
                yield return Encode(subset);
            }
        }

        private static long Encode(int[] sortedSubset)
        {
            long key = sortedSubset.Length;
            foreach (var n in sortedSubset) key = key * 128 + n;
            return key;
        }

        private static double Gumbel(Random rng) => -Math.Log(-Math.Log(1 - rng.NextDouble()));
    }

    private static double LineScore(
        FeatureSet fs,
        int[] set,
        Dictionary<int, double> scores,
        ScoringStrategy strategy)
    {
        double numberScore = set.Sum(number => scores.GetValueOrDefault(number));
        return numberScore + strategy.PairWeight * PredictionEngine.PairSynergy(fs, set) -
            strategy.PenaltyWeight * PredictionEngine.TypicalityPenalty(fs, set);
    }

    private static IReadOnlyList<int[]> DistinctRandomLines(Random rng, int[] legalNumbers, int pickCount, int count)
    {
        var lines = new List<int[]>(count);
        var keys = new HashSet<string>();
        while (lines.Count < count)
        {
            var line = RandomSet(rng, legalNumbers, pickCount);
            if (keys.Add(LineKey(line))) lines.Add(line);
        }
        return lines;
    }

    private static long CombinationCount(int n, int k)
    {
        k = Math.Min(k, n - k);
        long result = 1;
        for (int i = 1; i <= k; i++)
        {
            result = result * (n - k + i) / i;
            if (result > int.MaxValue) return int.MaxValue;
        }
        return result;
    }

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
        return [.. set.OrderBy(number => number)];
    }

    private static int[] RandomSet(Random rng, int[] legalNumbers, int pickCount)
    {
        var set = new HashSet<int>();
        while (set.Count < pickCount) set.Add(legalNumbers[rng.Next(legalNumbers.Length)]);
        return [.. set.OrderBy(number => number)];
    }

    private static string LineKey(int[] sortedSet) => string.Join(',', sortedSet);

    private static int PredictionLexCompare(int[] a, int[] b)
    {
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return 0;
    }

    private static IEnumerable<int[]> Combinations(int n, int k)
    {
        if (k > n) yield break;
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
