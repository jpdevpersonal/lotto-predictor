namespace LottoPredictor.Core.Analysis;

/// <summary>Historical features for one ball number, computed strictly from a prefix of draws.</summary>
public sealed class NumberFeatures
{
    public int Number { get; init; }
    public int TotalCount { get; init; }

    /// <summary>Number of draws in which this number was in the pool (era aware).</summary>
    public int EligibleDraws { get; init; }

    /// <summary>TotalCount / EligibleDraws.</summary>
    public double FreqRate { get; init; }

    /// <summary>FreqRate shrunk toward the fair-machine rate (Bayesian pseudo-count prior).
    /// Used for scoring so low-sample numbers do not dominate through noise.</summary>
    public double FreqRateShrunk { get; init; }

    public int Count10 { get; init; }
    public int Count25 { get; init; }
    public int Count50 { get; init; }
    public int Count100 { get; init; }

    /// <summary>Blended per-draw appearance rate over the recent windows.</summary>
    public double RecentRate { get; init; }

    /// <summary>RecentRate shrunk toward the fair-machine rate. Used for scoring.</summary>
    public double RecentRateShrunk { get; init; }

    /// <summary>Times drawn as the bonus ball while eligible.</summary>
    public int BonusCount { get; init; }

    /// <summary>Shrunk per-draw bonus appearance rate (prior 1/(pool-6)).</summary>
    public double BonusRate { get; init; }

    /// <summary>Draws since the number last appeared (0 = appeared in latest draw).</summary>
    public int DrawsSinceLast { get; init; }

    /// <summary>Mean gap in draws between consecutive appearances.</summary>
    public double AvgGap { get; init; }

    /// <summary>DrawsSinceLast / AvgGap. 1 = exactly "on schedule".</summary>
    public double GapRatio { get; init; }

    /// <summary>Recent (last 50) rate divided by long-term rate. &gt;1 = hot lately.</summary>
    public double RecentVsLongTerm { get; init; }

    /// <summary>Binomial z-score of observed appearances vs a fair-machine expectation,
    /// era aware. Large |z| suggests physical bias; ~N(0,1) for a fair lottery.</summary>
    public double BiasZ { get; init; }

    /// <summary>Multi-horizon exponentially weighted moving average rate (era-standardised).</summary>
    public double EwmaRate { get; init; }

    /// <summary>Empirical renewal hazard residual at the ball's current lag (DrawsSinceLast).</summary>
    public double GapHazardZ { get; init; }

    /// <summary>Shrunk z-score of appearances within the active Machine and BallSet regime.</summary>
    public double PhysicalAffinity { get; init; }

    /// <summary>Secondary structural signal (equipment affinity, slot concentration, and lag-1 repeat
    /// propensity) used when a game has no main-pool bonus ball or blended alongside it.</summary>
    public double SecondarySignal { get; init; }

    /// <summary>Appearances per sorted position (N1..N6).</summary>
    public int[] PositionCounts { get; init; } = new int[6];
}

/// <summary>Full feature snapshot for a prefix of draws. Immutable once built.</summary>
public sealed class FeatureSet
{
    private double[,]? pairSynergyCache;

    public required PoolInfo Pool { get; init; }
    public required int DrawCount { get; init; }
    public required int PickCount { get; init; }

    /// <summary>Index 0 corresponds to ball number 1.</summary>
    public required NumberFeatures[] Numbers { get; init; }

    /// <summary>Pair co-occurrence counts, indexed [a, b] with a &lt; b.</summary>
    public required int[,] PairCounts { get; init; }

    /// <summary>Counts for the most frequent triples (key packed a*10000+b*100+c... kept as tuple).</summary>
    public required Dictionary<(int, int, int), int> TripleCounts { get; init; }

    // Combination-level historical distributions (current-era draws where possible).
    public required double SumMean { get; init; }
    public required double SumStd { get; init; }
    public required double RangeMean { get; init; }
    public required double RangeStd { get; init; }
    public required double OddMean { get; init; }
    public required double OddStd { get; init; }
    public required double ConsecMean { get; init; }
    public required double ConsecStd { get; init; }
    public required double LowHalfMean { get; init; }
    public required double LowHalfStd { get; init; }

    // Current-era chi-square uniformity test: is there any measurable ball bias at all?
    public required double ChiSquare { get; init; }
    public required int ChiSquareDf { get; init; }
    public required double ChiSquarePValue { get; init; }
    public required int ChiSquareWindowDraws { get; init; }

    public NumberFeatures For(int number) => Numbers[number - 1];

    /// <summary>Expected co-occurrence count of a pair under a uniform draw model, era aware
    /// across any sequence of pool-size changes.</summary>
    public double ExpectedPairCount(int a, int b)
    {
        int eligibleFrom = Math.Max(Pool.EligibleFromIndex(a), Pool.EligibleFromIndex(b));
        double pairNumerator = PickCount * (PickCount - 1.0);
        return pairNumerator * Pool.PairFactorBetween(eligibleFrom, DrawCount);
    }

    /// <summary>Cached pair synergy matrix for fast combination search.</summary>
    public double[,] GetPairSynergyMatrix()
    {
        if (pairSynergyCache is not null) return pairSynergyCache;
        int p = Pool.PoolSize;
        var matrix = new double[p + 1, p + 1];
        for (int a = 1; a <= p; a++)
        {
            for (int b = a + 1; b <= p; b++)
            {
                double observed = PairCounts[a, b];
                double expected = ExpectedPairCount(a, b);
                double val = Math.Clamp((observed - expected) / Math.Sqrt(expected + 1.0), -2.0, 2.0);
                matrix[a, b] = val;
                matrix[b, a] = val;
            }
        }
        pairSynergyCache = matrix;
        return matrix;
    }
}
