import type {
  BacktestingDto,
  DrawDto,
  DrawHistoryDto,
  LearningDto,
  PredictionDto,
  StatisticsDto,
} from "./types";

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, init);
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
  addDraw: (numbers: number[]) =>
    request<DrawDto>("/api/draws", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ numbers }),
    }),
  addDrawRounds: (rounds: number[][]) =>
    request<DrawDto[]>("/api/draws/rounds", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ rounds }),
    }),
  addLatestRound: (numbers: number[]) =>
    request<DrawDto>("/api/draws/latest/round", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ numbers }),
    }),
  updateDraw: (id: number, numbers: number[]) =>
    request<DrawDto>(`/api/draws/${id}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ numbers }),
    }),
  latestPrediction: () =>
    request<PredictionDto | null>("/api/predictions/latest"),
  generatePrediction: () =>
    request<PredictionDto>("/api/predictions/generate", { method: "POST" }),
  predictionHistory: () => request<PredictionDto[]>("/api/predictions/history"),
  learning: () => request<LearningDto>("/api/learning"),
};
