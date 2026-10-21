namespace LottoPredictor.Core.Analysis;

public sealed record PortfolioLine(
    int Rank,
    int[] Numbers,
    double Score,
    /// <summary>Largest number of balls this line shares with any earlier line.</summary>
    int MaxOverlapWithEarlier);

/// <summary>Hit count over <see cref="Trials"/> draws. Portfolio results enumerate every possible
/// draw, so the interval collapses to the exact probability; <see cref="PortfolioOptimizer.Simulate"/>
/// still returns Monte Carlo estimates with a Wilson interval.</summary>
public sealed record PortfolioSimulation(
    int Trials,
    long Hits,
    double Probability,
    double CiLow,
    double CiHigh,
    /// <summary>True when every possible draw was enumerated, so the probability is exact.</summary>
    bool Exhaustive = false);

/// <summary>Odds of a fixed portfolio for one uniform draw.</summary>
public sealed record PortfolioOdds(
    double SingleLineThreePlusProbability,
    /// <summary>P(at least one line matches 3+). Exact when <see cref="IsExact"/>; otherwise
    /// a reproducible Monte Carlo estimate.</summary>
    double ThreePlusProbability,
    bool IsExact,
    int MaxPairwiseOverlap);

public sealed record PortfolioResult(
    IReadOnlyList<PortfolioLine> Lines,
    PortfolioOdds Odds,
    PortfolioSimulation CoverageOptimized,
    PortfolioSimulation RandomDistinct,
    double SingleLineThreePlusProbability,
    string Objective);

/// <summary>Builds K lines that maximise P(at least one line matches 3+ main numbers) for a
/// uniform draw. A greedy pairwise intersection-loss heuristic builds a first portfolio, then a
/// seeded local search swaps single balls to cut the pairwise loss further; the version with the
/// higher exact probability (counted over every possible draw) is kept. Even disjoint lines can
/// both match 3+ in a six-ball draw, so K·p is generally only an upper bound, not achievable odds.
/// When the pool can no longer supply low-overlap lines the constraint relaxes one ball at a time.
/// Model scores only break ties between equally valid lines; they cannot raise the odds.</summary>
public static class PortfolioOptimizer
{
    private const int ConstructionsPerLine = 400;
    private const int SimulationTrials = 100000;
    private const int RefinementStepsPerLine = 4000;
    private const int RefinementBaseSteps = 20000;
    /// <summary>Exact enumeration packs balls into 64-bit masks.</summary>
    private const int MaxExactPoolSize = 63;

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
        // Exact probability that a draw gives 3+ to both of two lines sharing s balls: the loss
        // each new line inflicts on the union bound, per earlier line it overlaps with.
        var pairTerm = Enumerable.Range(0, pickCount + 1)
            .Select(s => PairwiseThreePlusProbability(poolSize, pickCount, s)).ToArray();

        while (selected.Count < lineCount)
        {
            int[]? best = null;
            double bestLoss = double.PositiveInfinity;
            double bestScore = double.NegativeInfinity;
            // Tier t admits lines sharing at most t balls with every earlier line.
            for (int tier = 0; tier < pickCount && best is null; tier++)
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

            packing.Add(best);
            selectedSets.Add(best);
            selectedKeys.Add(LineKey(best));
            selected.Add(new PortfolioLine(selected.Count + 1, best, Math.Round(bestScore, 4), 0));
        }

        var finalSets = selectedSets;
        var coverage = Evaluate(finalSets, poolSize, pickCount, randomSeed);
        var refined = Refine(selectedSets, legalNumbers, pickCount, pairTerm,
            new Random(randomSeed + 4057 + lineCount));
        if (refined is not null)
        {
            var refinedCoverage = Evaluate(refined, poolSize, pickCount, randomSeed);
            if (refinedCoverage.Hits > coverage.Hits)
            {
                finalSets = refined;
                coverage = refinedCoverage;
            }
        }

