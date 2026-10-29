namespace LottoPredictor.Core.Analysis;

/// <summary>Lightweight, immutable view of a draw used by the analysis engine.
/// Numbers are always sorted ascending.</summary>
public sealed record DrawEvent(
    int Sequence,
    int DrawNumber,
    DateOnly Date,
    int[] Numbers,
    int? Bonus = null,
    string? Machine = null,
    string? BallSet = null);

public sealed record PoolRule(DateOnly EffectiveFrom, int PoolSize);

/// <summary>Describes the ball pool inferred from the data. The UK-style dataset switched from a
/// 1-49 pool to a 1-59 pool partway through; numbers 50+ are only "eligible" from the first draw
/// that contains a number above 49.</summary>
public sealed class PoolInfo
{
    public int PoolSize { get; }
    public int NextPoolSize { get; }

    /// <summary>Index (into the ordered draw list) of the first draw belonging to a larger pool
    /// than the initial era. Equal to the draw count when the dataset only ever used one pool.</summary>
    public int Era2StartIndex { get; }

    /// <summary>Index of the first draw belonging to the current (latest) pool size era.</summary>
    public int CurrentEraStartIndex { get; }

    public int DrawCount { get; }
    private readonly int[] poolByIndex;
    private readonly int[] eligibleFromByNumber;
    private readonly double[] invPoolPrefix;
    private readonly double[] pairFactorPrefix;

    private PoolInfo(
        int poolSize,
        int nextPoolSize,
        int era2StartIndex,
        int currentEraStartIndex,
        int drawCount,
        int[] poolByIndex,
        int[] eligibleFromByNumber,
        double[] invPoolPrefix,
        double[] pairFactorPrefix)
    {
        PoolSize = poolSize;
        NextPoolSize = nextPoolSize;
        Era2StartIndex = era2StartIndex;
        CurrentEraStartIndex = currentEraStartIndex;
        DrawCount = drawCount;
        this.poolByIndex = poolByIndex;
        this.eligibleFromByNumber = eligibleFromByNumber;
        this.invPoolPrefix = invPoolPrefix;
        this.pairFactorPrefix = pairFactorPrefix;
    }

    public static PoolInfo Detect(
        IReadOnlyList<DrawEvent> draws,
        int? configuredPoolSize = null,
        DateOnly? poolExpansionDate = null,
        DateOnly? targetDrawDate = null,
        IReadOnlyList<PoolRule>? ruleEras = null)
    {
        int observedMax = 0;
        for (int i = 0; i < draws.Count; i++)
        {
            var nums = draws[i].Numbers;
            for (int j = 0; j < nums.Length; j++)
            {
                if (nums[j] > observedMax) observedMax = nums[j];
            }
        }
        int poolSize = configuredPoolSize ?? observedMax;
        var eras = BuildEras(poolSize, poolExpansionDate, ruleEras, draws, observedMax, configuredPoolSize is null);
        for (int i = 0; i < eras.Count; i++)
            if (eras[i].PoolSize > poolSize) poolSize = eras[i].PoolSize;
        if (poolSize < observedMax)
            throw new InvalidOperationException(
                $"Observed ball {observedMax} exceeds configured pool size {poolSize}.");

        var poolByIndex = new int[draws.Count];
        var invPoolPrefix = new double[draws.Count + 1];
        var pairFactorPrefix = new double[draws.Count + 1];
        int era2Start = draws.Count;
        int firstPool = eras[0].PoolSize;
        for (int i = 0; i < draws.Count; i++)
        {
            int p = PoolForDate(eras, draws[i].Date);
            poolByIndex[i] = p;
            if (p != firstPool && era2Start == draws.Count) era2Start = i;
            invPoolPrefix[i + 1] = invPoolPrefix[i] + 1.0 / Math.Max(1, p);
            pairFactorPrefix[i + 1] = pairFactorPrefix[i] + 1.0 / (Math.Max(1.0, p) * Math.Max(1.0, p - 1.0));
        }

        var eligibleFrom = new int[Math.Max(poolSize, 1) + 1];
        for (int number = 1; number < eligibleFrom.Length; number++)
        {
            int index = 0;
            while (index < draws.Count && poolByIndex[index] < number) index++;
            eligibleFrom[number] = index;
        }

        int nextPoolSize = targetDrawDate is DateOnly target
            ? PoolForDate(eras, target)
            : draws.Count > 0 ? poolByIndex[^1] : eras[^1].PoolSize;
        int currentEraStart = 0;
        for (int i = draws.Count - 1; i >= 0; i--)
        {
            if (poolByIndex[i] != nextPoolSize)
            {
                currentEraStart = i + 1;
                break;
            }
        }

        return new PoolInfo(
            Math.Max(poolSize, 1), Math.Max(nextPoolSize, 1), era2Start, currentEraStart, draws.Count,
            poolByIndex, eligibleFrom, invPoolPrefix, pairFactorPrefix);
    }

