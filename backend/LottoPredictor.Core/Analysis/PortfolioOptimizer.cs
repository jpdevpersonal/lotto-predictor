namespace LottoPredictor.Core.Analysis;

public sealed record PortfolioLine(
    int Rank,
    int[] Numbers,
    double Score,
    int NewFourSubsets,
    int SharedFourSubsets);

public sealed record PortfolioSimulation(
    int Trials,
    int Hits,
    double Probability,
    double CiLow,
    double CiHigh);

public sealed record PortfolioResult(
    IReadOnlyList<PortfolioLine> Lines,
    PortfolioSimulation CoverageOptimized,
    PortfolioSimulation RandomDistinct,
    double SingleLineFourPlusProbability,
    string Objective);

public static class PortfolioOptimizer
{
    private const int CandidateSamples = 6000;
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

        var rng = new Random(randomSeed + lineCount * 17 + poolSize * 31 + pickCount);
        var selected = new List<PortfolioLine>(lineCount);
        var selectedKeys = new HashSet<string>();
        var coveredFourSubsets = new HashSet<long>();

        var candidateSets = CandidateSets(legalNumbers, pickCount, scores, rng).ToList();
        while (selected.Count < lineCount)
        {
            (int[] Set, double Score, int New, int Shared)? best = null;
            foreach (var set in candidateSets)
            {
                var key = LineKey(set);
                if (selectedKeys.Contains(key)) continue;

                int fresh = 0;
                int shared = 0;
                foreach (var subset in FourSubsetKeys(set))
                {
                    if (coveredFourSubsets.Contains(subset)) shared++;
                    else fresh++;
                }

                double score = LineScore(fs, set, scores, strategy);
                if (best is null || fresh > best.Value.New ||
                    (fresh == best.Value.New && shared < best.Value.Shared) ||
                    (fresh == best.Value.New && shared == best.Value.Shared && score > best.Value.Score) ||
                    (fresh == best.Value.New && shared == best.Value.Shared && score == best.Value.Score &&
                     PredictionLexCompare(set, best.Value.Set) < 0))
                    best = (set, score, fresh, shared);
            }

            if (best is null)
            {
                var fallback = RandomSet(rng, poolSize, pickCount);
                while (selectedKeys.Contains(LineKey(fallback)))
                    fallback = RandomSet(rng, poolSize, pickCount);
                best = (fallback, LineScore(fs, fallback, scores, strategy),
                    FourSubsetKeys(fallback).Count(subset => !coveredFourSubsets.Contains(subset)),
                    FourSubsetKeys(fallback).Count(subset => coveredFourSubsets.Contains(subset)));
            }

            foreach (var subset in FourSubsetKeys(best.Value.Set)) coveredFourSubsets.Add(subset);
            selectedKeys.Add(LineKey(best.Value.Set));
            selected.Add(new PortfolioLine(
                selected.Count + 1,
                best.Value.Set,
                Math.Round(best.Value.Score, 4),
                best.Value.New,
                best.Value.Shared));
        }

        var randomPortfolio = DistinctRandomLines(
            new Random(randomSeed + 991), legalNumbers, pickCount, lineCount);
        return new PortfolioResult(
            selected,
            Simulate(selected.Select(line => line.Numbers).ToList(), poolSize, pickCount, randomSeed + 1009),
            Simulate(randomPortfolio, poolSize, pickCount, randomSeed + 2003),
            Backtester.FourPlusProbability(poolSize, pickCount),
            $"P(at least one of K={lineCount} fixed lines matches at least four main numbers)");
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

    private static IEnumerable<int[]> CandidateSets(
        int[] legalNumbers,
        int pickCount,
        Dictionary<int, double> scores,
        Random rng)
    {
        var ranked = legalNumbers
            .OrderByDescending(number => scores.GetValueOrDefault(number))
            .ThenBy(number => number)
            .ToArray();

        if (ranked.Length <= 20)
        {
            foreach (var combo in Combinations(ranked.Length, pickCount))
                yield return combo.Select(i => ranked[i]).OrderBy(number => number).ToArray();
        }

        yield return ranked.Take(pickCount).OrderBy(number => number).ToArray();

        for (int offset = 0; offset < ranked.Length && offset < 24; offset++)
        {
            var line = new HashSet<int>();
            for (int i = offset; line.Count < pickCount; i += Math.Max(1, ranked.Length / pickCount))
                line.Add(ranked[i % ranked.Length]);
            yield return [.. line.OrderBy(number => number)];
        }

        for (int i = 0; i < CandidateSamples; i++)
            yield return RandomSet(rng, legalNumbers, pickCount);
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

    private static IEnumerable<long> FourSubsetKeys(int[] sortedSet)
    {
        for (int a = 0; a < sortedSet.Length - 3; a++)
            for (int b = a + 1; b < sortedSet.Length - 2; b++)
                for (int c = b + 1; c < sortedSet.Length - 1; c++)
                    for (int d = c + 1; d < sortedSet.Length; d++)
                        yield return Encode(sortedSet[a], sortedSet[b], sortedSet[c], sortedSet[d]);
    }

    private static long Encode(int a, int b, int c, int d) =>
        ((long)a << 48) | ((long)b << 32) | ((long)c << 16) | (uint)d;

    private static string LineKey(int[] sortedSet) => string.Join(',', sortedSet);

    private static int PredictionLexCompare(int[] a, int[] b)
    {
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return 0;
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