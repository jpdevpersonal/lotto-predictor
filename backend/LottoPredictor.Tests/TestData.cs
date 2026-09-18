using LottoPredictor.Core.Analysis;

namespace LottoPredictor.Tests;

/// <summary>Helpers for building synthetic draw histories.</summary>
public static class TestData
{
    public static DrawEvent Ev(int sequence, params int[] numbers) =>
        new(sequence, sequence, new DateOnly(2020, 1, 1).AddDays(sequence), [.. numbers.OrderBy(x => x)]);

    /// <summary>Deterministic pseudo-random history of draws from a 1..poolSize pool.</summary>
    public static List<DrawEvent> RandomHistory(int count, int poolSize, int seed = 42, int pickCount = 6)
    {
        var rng = new Random(seed);
        var draws = new List<DrawEvent>(count);
        for (int i = 1; i <= count; i++)
        {
            var set = new HashSet<int>();
            while (set.Count < pickCount) set.Add(rng.Next(1, poolSize + 1));
            draws.Add(Ev(i, [.. set]));
        }
        return draws;
    }
}