    /// <summary>First draw index at which the given number could have been drawn.</summary>
    public int EligibleFromIndex(int number) =>
        number >= 0 && number < eligibleFromByNumber.Length ? eligibleFromByNumber[number] : DrawCount;

    /// <summary>Pool size in force for the draw at the given index.</summary>
    public int PoolAt(int index) => poolByIndex.Length == 0
        ? NextPoolSize
        : poolByIndex[Math.Clamp(index, 0, poolByIndex.Length - 1)];

    /// <summary>Expected appearances of an eligible ball across draw indices [startIndex, endIndex).</summary>
    public double ExpectedAppearances(int startIndex, int endIndex, int pickCount)
    {
        int s = Math.Clamp(startIndex, 0, DrawCount);
        int e = Math.Clamp(endIndex, s, DrawCount);
        return pickCount * (invPoolPrefix[e] - invPoolPrefix[s]);
    }

    /// <summary>Sum of 1 / (PoolAt(i) * (PoolAt(i) - 1)) across draw indices [startIndex, endIndex).</summary>
    public double PairFactorBetween(int startIndex, int endIndex)
    {
        int s = Math.Clamp(startIndex, 0, DrawCount);
        int e = Math.Clamp(endIndex, s, DrawCount);
        return pairFactorPrefix[e] - pairFactorPrefix[s];
    }

    private static List<PoolRule> BuildEras(
        int configuredPoolSize,
        DateOnly? poolExpansionDate,
        IReadOnlyList<PoolRule>? ruleEras,
        IReadOnlyList<DrawEvent> draws,
        int observedMax,
        bool inferredPoolSize)
    {
        if (ruleEras is { Count: > 0 })
            return ruleEras.OrderBy(era => era.EffectiveFrom).ToList();

        if (configuredPoolSize > 49 && poolExpansionDate is DateOnly expansionDate)
            return [new PoolRule(DateOnly.MinValue, 49), new PoolRule(expansionDate, configuredPoolSize)];

        if (inferredPoolSize && observedMax > 49)
        {
            int firstExpandedIndex = -1;
            for (int i = 0; i < draws.Count && firstExpandedIndex < 0; i++)
                if (draws[i].Numbers.Any(number => number > 49)) firstExpandedIndex = i;
            if (firstExpandedIndex >= 0)
                return [
                    new PoolRule(DateOnly.MinValue, 49),
                    new PoolRule(draws[firstExpandedIndex].Date, configuredPoolSize),
                ];
        }

        return [new PoolRule(DateOnly.MinValue, Math.Max(configuredPoolSize, 1))];
    }

    private static int PoolForDate(IReadOnlyList<PoolRule> eras, DateOnly date)
    {
        int pool = eras[0].PoolSize;
        foreach (var era in eras)
        {
            if (date < era.EffectiveFrom) break;
            pool = era.PoolSize;
        }
        return pool;
    }
}
