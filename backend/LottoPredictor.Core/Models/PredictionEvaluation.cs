namespace LottoPredictor.Core.Models;

public class PredictionEvaluation
{
    public int Id { get; set; }
    public int PredictionId { get; set; }
    public Prediction Prediction { get; set; } = null!;
    public int EvaluatedDrawId { get; set; }
    public Draw EvaluatedDraw { get; set; } = null!;
    public string ActualNumbersCsv { get; set; } = "";
    public int Matches { get; set; }
    public int? BonusMatches { get; set; }
    public string? ActualLuckyStarsCsv { get; set; }
    public int? LuckyStarMatches { get; set; }
    public DateTime EvaluatedUtc { get; set; }
}