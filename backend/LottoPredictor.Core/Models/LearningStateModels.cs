namespace LottoPredictor.Core.Models;

/// <summary>Persistent multiplicative weight of one strategy in the live hedge ensemble.</summary>
public class HedgeWeightState
{
    public int Id { get; set; }
    public string StrategyName { get; set; } = "";
    public double LogWeight { get; set; }
    public int UpdatedThroughSequence { get; set; }
}

/// <summary>Weights and AdaGrad accumulators of the online per-ball conditional-logit model.</summary>
public class OnlineModelState
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string WeightsCsv { get; set; } = "";
    public string AccumulatorsCsv { get; set; } = "";
    public int Updates { get; set; }
    public int UpdatedThroughSequence { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>Small key/value store for learning decisions (active strategy, promotion point).</summary>
public class LearningStateEntry
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
