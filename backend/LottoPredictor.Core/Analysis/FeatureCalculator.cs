namespace LottoPredictor.Core.Analysis;

/// <summary>Pure feature computation. Given an ordered prefix of draws it computes every
/// per-number and combination-level statistic. It never sees anything outside the list it
/// is given, which is what makes walk-forward backtesting leak-free.</summary>
public static class FeatureCalculator
{
    public static FeatureSet Compute(
        IReadOnlyList<DrawEvent> draws,
        int? configuredPoolSize = null,
        DateOnly? poolExpansionDate = null,
        DateOnly? targetDrawDate = null,
        IReadOnlyList<PoolRule>? ruleEras = null,
        bool includeTriples = true)
    {
        if (draws.Count == 0) throw new InvalidOperationException("Cannot compute features with no draws.");

        var pool = PoolInfo.Detect(draws, configuredPoolSize, poolExpansionDate, targetDrawDate, ruleEras);
        int p = pool.PoolSize;
        int n = draws.Count;
        int pickCount = draws[0].Numbers.Length;
        if (pickCount == 0)
            throw new InvalidOperationException("Draws must contain at least one ball.");
        for (int i = 0; i < n; i++)
        {
            var numbers = draws[i].Numbers;
            if (numbers.Length != pickCount)
                throw new InvalidOperationException("All draws must contain the same number of balls.");
            int poolAtDraw = pool.PoolAt(i);
            for (int j = 0; j < pickCount; j++)
            {
                int val = numbers[j];
                if (val < 1 || val > poolAtDraw)
                    throw new InvalidOperationException(
                        $"Draw at sequence {draws[i].Sequence} contains a ball outside pool 1-{poolAtDraw}.");
                if (j > 0)
                {
                    if (val == numbers[j - 1])
                        throw new InvalidOperationException(
                            $"Draw at sequence {draws[i].Sequence} contains duplicate balls.");
                    if (val < numbers[j - 1])
                        throw new InvalidOperationException(
                            $"Draw at sequence {draws[i].Sequence} must be sorted in ascending order.");
                }
            }
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
        var bonusDrawIndices = new List<int>();

        for (int i = 0; i < n; i++)
        {
            if (draws[i].Bonus is int bb && bb >= 1 && bb <= p)
            {
                bonusCounts[bb]++;
                bonusDrawIndices.Add(i);
            }
            var nums = draws[i].Numbers;
            for (int j = 0; j < pickCount; j++)
            {
                int v = nums[j];
                occurrences[v].Add(i);
                positionCounts[v][j]++;
            }
            for (int a = 0; a < pickCount; a++)
            {
                int na = nums[a];
                for (int b = a + 1; b < pickCount; b++)
                {
                    int nb = nums[b];
                    pairCounts[na, nb]++;
                    if (includeTriples)
                    {
                        for (int c = b + 1; c < pickCount; c++)
                        {
                            var key = (na, nb, nums[c]);
                            tripleCounts.TryGetValue(key, out int t);
                            tripleCounts[key] = t + 1;
                        }
                    }
                }
            }
        }

        var physicalAffinity = ComputePhysicalAffinity(draws, pool, p, n, pickCount);

        // Precompute cumulative variance and bonus expectation prefixes for O(1) per-ball queries.
        var varPrefix = new double[n + 1];
        var bonusExpPrefix = new double[n + 1];
        var bonusCountPrefix = new int[n + 1];
        for (int i = 0; i < n; i++)
        {
            int poolAtI = pool.PoolAt(i);
            double q = (double)pickCount / poolAtI;
            varPrefix[i + 1] = varPrefix[i] + q * (1.0 - q);
            bool hasBonus = draws[i].Bonus is int bVal && bVal >= 1 && bVal <= poolAtI;
            bonusCountPrefix[i + 1] = bonusCountPrefix[i] + (hasBonus ? 1 : 0);
            bonusExpPrefix[i + 1] = bonusExpPrefix[i] + (hasBonus ? 1.0 / Math.Max(1, poolAtI - pickCount) : 0.0);
        }

        var features = new NumberFeatures[p];
        double qNow = (double)pickCount / pool.NextPoolSize;
        double currentExpectedGap = pool.NextPoolSize / (double)pickCount;
        const double shrink = 20.0;

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
            double gapHazardZ = 0;
            if (total >= 2)
            {
                double sumScaledGaps = 0;
                int closeLagCount = 0;
                int surviveCount = 0;
                for (int k = 1; k < total; k++)
                {
                    int rawGap = occ[k] - occ[k - 1];
                    double eraScale = (double)pool.NextPoolSize / pool.PoolAt(occ[k]);
                    double scaledGap = rawGap * eraScale;
                    sumScaledGaps += scaledGap;

                    int targetLag = drawsSinceLast + 1;
                    if (scaledGap >= targetLag - 1.5)
                    {
                        surviveCount++;
                        if (Math.Abs(scaledGap - targetLag) <= 1.5) closeLagCount++;
                    }
                }
                avgGap = sumScaledGaps / (total - 1);
                if (surviveCount >= 3)
                {
                    double fairHazard = 1.0 - Math.Pow(Math.Max(0.0, 1.0 - qNow), 3);
                    double empHazard = (closeLagCount + 4.0 * fairHazard) / (surviveCount + 4.0);
                    gapHazardZ = (empHazard - fairHazard) / Math.Sqrt(fairHazard * (1.0 - fairHazard) / (surviveCount + 4.0) + 1e-9);
                }
            }
            else
            {
                avgGap = Math.Max(1, eligible);
            }

            double freqRate = eligible > 0 ? (double)total / eligible : 0;

            double expected = pool.ExpectedAppearances(eligibleFrom, n, pickCount);
            double variance = varPrefix[n] - varPrefix[eligibleFrom];
            double biasZ = variance > 1e-9 ? (total - expected) / Math.Sqrt(variance) : 0;

            // Era-standardised Bayesian shrinkage onto the current-era fair rate qNow.
            // In a single-era pool, expected == eligible * qNow, so this equals (total + shrink * qNow) / (eligible + shrink)
            // identically, while eliminating multi-era baseline shift when the pool size changed.
            double freqShrunk = Math.Max(1e-6, qNow + (total - expected) / (Math.Max(0, eligible) + shrink));
            double recentShrunk = recentWindows.Length > 0
                ? recentWindows.Average(window =>
                {
                    double winExpected = pool.ExpectedAppearances(n - window.Size, n, pickCount);
                    return Math.Max(0.0, qNow + (window.Count - winExpected) / (window.Size + shrink));
                })
                : qNow;
            double exp50 = pool.ExpectedAppearances(n - w50, n, pickCount);
            double rate50Shrunk = Math.Max(1e-6, qNow + (c50 - exp50) / (w50 + shrink));

            // Multi-horizon EWMA rate (half-lives 12 and 40 draws) normalised against era expectations.
            double ewmaObs = 0, ewmaExp = 0, ewmaWeight = 0;
            int ewmaStart = Math.Max(eligibleFrom, n - 120);
            for (int d = ewmaStart; d < n; d++)
            {
                double w = 0.6 * Math.Exp(-0.05776 * (n - 1 - d)) + 0.4 * Math.Exp(-0.01733 * (n - 1 - d));
                ewmaExp += w * ((double)pickCount / pool.PoolAt(d));
                ewmaWeight += w;
            }
            for (int k = total - 1; k >= 0 && occ[k] >= ewmaStart; k--)
            {
                int d = occ[k];
                double w = 0.6 * Math.Exp(-0.05776 * (n - 1 - d)) + 0.4 * Math.Exp(-0.01733 * (n - 1 - d));
                ewmaObs += w;
            }
            double ewmaRate = ewmaWeight > 0
                ? Math.Max(0.0, qNow + (ewmaObs - ewmaExp) / (ewmaWeight + 6.0))
                : qNow;

            double bonusPrior = 1.0 / Math.Max(1, pool.NextPoolSize - pickCount);
            int eligibleBonusDraws = bonusCountPrefix[n] - bonusCountPrefix[eligibleFrom];
            double expectedBonus = bonusExpPrefix[n] - bonusExpPrefix[eligibleFrom];
            double bonusRate = eligibleBonusDraws > 0
                ? Math.Max(0.0, bonusPrior + (bonusCounts[v] - expectedBonus) / (eligibleBonusDraws + shrink))
                : (bonusCounts[v] + shrink * bonusPrior) / (Math.Max(0, eligible) + shrink);

            // Repeat / lag-1 follow-through propensity + positional slot stability + physical affinity.
            int repeatHits = 0;
            for (int k = 1; k < total; k++)
                if (occ[k] - occ[k - 1] == 1) repeatHits++;
            double repeatExpected = Math.Max(0, total - 1) * qNow;
            double repeatZ = total > 2
                ? (repeatHits - repeatExpected) / Math.Sqrt(repeatExpected * (1.0 - qNow) + 2.0)
                : 0.0;
            double appearedLast = drawsSinceLast == 0 ? 1.0 : -0.2;
            double secondarySignal = 0.55 * physicalAffinity[v] + 0.30 * repeatZ * appearedLast + 0.15 * gapHazardZ;

            // Current-era scaled DrawsSinceLast for GapRatio when the last appearance was in an earlier era.
            double effectiveSinceLast = drawsSinceLast;
            if (total > 0 && occ[^1] < pool.CurrentEraStartIndex && eligible > 0)
            {
                int era1Wait = Math.Max(0, pool.CurrentEraStartIndex - 1 - occ[^1]);
                int currentEraWait = n - Math.Max(pool.CurrentEraStartIndex, occ[^1] + 1);
                effectiveSinceLast = currentEraWait + era1Wait * ((double)pool.NextPoolSize / pool.PoolAt(occ[^1]));
            }
            double shrunkAvgGap = total >= 5
                ? (avgGap * (total - 1) + 3.0 * currentExpectedGap) / (total + 2.0)
                : avgGap;

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
                GapRatio = avgGap > 0 ? effectiveSinceLast / avgGap : 0,
                RecentVsLongTerm = rate50Shrunk / freqShrunk,
                BiasZ = biasZ,
                EwmaRate = ewmaRate,
                GapHazardZ = Math.Clamp(gapHazardZ + 0.35 * ((effectiveSinceLast / Math.Max(1.0, shrunkAvgGap)) - 1.0), -3.0, 3.0),
                PhysicalAffinity = physicalAffinity[v],
                SecondarySignal = secondarySignal,
                PositionCounts = positionCounts[v],
            };
        }

