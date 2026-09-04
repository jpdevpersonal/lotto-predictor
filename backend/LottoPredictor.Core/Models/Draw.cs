namespace LottoPredictor.Core.Models;

/// <summary>One drawn set of six numbers. A single draw number in the source CSV can
/// contain two independent draw events (two machines drawn on the same night); each is stored
/// as its own row with its own global Sequence.</summary>
public class Draw
{
    public int Id { get; set; }

    /// <summary>1-based chronological position across the whole dataset (oldest = 1).</summary>
    public int Sequence { get; set; }

    /// <summary>Draw number from the source data. Not unique: recent draw events share numbers.</summary>
    public int DrawNumber { get; set; }

    public DateOnly Date { get; set; }
    public int N1 { get; set; }
    public int N2 { get; set; }
    public int N3 { get; set; }
    public int N4 { get; set; }
    public int N5 { get; set; }
    public int? N6 { get; set; }
    public int? Bonus { get; set; }
    public int? Bonus2 { get; set; }
    public long Jackpot { get; set; }
    public int Wins { get; set; }
    public string Machine { get; set; } = "";
    public string BallSet { get; set; } = "";
    public string Source { get; set; } = "csv";

    public int[] Numbers() => N6.HasValue
        ? [N1, N2, N3, N4, N5, N6.Value]
        : [N1, N2, N3, N4, N5];

    public int[] BonusNumbers() => new[] { Bonus, Bonus2 }
        .Where(number => number.HasValue)
        .Select(number => number!.Value)
        .ToArray();

    public void SetNumbers(IReadOnlyList<int> numbers)
    {
        if (numbers.Count is not (5 or 6))
            throw new ArgumentException("A draw must contain five or six main numbers.", nameof(numbers));

        var sorted = numbers.OrderBy(x => x).ToArray();
        N1 = sorted[0]; N2 = sorted[1]; N3 = sorted[2];
        N4 = sorted[3]; N5 = sorted[4]; N6 = sorted.Length == 6 ? sorted[5] : null;
    }
}
