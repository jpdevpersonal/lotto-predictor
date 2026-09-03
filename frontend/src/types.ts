export interface DrawDto {
  id: number;
  sequence: number;
  drawNumber: number;
  date: string;
  numbers: number[];
  bonus: number | null;
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
  cutoffSequence: number;
  cutoffDrawNumber: number;
  modelVersion: string;
  strategyName: string;
  actualNumbers: number[] | null;
  matches: number | null;
  explanation: NumberExplanationDto[] | null;
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