        // Combination-level distributions come from the current constant-pool era where there is enough of it,
        // because the sum/range distribution depends on the pool size.
        int eraStart = pool.CurrentEraStartIndex < n && n - pool.CurrentEraStartIndex >= 30
            ? pool.CurrentEraStartIndex
            : 0;
        var sums = new List<double>(n - eraStart);
        var ranges = new List<double>(n - eraStart);
        var odds = new List<double>(n - eraStart);
        var consecs = new List<double>(n - eraStart);
        var lows = new List<double>(n - eraStart);
        int half = (p + 1) / 2;
        for (int i = eraStart; i < n; i++)
        {
            var nums = draws[i].Numbers;
            int sum = 0, oddCount = 0, cc = 0, lowCount = 0;
            for (int j = 0; j < pickCount; j++)
            {
                int v = nums[j];
                sum += v;
                if ((v & 1) == 1) oddCount++;
                if (v <= half) lowCount++;
                if (j < pickCount - 1 && nums[j + 1] == v + 1) cc++;
            }
            sums.Add(sum);
            ranges.Add(nums[^1] - nums[0]);
            odds.Add(oddCount);
            consecs.Add(cc);
            lows.Add(lowCount);
        }

        var (sumMean, sumStd) = MeanStd(sums);
        var (rangeMean, rangeStd) = MeanStd(ranges);
        var (oddMean, oddStd) = MeanStd(odds);
        var (consecMean, consecStd) = MeanStd(consecs);
        var (lowMean, lowStd) = MeanStd(lows);

