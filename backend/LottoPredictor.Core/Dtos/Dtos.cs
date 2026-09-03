namespace LottoPredictor.Core.Dtos;

public record DrawDto(
    int Id,
    int Sequence,
    int DrawNumber,
    string Date,
    int[] Numbers,
    int? Bonus,
    string Machine,
    string BallSet,
    string Source);

public record AddDrawRequest(int[] Numbers, int? Bonus = null);

public record AddDrawRoundsRequest(int[][] Rounds, int?[]? Bonuses = null);

public record UpdateDrawRequest(int[] Numbers, int? Bonus = null);

public record DrawHistoryDto(
    IReadOnlyList<DrawDto> Items,
    int Total,
    int Offset,
    int Limit);

public record NumberExplanationDto(
    int Number,
    int OverallFrequency,
    double OverallRate,
    int Count10,
    int Count25,
    int Count50,
    int Count100,
    int DrawsSinceSeen,
    double AverageGap,
    double GapRatio,
    double ModelScore);

public record PredictionDto(
    int Id,
    DateTime CreatedUtc,
    int[] Numbers,
    int CutoffSequence,
    int CutoffDrawNumber,
    string ModelVersion,
    string StrategyName,
    int[]? ActualNumbers,
    int? Matches,
    IReadOnlyList<NumberExplanationDto>? Explanation);

public record PredictionLineDto(int Rank, int[] Numbers, double Score);

public record PredictionLinesDto(
    string StrategyName,
    int CutoffDrawNumber,
    IReadOnlyList<PredictionLineDto> Lines);

public record BestOfLinesDto(
    int[] Numbers,
    int[] Frequencies,
    int LinesConsidered,
    string StrategyName,
    int CutoffDrawNumber);

public record NumberStatsDto(
    int Number,
    int TotalCount,
    int EligibleDraws,
    double FreqRate,
    int Count10,
    int Count25,
    int Count50,
    int Count100,
    int DrawsSinceLast,
    double AvgGap,
    double GapRatio,
    double RecentVsLongTerm,
    int[] PositionCounts);

public record SetStatsDto(
    double SumMean, double SumStd,
    double RangeMean, double RangeStd,
    double OddMean, double OddStd,
    double ConsecMean, double ConsecStd,
    double LowHalfMean, double LowHalfStd);

public record StatisticsDto(
    int DrawCount,
    int PoolSize,
    int? PoolChangeDrawNumber,
    DrawDto? LatestDraw,
    IReadOnlyList<DrawDto> LatestRounds,
    IReadOnlyList<NumberStatsDto> Numbers,
    SetStatsDto SetStats);

public record StrategyBacktestDto(
    string Name,
    string Weights,
    int Evaluated,
    double AvgMatches,
    double RecencyWeightedAvg,
    double Pct0,
    double Pct1,
    double Pct2,
    double Pct3Plus,
    bool IsBest,
    bool IsLearned);

public record BacktestingDto(
    int EvaluatedDraws,
    int WarmupDraws,
    IReadOnlyList<StrategyBacktestDto> Strategies,
    string ActiveStrategyName,
    double RandomExpectedMatches,
    double RandomSimulatedAvgMatches,
    double RandomPct0,
    double RandomPct1,
    double RandomPct2,
    double RandomPct3Plus,
    string Verdict);

public record LearnedStrategyDto(
    string Name,
    string Weights,
    int Generation,
    double AvgMatches,
    double RecencyWeightedAvg,
    int EvaluatedDraws,
    DateTime CreatedUtc);

public record PerformancePointDto(
    int DrawCount,
    DateTime LoggedUtc,
    string StrategyName,
    double AvgMatches,
    double RecencyWeightedAvg,
    double RandomExpected,
    bool WasActive);

public record LearningDto(
    int Generation,
    string ActiveStrategyName,
    string ActiveWeights,
    bool ActiveIsLearned,
    UniformityDto Uniformity,
    IReadOnlyList<HedgeWeightDto> HedgeWeights,
    IReadOnlyList<LearnedStrategyDto> LearnedStrategies,
    IReadOnlyList<PerformancePointDto> History);

public record UniformityDto(
    double ChiSquare,
    int DegreesOfFreedom,
    double PValue,
    int WindowDraws,
    string Assessment);

public record HedgeWeightDto(string StrategyName, double Weight);
