namespace LottoPredictor.Core.Analysis;

/// <summary>Pure feature computation. Given an ordered prefix of draws it computes every
/// per-number and combination-level statistic. It never sees anything outside the list it
/// is given, which is what makes walk-forward backtesting leak-free.</summary>
public static class FeatureCalculator
{
    public static FeatureSet Compute(
        IReadOnlyList<DrawEvent> draws,
        int? configuredPoolSize = null,
        DateOnly? poolExpansionDate = null)
    {
        if (draws.Count == 0) throw new InvalidOperationException("Cannot compute features with no draws.");

        var pool = PoolInfo.Detect(draws, configuredPoolSize, poolExpansionDate);
        int p = pool.PoolSize;
        int n = draws.Count;
        int pickCount = draws[0].Numbers.Length;
        if (pickCount == 0)
            throw new InvalidOperationException("Draws must contain at least one ball.");
        if (draws.Any(draw => draw.Numbers.Length != pickCount))
            throw new InvalidOperationException("All draws must contain the same number of balls.");
        for (int i = 0; i < draws.Count; i++)
        {
            var numbers = draws[i].Numbers;
            if (numbers.Any(number => number < 1 || number > p))
                throw new InvalidOperationException(
                    $"Draw at sequence {draws[i].Sequence} contains a ball outside pool 1-{p}.");
            if (numbers.Distinct().Count() != numbers.Length)
                throw new InvalidOperationException(
                    $"Draw at sequence {draws[i].Sequence} contains duplicate balls.");
            if (!numbers.SequenceEqual(numbers.OrderBy(number => number)))
                throw new InvalidOperationException(
                    $"Draw at sequence {draws[i].Sequence} must be sorted in ascending order.");
        }

        var occurrences = new List<int>[p + 1];
        var positionCounts = new int[p + 1][];
        for (int v = 1; v <= p; v++)
        {
            occurrences[v] = [];
            positionCounts[v] = new int[pickCount];
        }
        var pairCounts = new int[p + 1, p + 1];
        var tripleCounts = new Dictionary<(int, int, int), int>();
        var bonusCounts = new int[p + 1];

        for (int i = 0; i < n; i++)
        {
            if (draws[i].Bonus is int bb && bb >= 1 && bb <= p) bonusCounts[bb]++;
            var nums = draws[i].Numbers;
            for (int j = 0; j < pickCount; j++)
            {
                int v = nums[j];
                occurrences[v].Add(i);
                positionCounts[v][j]++;
            }
            for (int a = 0; a < pickCount; a++)
                for (int b = a + 1; b < pickCount; b++)
                {
                    pairCounts[nums[a], nums[b]]++;
                    for (int c = b + 1; c < pickCount; c++)
                    {
                        var key = (nums[a], nums[b], nums[c]);
                        tripleCounts.TryGetValue(key, out int t);
                        tripleCounts[key] = t + 1;
                    }
                }
        }

        var features = new NumberFeatures[p];
        for (int v = 1; v <= p; v++)
        {
            var occ = occurrences[v];
            int eligibleFrom = pool.EligibleFromIndex(v);
            int eligible = n - eligibleFrom;
            int total = occ.Count;

            int c10 = CountFrom(occ, n - 10);
            int c25 = CountFrom(occ, n - 25);
            int c50 = CountFrom(occ, n - 50);
            int c100 = CountFrom(occ, n - 100);

            int w10 = Math.Min(10, eligible), w25 = Math.Min(25, eligible),
                w50 = Math.Min(50, eligible), w100 = Math.Min(100, eligible);
            var recentWindows = new[]
                {
                    (Count: c10, Size: w10),
                    (Count: c25, Size: w25),
                    (Count: c50, Size: w50),
                    (Count: c100, Size: w100),
                }
                .Where(window => window.Size > 0)
                .DistinctBy(window => window.Size)
                .ToArray();
            double recentRate = recentWindows.Length > 0
                ? recentWindows.Average(window => (double)window.Count / window.Size)
                : 0;

            int drawsSinceLast = total == 0 ? eligible : n - 1 - occ[^1];
            double avgGap;
            if (total >= 2)
            {
                double sumGaps = 0;
                for (int k = 1; k < total; k++) sumGaps += occ[k] - occ[k - 1];
                avgGap = sumGaps / (total - 1);
            }
            else
            {
                avgGap = Math.Max(1, eligible);
            }

            double freqRate = eligible > 0 ? (double)total / eligible : 0;
            double rate50 = w50 > 0 ? (double)c50 / w50 : 0;

            // Fair-machine expectation, era aware: pool was 49 before the switch, PoolSize after.
            int p1 = p > 49 ? 49 : p;
            int era1 = Math.Max(0, Math.Min(pool.Era2StartIndex, n) - eligibleFrom);
            int era2 = Math.Max(0, n - Math.Max(pool.Era2StartIndex, eligibleFrom));
            double q1 = (double)pickCount / p1, q2 = (double)pickCount / p;
            double expected = era1 * q1 + era2 * q2;
            double variance = era1 * q1 * (1 - q1) + era2 * q2 * (1 - q2);
            double biasZ = variance > 1e-9 ? (total - expected) / Math.Sqrt(variance) : 0;

            // Bayesian shrinkage toward the fair rate: ~20 pseudo-draws of prior evidence.
            const double shrink = 20.0;
            double qNow = (double)pickCount / pool.PoolAt(n - 1);
            double qBar = eligible > 0 ? expected / eligible : qNow;
            double freqShrunk = (total + shrink * qBar) / (Math.Max(0, eligible) + shrink);
            double recentShrunk = recentWindows.Length > 0
                ? recentWindows.Average(window =>
                    (window.Count + shrink * qNow) / (window.Size + shrink))
                : qNow;
            double rate50Shrunk = (c50 + shrink * qNow) / (w50 + shrink);

            double bonusPrior = 1.0 / Math.Max(1, p - pickCount);
            double bonusRate = (bonusCounts[v] + shrink * bonusPrior) / (Math.Max(0, eligible) + shrink);

            features[v - 1] = new NumberFeatures
            {
                Number = v,
                TotalCount = total,
                EligibleDraws = eligible,
                FreqRate = freqRate,
                Count10 = c10,
                Count25 = c25,
                Count50 = c50,
                Count100 = c100,
                RecentRate = recentRate,
                FreqRateShrunk = freqShrunk,
                RecentRateShrunk = recentShrunk,
                BonusCount = bonusCounts[v],
                BonusRate = bonusRate,
                DrawsSinceLast = drawsSinceLast,
                AvgGap = avgGap,
                GapRatio = avgGap > 0 ? drawsSinceLast / avgGap : 0,
                RecentVsLongTerm = rate50Shrunk / freqShrunk,
                BiasZ = biasZ,
                PositionCounts = positionCounts[v],
            };
        }

        // Combination-level distributions come from the current era where there is enough of it,
        // because the sum/range distribution depends on the pool size.
        int eraStart = pool.Era2StartIndex < n && n - pool.Era2StartIndex >= 30 ? pool.Era2StartIndex : 0;
        var sums = new List<double>();
        var ranges = new List<double>();
        var odds = new List<double>();
        var consecs = new List<double>();
        var lows = new List<double>();
        int half = (p + 1) / 2;
        for (int i = eraStart; i < n; i++)
        {
            var nums = draws[i].Numbers;
            sums.Add(nums.Sum());
            ranges.Add(nums[^1] - nums[0]);
            odds.Add(nums.Count(x => x % 2 == 1));
            int cc = 0;
            for (int j = 0; j < pickCount - 1; j++) if (nums[j + 1] == nums[j] + 1) cc++;
            consecs.Add(cc);
            lows.Add(nums.Count(x => x <= half));
        }

        var (sumMean, sumStd) = MeanStd(sums);
        var (rangeMean, rangeStd) = MeanStd(ranges);
        var (oddMean, oddStd) = MeanStd(odds);
        var (consecMean, consecStd) = MeanStd(consecs);
        var (lowMean, lowStd) = MeanStd(lows);

        // Chi-square uniformity test over the current era (constant pool = valid test).
        int chiStart = p > 49 ? pool.Era2StartIndex : 0;
        int chiWindow = n - chiStart;
        double chiSquare = 0;
        int chiDf = 0;
        double chiP = 1.0;
        if (chiWindow >= 30)
        {
            double e = chiWindow * (double)pickCount / p;
            for (int v = 1; v <= p; v++)
            {
                int observed = CountFrom(occurrences[v], chiStart);
                chiSquare += (observed - e) * (observed - e) / e;
            }
            chiDf = p - 1;
            chiP = StatFunctions.ChiSquarePValue(chiSquare, chiDf);
        }

        return new FeatureSet
        {
            Pool = pool,
            DrawCount = n,
            PickCount = pickCount,
            Numbers = features,
            PairCounts = pairCounts,
            TripleCounts = tripleCounts,
            SumMean = sumMean,
            SumStd = sumStd,
            RangeMean = rangeMean,
            RangeStd = rangeStd,
            OddMean = oddMean,
            OddStd = oddStd,
            ConsecMean = consecMean,
            ConsecStd = consecStd,
            LowHalfMean = lowMean,
            LowHalfStd = lowStd,
            ChiSquare = chiSquare,
            ChiSquareDf = chiDf,
            ChiSquarePValue = chiP,
            ChiSquareWindowDraws = chiWindow,
        };
    }

    private static int CountFrom(List<int> sortedOccurrences, int fromIndex)
    {
        if (fromIndex <= 0) return sortedOccurrences.Count;
        int c = 0;
        for (int i = sortedOccurrences.Count - 1; i >= 0 && sortedOccurrences[i] >= fromIndex; i--) c++;
        return c;
    }

    private static (double Mean, double Std) MeanStd(List<double> values)
    {
        if (values.Count == 0) return (0, 0);
        double mean = values.Average();
        double var = values.Count > 1 ? values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1) : 0;
        return (mean, Math.Sqrt(var));
    }
}