        var lines = finalSets.Select((line, index) => new PortfolioLine(
            index + 1,
            line,
            ReferenceEquals(finalSets, selectedSets)
                ? selected[index].Score
                : Math.Round(LineScore(fs, line, scores, strategy), 4),
            index == 0 ? 0 : finalSets.Take(index).Max(earlier => earlier.Intersect(line).Count())))
            .ToList();

        var randomPortfolio = DistinctRandomLines(
            new Random(randomSeed + 991), legalNumbers, pickCount, lineCount);
        double single = Backtester.ThreePlusProbability(poolSize, pickCount);
        return new PortfolioResult(
            lines,
            OddsCore(finalSets, poolSize, pickCount, coverage),
            coverage,
            Evaluate(randomPortfolio, poolSize, pickCount, randomSeed + 2003),
            single,
            $"P(at least one of K={lineCount} fixed lines matches at least three main numbers in a round)");
    }

    /// <summary>Exact whenever the pool fits a 64-bit mask (every supported game), by counting
    /// every possible draw (or reusing an exhaustive <paramref name="simulation"/>). Larger pools
    /// fall back to the closed form for one/two lines or mutually exclusive hit events, and
    /// otherwise to <paramref name="simulation"/>.</summary>
    public static PortfolioOdds ComputeOdds(
        IReadOnlyList<int[]> lines, int poolSize, int pickCount, PortfolioSimulation? simulation = null) =>
        OddsCore(lines, poolSize, pickCount, simulation);

    private static PortfolioOdds OddsCore(
        IReadOnlyList<int[]> lines, int poolSize, int pickCount, PortfolioSimulation? simulation)
    {
        double single = Backtester.ThreePlusProbability(poolSize, pickCount);
        var pairTerm = new double?[pickCount + 1];
        double total = lines.Count * single;
        int maxOverlap = 0;
        for (int i = 0; i < lines.Count; i++)
            for (int j = i + 1; j < lines.Count; j++)
            {
                int shared = lines[i].Intersect(lines[j]).Count();
                maxOverlap = Math.Max(maxOverlap, shared);
                pairTerm[shared] ??= PairwiseThreePlusProbability(poolSize, pickCount, shared);
                total -= pairTerm[shared]!.Value;
            }

        if (poolSize <= MaxExactPoolSize)
        {
            double exactProbability = simulation is { Exhaustive: true }
                ? simulation.Probability
                : ExactThreePlusProbability(lines, poolSize, pickCount);
            return new PortfolioOdds(single, exactProbability, true, maxOverlap);
        }

        bool exact = lines.Count <= 2 || maxOverlap < Math.Max(0, 6 - pickCount);
        double probability = exact ? Math.Clamp(total, 0, 1) :
            (simulation ?? Simulate(lines, poolSize, pickCount)).Probability;
        return new PortfolioOdds(single, probability, exact, maxOverlap);
    }

    /// <summary>Exact P(at least one line matches 3+) for one uniform draw, counted over every
    /// possible draw.</summary>
    public static double ExactThreePlusProbability(IReadOnlyList<int[]> lines, int poolSize, int pickCount)
    {
        var (hits, total) = CountThreePlusDraws(lines, poolSize, pickCount);
        return total == 0 ? 0 : (double)hits / total;
    }

    /// <summary>Counts the draws in which at least one line matches 3+ main numbers. Lines are
    /// held as bitsets per ball, and per-line match counts are bit-sliced saturating counters
    /// (≥1, ≥2, ≥3), so each enumerated draw costs a few word operations regardless of K. A branch
    /// that already guarantees a hit is counted in closed form instead of enumerated.</summary>
    public static (long Hits, long Total) CountThreePlusDraws(
        IReadOnlyList<int[]> lines, int poolSize, int pickCount)
    {
        if (poolSize > MaxExactPoolSize)
            throw new ArgumentOutOfRangeException(nameof(poolSize), $"Exact counting supports pools up to {MaxExactPoolSize}.");
        long total = (long)Math.Round(Choose(poolSize, pickCount));
        if (lines.Count == 0 || pickCount < 3 || pickCount > poolSize) return (0, total);

        int words = (lines.Count + 63) / 64;
        var ballLines = new ulong[poolSize + 1][];
        for (int n = 0; n <= poolSize; n++) ballLines[n] = new ulong[words];
        for (int l = 0; l < lines.Count; l++)
            foreach (var n in lines[l]) ballLines[n][l >> 6] |= 1UL << (l & 63);

        long hits = 0;
        Parallel.For(1, poolSize - pickCount + 2, first =>
        {
            // counters[depth] holds ≥1, ≥2, ≥3 bitsets after `depth` balls have been chosen.
            var counters = new ulong[pickCount + 1][];
            for (int d = 0; d <= pickCount; d++) counters[d] = new ulong[3 * words];
            Add(counters[0], counters[1], ballLines[first], words);
            long local = CountFrom(1, first + 1, counters, ballLines, words, poolSize, pickCount);
            Interlocked.Add(ref hits, local);
        });
        return (hits, total);
    }

    private static void Add(ulong[] from, ulong[] to, ulong[] ball, int words)
    {
        for (int w = 0; w < words; w++)
        {
            ulong ge1 = from[w], ge2 = from[words + w], ge3 = from[2 * words + w], b = ball[w];
            to[w] = ge1 | b;
            to[words + w] = ge2 | (ge1 & b);
            to[2 * words + w] = ge3 | (ge2 & b);
        }
    }

    private static long CountFrom(
        int depth, int start, ulong[][] counters, ulong[][] ballLines, int words, int poolSize, int pickCount)
    {
        var state = counters[depth];
        int remaining = pickCount - depth;
        for (int w = 0; w < words; w++)
            if (state[2 * words + w] != 0)
                return (long)Math.Round(Choose(poolSize - start + 1, remaining));

        long hits = 0;
        if (remaining == 1)
        {
            for (int n = start; n <= poolSize; n++)
            {
                var ball = ballLines[n];
                for (int w = 0; w < words; w++)
                    if ((state[words + w] & ball[w]) != 0) { hits++; break; }
            }
            return hits;
        }

        for (int n = start; n <= poolSize - remaining + 1; n++)
        {
            Add(state, counters[depth + 1], ballLines[n], words);
            hits += CountFrom(depth + 1, n + 1, counters, ballLines, words, poolSize, pickCount);
        }
        return hits;
    }

    /// <summary>Exact hit count over every possible draw when the pool fits a 64-bit mask;
    /// otherwise a seeded Monte Carlo estimate.</summary>
    public static PortfolioSimulation Evaluate(
        IReadOnlyList<int[]> lines, int poolSize, int pickCount, int randomSeed = 20260914)
    {
        if (poolSize > MaxExactPoolSize) return Simulate(lines, poolSize, pickCount, randomSeed);
        var (hits, total) = CountThreePlusDraws(lines, poolSize, pickCount);
        double probability = (double)hits / total;
        return new PortfolioSimulation(
            (int)Math.Min(total, int.MaxValue), hits, probability, probability, probability, Exhaustive: true);
    }

    /// <summary>Seeded simulated annealing over single-ball swaps that minimises the summed
    /// pairwise loss Σ P(both lines match 3+). Returns the lowest-loss portfolio seen, or null
    /// when it is no better than the start. Duplicate lines and excluded balls are never produced.</summary>
    private static List<int[]>? Refine(
        IReadOnlyList<int[]> start, int[] legalNumbers, int pickCount, double[] pairTerm, Random rng)
    {
        int k = start.Count;
        if (k < 2 || legalNumbers.Length <= pickCount) return null;

        int maxBall = legalNumbers.Max();
        var lines = start.Select(line => line.ToArray()).ToArray();
        var has = lines.Select(line =>
        {
            var mask = new bool[maxBall + 1];
            foreach (var n in line) mask[n] = true;
            return mask;
        }).ToArray();
        var overlap = new int[k, k];
        double loss = 0;
        for (int i = 0; i < k; i++)
            for (int j = i + 1; j < k; j++)
            {
                int shared = lines[i].Count(n => has[j][n]);
                overlap[i, j] = overlap[j, i] = shared;
                loss += pairTerm[shared];
            }

        double startLoss = loss, bestLoss = loss;
        int[][]? best = null;
        double initialTemperature = 0;
        for (int s = 1; s < pairTerm.Length && initialTemperature <= 0; s++)
            initialTemperature = pairTerm[s] - pairTerm[s - 1];
        if (initialTemperature <= 0) return null;

        int steps = RefinementBaseSteps + RefinementStepsPerLine * k;
        for (int step = 0; step < steps; step++)
        {
            double temperature = initialTemperature * Math.Pow(1e-4, (double)step / steps);
            int i = rng.Next(k), position = rng.Next(pickCount);
            int removed = lines[i][position], added = legalNumbers[rng.Next(legalNumbers.Length)];
            if (has[i][added]) continue;

            double delta = 0;
            for (int j = 0; j < k && !double.IsPositiveInfinity(delta); j++)
            {
                if (j == i) continue;
                int shared = overlap[i, j] - (has[j][removed] ? 1 : 0) + (has[j][added] ? 1 : 0);
                delta = shared == pickCount ? double.PositiveInfinity : delta + pairTerm[shared] - pairTerm[overlap[i, j]];
            }
            if (double.IsPositiveInfinity(delta)) continue;
            if (delta > 0 && rng.NextDouble() >= Math.Exp(-delta / temperature)) continue;

            for (int j = 0; j < k; j++)
            {
                if (j == i) continue;
                int shared = overlap[i, j] - (has[j][removed] ? 1 : 0) + (has[j][added] ? 1 : 0);
                overlap[i, j] = overlap[j, i] = shared;
            }
            has[i][removed] = false;
            has[i][added] = true;
            lines[i][position] = added;
            loss += delta;
            if (loss < bestLoss - 1e-15)
            {
                bestLoss = loss;
                best = lines.Select(line => line.ToArray()).ToArray();
            }
        }

        if (best is null || bestLoss >= startLoss) return null;
        foreach (var line in best) Array.Sort(line);
        return [.. best];
    }

    /// <summary>P(two specific lines that share <paramref name="shared"/> balls both match 3+
    /// main numbers in one uniform draw of <paramref name="pickCount"/> balls).</summary>
    public static double PairwiseThreePlusProbability(int poolSize, int pickCount, int shared)
    {
        int only = pickCount - shared;          // balls unique to each line
        int outside = poolSize - 2 * pickCount + shared;
        double count = 0;
        for (int a = 0; a <= shared; a++)
            for (int b = 0; b <= only; b++)
                for (int c = 0; c <= only; c++)
                {
                    int o = pickCount - a - b - c;
                    if (o < 0 || a + b < 3 || a + c < 3) continue;
                    count += Choose(shared, a) * Choose(only, b) * Choose(only, c) * Choose(outside, o);
                }
        return count / Choose(poolSize, pickCount);
    }

    /// <summary>Lines needed so P(at least one 3+ match on a draw night with
    /// <paramref name="rounds"/> independent rounds) reaches <paramref name="target"/>,
    /// as a necessary lower bound from the union bound, not an achievable guarantee.</summary>
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
            if (portfolio.Any(line => Backtester.CountMatches(line, draw) >= 3)) hits++;
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
            for (int size = 1; size <= pickCount; size++)
                foreach (var key in SubsetKeys(line, size)) used[size].Add(key);
            var mask = new bool[poolSize + 1];
            foreach (var n in line)
            {
                mask[n] = true;
                uses[n]++;
            }
            presence.Add(mask);
        }

        /// <summary>Sum over earlier lines of P(this line and that line both match 3+).</summary>
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
