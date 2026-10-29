namespace LottoPredictor.Core.Models;

/// <summary>A line frozen for one strategy using only draws up to <see cref="CutoffSequence"/>,
/// scored once the draw at CutoffSequence + 1 arrives. This prospective ledger is the only data
/// used to rank learned strategies, update the persistent hedge and promote an active strategy.</summary>
public class ShadowPrediction
{
    public int Id { get; set; }
    public string StrategyName { get; set; } = "";
    /// <summary>builtin, learned, online or ensemble.</summary>
    public string StrategyKind { get; set; } = "";
    public string Weights { get; set; } = "";
    public int CutoffSequence { get; set; }
    public string NumbersCsv { get; set; } = "";
    /// <summary>Per-ball scores for balls 1..N, standardised to mean 0 and unit variance.</summary>
    public string ScoresCsv { get; set; } = "";
    /// <summary>Exact probability that a uniformly random line matches 3+ numbers.</summary>
    public double RandomThreePlusProbability { get; set; }
    public double RandomExpectedMatches { get; set; }
    public DateTime CreatedUtc { get; set; }

    public int? TargetDrawId { get; set; }
    public int? Matches { get; set; }
    public bool? ThreePlus { get; set; }
    /// <summary>Standardised sum of the drawn balls' scores; exactly mean 0, variance 1 under
    /// fair draws regardless of the strategy.</summary>
    public double? ScoreZ { get; set; }
    public double? Reward { get; set; }
    public DateTime? ScoredUtc { get; set; }
    /// <summary>Set when a draw at or before the cutoff was edited after freezing.</summary>
    public bool Invalid { get; set; }

    public int[] Numbers() => NumbersCsv.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(int.Parse).ToArray();

    public double[] Scores() => ScoresCsv.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
}
