namespace LottoPredictor.Core.Analysis;

/// <summary>A named, weighted scoring configuration. Weights are combined over z-scored features.
/// The candidate list is deliberately explicit: each strategy is a hypothesis that walk-forward
/// backtesting either supports or rejects.</summary>
public sealed record ScoringStrategy(
    string Name,
    double WLongTerm,
    double WRecent,
    double WGap,
    double WMomentum,
    double PairWeight,
    double PenaltyWeight,
    double WBias = 0.0) // weight on the Bayesian bias z-score (physical-bias detector)
{
    public string Version => $"v2/{Name}";

    public string Describe() =>
        $"long-term={WLongTerm:0.##}, recent={WRecent:0.##}, gap={WGap:0.##}, momentum={WMomentum:0.##}, " +
        $"bias={WBias:0.##}, pair={PairWeight:0.##}, typicality-penalty={PenaltyWeight:0.##}";

    public static IReadOnlyList<ScoringStrategy> Candidates { get; } =
    [
        new("long-term-frequency", 1.0, 0.0, 0.0, 0.0, 0.25, 1.0),
        new("recent-hot",          0.0, 1.0, 0.0, 0.0, 0.25, 1.0),
        new("overdue-gap",         0.0, 0.0, 1.0, 0.0, 0.25, 1.0),
        new("momentum",            0.0, 0.0, 0.0, 1.0, 0.25, 1.0),
        new("bias-detector",       0.0, 0.0, 0.0, 0.0, 0.25, 1.0, 1.0),
        new("blend-freq-recent",   0.5, 0.3, 0.0, 0.2, 0.25, 1.0),
        new("blend-freq-gap",      0.5, 0.2, 0.3, 0.0, 0.25, 1.0),
        new("blend-uniform",       0.2, 0.2, 0.2, 0.2, 0.25, 1.0, 0.2),
    ];
}
