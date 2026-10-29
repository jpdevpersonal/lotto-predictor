namespace LottoPredictor.Core.Models;

/// <summary>A strategy whose weights were discovered by the optimizer rather than hand-written.
/// Persisted so learning accumulates across restarts and new draws.</summary>
public class LearnedStrategy
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public double WLongTerm { get; set; }
    public double WRecent { get; set; }
    public double WGap { get; set; }
    public double WMomentum { get; set; }
    public double PairWeight { get; set; }
    public double PenaltyWeight { get; set; }
    public double WBias { get; set; }
    public double WBonus { get; set; }
    public int Generation { get; set; }
    public double AvgMatches { get; set; }
    public double RecencyWeightedAvg { get; set; }
    public int EvaluatedDraws { get; set; }
    public DateTime CreatedUtc { get; set; }
    /// <summary>Latest draw sequence known when the strategy was created. Only later draws,
    /// scored through the shadow ledger, count as evidence for it.</summary>
    public int BornAfterSequence { get; set; }
    public string Status { get; set; } = LearnedStatus.Challenger;
    public DateTime? RetiredUtc { get; set; }
}

public static class LearnedStatus
{
    public const string Challenger = "Challenger";
    public const string Champion = "Champion";
    public const string Retired = "Retired";
}