        // Chi-square uniformity test over the current constant-pool era (constant pool = valid test).
        int chiStart = pool.CurrentEraStartIndex;
        int chiWindow = n - chiStart;
        double chiSquare = 0;
        int chiDf = 0;
        double chiP = 1.0;
        if (chiWindow >= 30 && pool.NextPoolSize > 1)
        {
            double e = chiWindow * (double)pickCount / pool.NextPoolSize;
            for (int v = 1; v <= pool.NextPoolSize; v++)
            {
                int observed = CountFrom(occurrences[v], chiStart);
                chiSquare += (observed - e) * (observed - e) / e;
            }
            chiDf = pool.NextPoolSize - 1;
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

    /// <summary>Computes per-ball physical equipment affinity weighted by the active BallSet and
    /// Machine regime observed in the most recent draws. Returns zeros when no equipment metadata is present.</summary>
    private static double[] ComputePhysicalAffinity(
        IReadOnlyList<DrawEvent> draws, PoolInfo pool, int maxPool, int n, int pickCount)
    {
        var affinity = new double[maxPool + 1];
        int recentStart = Math.Max(0, n - 40);
        var recentBallSets = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var recentMachines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double setWeightTotal = 0, machineWeightTotal = 0;

        for (int i = recentStart; i < n; i++)
        {
            double recency = Math.Exp(-0.05 * (n - 1 - i));
            if (!string.IsNullOrWhiteSpace(draws[i].BallSet))
            {
                string bs = draws[i].BallSet!;
                recentBallSets[bs] = recentBallSets.GetValueOrDefault(bs) + recency;
                setWeightTotal += recency;
            }
            if (!string.IsNullOrWhiteSpace(draws[i].Machine))
            {
                string m = draws[i].Machine!;
                recentMachines[m] = recentMachines.GetValueOrDefault(m) + recency;
                machineWeightTotal += recency;
            }
        }

        if (setWeightTotal <= 0 && machineWeightTotal <= 0) return affinity;

        var setDrawCounts = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        var setExpected = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var bs in recentBallSets.Keys)
        {
            setDrawCounts[bs] = new double[maxPool + 1];
            setExpected[bs] = new double[maxPool + 1];
        }

        var machDrawCounts = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        var machExpected = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in recentMachines.Keys)
        {
            machDrawCounts[m] = new double[maxPool + 1];
            machExpected[m] = new double[maxPool + 1];
        }

