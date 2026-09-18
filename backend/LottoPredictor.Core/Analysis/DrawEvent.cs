namespace LottoPredictor.Core.Analysis;

/// <summary>Lightweight, immutable view of a draw used by the analysis engine.
/// Numbers are always sorted ascending.</summary>
public sealed record DrawEvent(int Sequence, int DrawNumber, DateOnly Date, int[] Numbers, int? Bonus = null);

public sealed record PoolRule(DateOnly EffectiveFrom, int PoolSize);

/// <summary>Describes the ball pool inferred from the data. The UK-style dataset switched from a
/// 1-49 pool to a 1-59 pool partway through; numbers 50+ are only "eligible" from the first draw
/// that contains a number above 49.</summary>
public sealed class PoolInfo
{
    public int PoolSize { get; }
    public int NextPoolSize { get; }

    /// <summary>Index (into the ordered draw list) of the first draw belonging to the larger pool.
    /// Equal to the draw count when the dataset only ever used one pool.</summary>
    public int Era2StartIndex { get; }

    public int DrawCount { get; }
    private readonly int[] poolByIndex;
    private readonly int[] eligibleFromByNumber;

    private PoolInfo(
        int poolSize,
        int nextPoolSize,
        int era2StartIndex,
        int drawCount,
        int[] poolByIndex,
        int[] eligibleFromByNumber)
    {
        PoolSize = poolSize;
        NextPoolSize = nextPoolSize;
        Era2StartIndex = era2StartIndex;
        DrawCount = drawCount;
        this.poolByIndex = poolByIndex;
        this.eligibleFromByNumber = eligibleFromByNumber;
    }

    public static PoolInfo Detect(
        IReadOnlyList<DrawEvent> draws,
        int? configuredPoolSize = null,
        DateOnly? poolExpansionDate = null,
        DateOnly? targetDrawDate = null,
        IReadOnlyList<PoolRule>? ruleEras = null)
    {
        int pickCount = draws.Count > 0 ? draws[0].Numbers.Length : 0;
        int observedMax = 0;
        for (int i = 0; i < draws.Count; i++)
        {
            foreach (var v in draws[i].Numbers)
            {
                if (v > observedMax) observedMax = v;
            }
        }
        int poolSize = configuredPoolSize ?? observedMax;
        var eras = BuildEras(poolSize, poolExpansionDate, ruleEras, draws, observedMax, configuredPoolSize is null);
        poolSize = Math.Max(poolSize, eras.Max(era => era.PoolSize));
        if (poolSize < observedMax)
            throw new InvalidOperationException(
                $"Observed ball {observedMax} exceeds configured pool size {poolSize}.");

        var poolByIndex = new int[draws.Count];
        int era2Start = draws.Count;
        int firstPool = eras[0].PoolSize;
        for (int i = 0; i < draws.Count; i++)
        {
            poolByIndex[i] = PoolForDate(eras, draws[i].Date);
            if (poolByIndex[i] != firstPool && era2Start == draws.Count) era2Start = i;
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
        return new PoolInfo(
            Math.Max(poolSize, 1), Math.Max(nextPoolSize, 1), era2Start, draws.Count,
            poolByIndex, eligibleFrom);
    }

    /// <summary>First draw index at which the given number could have been drawn.</summary>
    public int EligibleFromIndex(int number) =>
        number >= 0 && number < eligibleFromByNumber.Length ? eligibleFromByNumber[number] : DrawCount;

    /// <summary>Pool size in force for the draw at the given index.</summary>
    public int PoolAt(int index) => poolByIndex.Length == 0
        ? NextPoolSize
        : poolByIndex[Math.Clamp(index, 0, poolByIndex.Length - 1)];

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
