import type {
  BacktestingDto,
  BestOfLinesDto,
  DrawDto,
  DrawHistoryDto,
  LearningDto,
  PredictionDto,
  PredictionLinesDto,
  StatisticsDto,
  LotteryKey,
} from "./types";

let selectedLottery: LotteryKey = "uk-lotto";

export function setApiLottery(lottery: LotteryKey) {
  selectedLottery = lottery;
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const headers = new Headers(init?.headers);
  headers.set("X-Lottery", selectedLottery);
  const res = await fetch(url, { ...init, headers });
  if (res.status === 404) return null as T;
  if (!res.ok) {
    let message = `Request failed (${res.status})`;
    try {
      const body = await res.json();
      if (body?.errors) message = (body.errors as string[]).join(" ");
    } catch {
      /* keep default message */
    }
    throw new Error(message);
  }
  return res.json() as Promise<T>;
}

export const api = {
  statistics: () => request<StatisticsDto>("/api/statistics"),
  backtesting: () => request<BacktestingDto>("/api/backtesting"),
  latestDraw: () => request<DrawDto | null>("/api/draws/latest"),
  drawHistory: (loadAll = false) =>
    request<DrawHistoryDto>(`/api/draws/history?limit=100&loadAll=${loadAll}`),
  addDraw: (numbers: number[], bonus: number | null = null) =>
    request<DrawDto>("/api/draws", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ numbers, bonus }),
    }),
  addDrawRounds: (
    rounds: number[][],
    bonuses: (number | null)[],
    luckyStars: number[][] = [],
  ) =>
    request<DrawDto[]>("/api/draws/rounds", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ rounds, bonuses, luckyStars }),
    }),
  addLatestRound: (numbers: number[], bonus: number | null = null) =>
    request<DrawDto>("/api/draws/latest/round", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ numbers, bonus }),
    }),
  updateDraw: (
    id: number,
    numbers: number[],
    bonus: number | null,
    luckyStars: number[] = [],
  ) =>
    request<DrawDto>(`/api/draws/${id}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ numbers, bonus, luckyStars }),
    }),
  latestPrediction: () =>
    request<PredictionDto | null>("/api/predictions/latest"),
  generatePrediction: (excludeLastDrawNumbers = false) =>
    request<PredictionDto>("/api/predictions/generate", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ excludeLastDrawNumbers }),
    }),
  predictionLines: (count = 50, excludeLastDrawNumbers = false) =>
    request<PredictionLinesDto>(
      `/api/predictions/lines?count=${count}&excludeLastDrawNumbers=${excludeLastDrawNumbers}`,
    ),
  bestOfLines: (count = 50) =>
    request<BestOfLinesDto>(`/api/predictions/lines/best?count=${count}`),
  predictionHistory: () => request<PredictionDto[]>("/api/predictions/history"),
  learning: () => request<LearningDto>("/api/learning"),
};
