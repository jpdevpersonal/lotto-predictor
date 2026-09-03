namespace LottoPredictor.Core.Models;

/// <summary>One row per strategy per analysis rebuild: tracks how every strategy (hand-written
/// and learned) performs as the dataset grows, so improvement over time is auditable.</summary>
public class StrategyPerformanceLog
{
    public int Id { get; set; }
    public DateTime LoggedUtc { get; set; }
    public int DrawCount { get; set; }
    public string StrategyName { get; set; } = "";
    public double AvgMatches { get; set; }
    public double RecencyWeightedAvg { get; set; }
    public double RandomExpected { get; set; }
    public bool WasActive { get; set; }
}
