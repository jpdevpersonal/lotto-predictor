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
    public int Generation { get; set; }
    public double AvgMatches { get; set; }
    public double RecencyWeightedAvg { get; set; }
    public int EvaluatedDraws { get; set; }
    public DateTime CreatedUtc { get; set; }
}
