namespace LottoPredictor.Core.Models;

public class Prediction
{
    public int Id { get; set; }
    public DateTime CreatedUtc { get; set; }
    public int P1 { get; set; }
    public int P2 { get; set; }
    public int P3 { get; set; }
    public int P4 { get; set; }
    public int P5 { get; set; }
    public int? P6 { get; set; }
    public string? LuckyStarsCsv { get; set; }

    /// <summary>Sequence of the last draw the prediction was allowed to see.</summary>
    public int CutoffSequence { get; set; }
    public int CutoffDrawNumber { get; set; }
    public string ModelVersion { get; set; } = "";
    public string StrategyName { get; set; } = "";

    /// <summary>Comma separated actual numbers once the next draw is known.</summary>
    public string? ActualNumbersCsv { get; set; }
    public string? ActualLuckyStarsCsv { get; set; }
    public int? Matches { get; set; }
    public int? LuckyStarMatches { get; set; }
    public int? EvaluatedDrawId { get; set; }
    public DateTime? EvaluatedUtc { get; set; }

    public int[] Numbers() => P6.HasValue
        ? [P1, P2, P3, P4, P5, P6.Value]
        : [P1, P2, P3, P4, P5];

    public int[] LuckyStars() => string.IsNullOrWhiteSpace(LuckyStarsCsv)
        ? []
        : LuckyStarsCsv.Split(',').Select(int.Parse).ToArray();

    public void SetNumbers(IReadOnlyList<int> numbers)
    {
        if (numbers.Count is not (5 or 6))
            throw new ArgumentException("A prediction must contain five or six main numbers.", nameof(numbers));

        var sorted = numbers.OrderBy(x => x).ToArray();
        P1 = sorted[0]; P2 = sorted[1]; P3 = sorted[2];
        P4 = sorted[3]; P5 = sorted[4]; P6 = sorted.Length == 6 ? sorted[5] : null;
    }
}
