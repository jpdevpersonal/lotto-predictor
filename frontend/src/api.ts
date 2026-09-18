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
const mutationApiKey = import.meta.env.VITE_MUTATION_API_KEY;
const mutatingMethods = new Set(["POST", "PUT", "PATCH", "DELETE"]);

export function setApiLottery(lottery: LotteryKey) {
  selectedLottery = lottery;
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const headers = new Headers(init?.headers);
  const method = init?.method?.toUpperCase() ?? "GET";
  headers.set("X-Lottery", selectedLottery);
  if (mutationApiKey && mutatingMethods.has(method)) {
    headers.set("X-Api-Key", mutationApiKey);
  }
  const res = await fetch(url, { ...init, headers });
  if (res.status === 404) return null as T;
  if (!res.ok) {
    let message = defaultErrorMessage(res.status, method);
    try {
      const body = await res.json();
      if (body?.errors) message = (body.errors as string[]).join(" ");
      else if (body?.title) message = body.title;
    } catch {
      /* keep default message */
    }
    throw new Error(message);
  }
  return res.json() as Promise<T>;
}

function defaultErrorMessage(status: number, method: string) {
  if (mutatingMethods.has(method)) {
    if (status === 503) return "Mutation API key is not configured on the server.";
    if (status === 401) return "Mutation API key is missing. Restart the app with VITE_MUTATION_API_KEY configured.";
    if (status === 403) return "Mutation API key is invalid. Check that frontend and backend keys match.";
  }
  return `Request failed (${status})`;
}

export const api = {
  statistics: () => request<StatisticsDto>("/api/statistics"),
  backtesting: () => request<BacktestingDto>("/api/backtesting"),
  latestDraw: () => request<DrawDto | null>("/api/draws/latest"),
  drawHistory: (offset = 0, limit = 100) =>
    request<DrawHistoryDto>(`/api/draws/history?offset=${offset}&limit=${limit}`),
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
    drawNumber: number | null = null,
    date: string | null = null,
  ) =>
    request<DrawDto[]>("/api/draws/rounds", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ rounds, bonuses, luckyStars, drawNumber, date }),
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
    drawNumber: number | null = null,
    date: string | null = null,
  ) =>
    request<DrawDto>(`/api/draws/${id}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ numbers, bonus, luckyStars, drawNumber, date }),
    }),
  latestPrediction: () =>
    request<PredictionDto | null>("/api/predictions/latest"),
  generatePrediction: (excludeLastDrawNumbers = false) =>
    request<PredictionDto>("/api/predictions/generate", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ excludeLastDrawNumbers }),
    }),
  predictionLines: (count = 1, excludeLastDrawNumbers = false) =>
    request<PredictionLinesDto>(
      `/api/predictions/lines?count=${count}&excludeLastDrawNumbers=${excludeLastDrawNumbers}`,
    ),
  bestOfLines: (count = 50) =>
    request<BestOfLinesDto>(`/api/predictions/lines/best?count=${count}`),
  predictionHistory: () => request<PredictionDto[]>("/api/predictions/history"),
  learning: () => request<LearningDto>("/api/learning"),
};
