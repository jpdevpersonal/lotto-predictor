namespace LottoPredictor.Core.Analysis;

/// <summary>Lightweight, immutable view of a draw used by the analysis engine.
/// Numbers are always sorted ascending.</summary>
public sealed record DrawEvent(int Sequence, int DrawNumber, DateOnly Date, int[] Numbers, int? Bonus = null);

/// <summary>Describes the ball pool inferred from the data. The UK-style dataset switched from a
/// 1-49 pool to a 1-59 pool partway through; numbers 50+ are only "eligible" from the first draw
/// that contains a number above 49.</summary>
public sealed class PoolInfo
{
    public int PoolSize { get; }

    /// <summary>Index (into the ordered draw list) of the first draw belonging to the larger pool.
    /// Equal to the draw count when the dataset only ever used one pool.</summary>
    public int Era2StartIndex { get; }

    public int DrawCount { get; }

    private PoolInfo(int poolSize, int era2StartIndex, int drawCount)
    {
        PoolSize = poolSize;
        Era2StartIndex = era2StartIndex;
        DrawCount = drawCount;
    }

    public static PoolInfo Detect(
        IReadOnlyList<DrawEvent> draws,
        int? configuredPoolSize = null,
        DateOnly? poolExpansionDate = null)
    {
        int pickCount = draws.Count > 0 ? draws[0].Numbers.Length : 0;
        int observedMax = 0;
        int era2Start = draws.Count;
        for (int i = 0; i < draws.Count; i++)
        {
            foreach (var v in draws[i].Numbers)
            {
                if (v > observedMax) observedMax = v;
                if (v > 49 && i < era2Start) era2Start = i;
            }
        }
        int poolSize = configuredPoolSize ?? observedMax;
        if (poolSize < observedMax)
            throw new InvalidOperationException(
                $"Observed ball {observedMax} exceeds configured pool size {poolSize}.");
        if (poolSize <= 49) era2Start = draws.Count;
        else if (poolExpansionDate is DateOnly expansionDate)
        {
            era2Start = 0;
            while (era2Start < draws.Count && draws[era2Start].Date < expansionDate)
                era2Start++;
        }
        else if (pickCount != 6) era2Start = 0;
        return new PoolInfo(Math.Max(poolSize, 1), era2Start, draws.Count);
    }

    /// <summary>First draw index at which the given number could have been drawn.</summary>
    public int EligibleFromIndex(int number) => number <= 49 ? 0 : Era2StartIndex;

    /// <summary>Pool size in force for the draw at the given index.</summary>
    public int PoolAt(int index) =>
        PoolSize > 49 && Era2StartIndex > 0 && index < Era2StartIndex ? 49 : PoolSize;
}