        for (int i = 0; i < n; i++)
        {
            int poolAtI = pool.PoolAt(i);
            double q = (double)pickCount / poolAtI;
            var nums = draws[i].Numbers;

            if (draws[i].BallSet is string bs && setDrawCounts.TryGetValue(bs, out var sCounts))
            {
                var sExp = setExpected[bs];
                for (int v = 1; v <= poolAtI; v++) sExp[v] += q;
                for (int j = 0; j < nums.Length; j++) sCounts[nums[j]] += 1.0;
            }
            if (draws[i].Machine is string m && machDrawCounts.TryGetValue(m, out var mCounts))
            {
                var mExp = machExpected[m];
                for (int v = 1; v <= poolAtI; v++) mExp[v] += q;
                for (int j = 0; j < nums.Length; j++) mCounts[nums[j]] += 1.0;
            }
        }

        for (int v = 1; v <= maxPool; v++)
        {
            double setZ = 0;
            if (setWeightTotal > 0)
            {
                foreach (var (bs, w) in recentBallSets)
                {
                    double obs = setDrawCounts[bs][v];
                    double exp = setExpected[bs][v];
                    if (exp > 0)
                        setZ += (w / setWeightTotal) * ((obs - exp) / Math.Sqrt(exp * (1.0 - (double)pickCount / pool.NextPoolSize) + 3.0));
                }
            }

            double machZ = 0;
            if (machineWeightTotal > 0)
            {
                foreach (var (m, w) in recentMachines)
                {
                    double obs = machDrawCounts[m][v];
                    double exp = machExpected[m][v];
                    if (exp > 0)
                        machZ += (w / machineWeightTotal) * ((obs - exp) / Math.Sqrt(exp * (1.0 - (double)pickCount / pool.NextPoolSize) + 3.0));
                }
            }

            double combined = setWeightTotal > 0 && machineWeightTotal > 0
                ? 0.65 * setZ + 0.35 * machZ
                : setWeightTotal > 0 ? setZ : machZ;
            affinity[v] = Math.Clamp(combined, -3.0, 3.0);
        }

        return affinity;
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
