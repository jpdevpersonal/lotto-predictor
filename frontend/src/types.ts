export type LotteryKey = "uk-lotto" | "euromillions";

export interface LotteryProfile {
  key: LotteryKey;
  name: string;
  mainNumberCount: number;
  mainPoolSize: number;
  luckyStarCount: number;
  luckyStarPoolSize: number;
  roundCount: number;
}

export const LOTTERIES: Record<LotteryKey, LotteryProfile> = {
  "uk-lotto": {
    key: "uk-lotto",
    name: "UK National Lottery",
    mainNumberCount: 6,
    mainPoolSize: 59,
    luckyStarCount: 0,
    luckyStarPoolSize: 0,
    roundCount: 2,
  },
  euromillions: {
    key: "euromillions",
    name: "EuroMillions",
    mainNumberCount: 5,
    mainPoolSize: 50,
    luckyStarCount: 2,
    luckyStarPoolSize: 12,
    roundCount: 1,
  },
};

export interface DrawDto {
  id: number;
  sequence: number;
  drawNumber: number;
  date: string;
  numbers: number[];
  bonus: number | null;
  luckyStars: number[];
  machine: string;
  ballSet: string;
  source: string;
}

export interface DrawHistoryDto {
  items: DrawDto[];
  total: number;
  offset: number;
  limit: number;
}

export interface NumberExplanationDto {
  number: number;
  overallFrequency: number;
  overallRate: number;
  count10: number;
  count25: number;
  count50: number;
  count100: number;
  drawsSinceSeen: number;
  averageGap: number;
  gapRatio: number;
  modelScore: number;
}

export interface PredictionDto {
  id: number;
  createdUtc: string;
  numbers: number[];
  luckyStars: number[];
  cutoffSequence: number;
  cutoffDrawNumber: number;
  modelVersion: string;
  strategyName: string;
  actualNumbers: number[] | null;
  matches: number | null;
  actualLuckyStars: number[] | null;
  luckyStarMatches: number | null;
  explanation: NumberExplanationDto[] | null;
}

export interface PredictionLineDto {
  rank: number;
  numbers: number[];
  luckyStars: number[];
  score: number;
}

export interface PredictionLinesDto {
  strategyName: string;
  cutoffDrawNumber: number;
  lines: PredictionLineDto[];
}

export interface BestOfLinesDto {
  numbers: number[];
  frequencies: number[];
  luckyStars: number[];
  luckyStarFrequencies: number[];
  linesConsidered: number;
  strategyName: string;
  cutoffDrawNumber: number;
}

export interface StrategyBacktestDto {
  name: string;
  weights: string;
  evaluated: number;
  avgMatches: number;
  recencyWeightedAvg: number;
  pct0: number;
  pct1: number;
  pct2: number;
  pct3Plus: number;
  isBest: boolean;
  isLearned: boolean;
}

export interface BacktestingDto {
  evaluatedDraws: number;
  warmupDraws: number;
  strategies: StrategyBacktestDto[];
  activeStrategyName: string;
  randomExpectedMatches: number;
  randomSimulatedAvgMatches: number;
  randomPct0: number;
  randomPct1: number;
  randomPct2: number;
  randomPct3Plus: number;
  verdict: string;
}

export interface StatisticsDto {
  drawCount: number;
  poolSize: number;
  poolChangeDrawNumber: number | null;
  latestDraw: DrawDto | null;
  latestRounds: DrawDto[];
}

export interface LearnedStrategyDto {
  name: string;
  weights: string;
  generation: number;
  avgMatches: number;
  recencyWeightedAvg: number;
  evaluatedDraws: number;
  createdUtc: string;
}

export interface PerformancePointDto {
  drawCount: number;
  loggedUtc: string;
  strategyName: string;
  avgMatches: number;
  recencyWeightedAvg: number;
  randomExpected: number;
  wasActive: boolean;
}

export interface UniformityDto {
  chiSquare: number;
  degreesOfFreedom: number;
  pValue: number;
  windowDraws: number;
  assessment: string;
}

export interface HedgeWeightDto {
  strategyName: string;
  weight: number;
}

export interface LearningDto {
  generation: number;
  activeStrategyName: string;
  activeWeights: string;
  activeIsLearned: boolean;
  uniformity: UniformityDto;
  hedgeWeights: HedgeWeightDto[];
  learnedStrategies: LearnedStrategyDto[];
  history: PerformancePointDto[];
}
