import { useState } from "react";
import type { ProspectiveLearningDto, ProspectiveSeriesDto } from "../types";

const COLORS = ["#2c3e78", "#c8952e", "#1e8a4c", "#c23b3b", "#7a4fb5", "#2b8fa3"];
const WIDTH = 640;
const HEIGHT = 220;
const PAD = { top: 12, right: 12, bottom: 24, left: 44 };

type Metric = "excess" | "scoreZ";

function ProspectiveChart({ series, metric }: { series: ProspectiveSeriesDto[]; metric: Metric }) {
  const value = (p: ProspectiveSeriesDto["points"][number]) =>
    metric === "excess" ? p.cumulativeExcessMatches : p.cumulativeScoreZ;
  const points = series.flatMap((s) => s.points);
  if (points.length === 0) return null;

  const xs = points.map((p) => p.cutoffSequence);
  const ys = points.map(value).concat(0);
  const xMin = Math.min(...xs);
  const xMax = Math.max(xMin + 1, ...xs);
  const yAbs = Math.max(1, ...ys.map(Math.abs));
  const plotW = WIDTH - PAD.left - PAD.right;
  const plotH = HEIGHT - PAD.top - PAD.bottom;
  const x = (v: number) => PAD.left + ((v - xMin) / (xMax - xMin)) * plotW;
  const y = (v: number) => PAD.top + ((yAbs - v) / (2 * yAbs)) * plotH;

  return (
    <figure className="prospective-chart">
      <svg
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        role="img"
        aria-label={
          metric === "excess"
            ? "Cumulative matches above random expectation per strategy"
            : "Cumulative ranking score z per strategy"
        }
      >
        <line x1={PAD.left} x2={WIDTH - PAD.right} y1={y(0)} y2={y(0)} className="chart-zero" />
        <text x={PAD.left - 6} y={y(yAbs) + 4} className="chart-label" textAnchor="end">
          {yAbs.toFixed(1)}
        </text>
        <text x={PAD.left - 6} y={y(0) + 4} className="chart-label" textAnchor="end">
          0
        </text>
        <text x={PAD.left - 6} y={y(-yAbs) + 4} className="chart-label" textAnchor="end">
          {(-yAbs).toFixed(1)}
        </text>
        <text x={PAD.left} y={HEIGHT - 6} className="chart-label">
          after draw {xMin}
        </text>
        <text x={WIDTH - PAD.right} y={HEIGHT - 6} className="chart-label" textAnchor="end">
          after draw {xMax}
        </text>
        {series.map((s, i) => (
          <polyline
            key={s.strategyName}
            fill="none"
            stroke={COLORS[i % COLORS.length]}
            strokeWidth={2}
            points={s.points.map((p) => `${x(p.cutoffSequence)},${y(value(p))}`).join(" ")}
          />
        ))}
      </svg>
      <figcaption className="chart-legend">
        {series.map((s, i) => (
          <span key={s.strategyName}>
            <span className="legend-swatch" style={{ background: COLORS[i % COLORS.length] }} />
            {s.strategyName}
          </span>
        ))}
      </figcaption>
    </figure>
  );
}

export function ProspectiveLearning({ prospective }: { prospective: ProspectiveLearningDto }) {
  const [metric, setMetric] = useState<Metric>("excess");
  const { selection, leaderboard, onlineModel } = prospective;
  const progress = Math.min(1, selection.prospectiveDraws / Math.max(1, selection.minProspectiveDraws));

  return (
    <>
      <h3>Prospective learning (real results only)</h3>
      <p className="muted">
        Before each draw, every live strategy freezes the line it would play. After the draw, those
        lines are scored, and the hedge weights, the online model and the challenger population learn
        from the result. Nothing is judged on a draw it could have seen, so this record is free of
        hindsight. <strong>{prospective.scoredDraws}</strong> draw(s) scored
        {prospective.lastScoredSequence ? ` (latest: draw ${prospective.lastScoredSequence})` : ""},{" "}
        {prospective.pendingLines} line(s) waiting for the next result.
      </p>

      <p className="verdict">
        Active strategy chosen by{" "}
        <strong>{selection.source === "prospective" ? "real results" : "the backtest"}</strong>
        {selection.source === "backtest" && (
          <>
            {" "}
            (real results take over after {selection.minProspectiveDraws} scored draws:{" "}
            {(progress * 100).toFixed(0)}% there)
          </>
        )}
        . {selection.reason}
      </p>

      {prospective.lastError && (
        <p className="error">Last learning update failed: {prospective.lastError}</p>
      )}

      {prospective.series.length > 0 && (
        <>
          <div className="inline-actions">
            <label>
              Chart{" "}
              <select value={metric} onChange={(e) => setMetric(e.target.value as Metric)}>
                <option value="excess">Cumulative matches above random</option>
                <option value="scoreZ">Cumulative ranking score (z)</option>
              </select>
            </label>
          </div>
          <ProspectiveChart series={prospective.series} metric={metric} />
          <p className="muted">
            The zero line is a fair machine. With no real edge, every line drifts around zero. The
            spread grows with √draws, so a short lead is almost always noise.
          </p>
        </>
      )}

      {leaderboard.length > 0 && (
        <div className="table-scroll">
          <table>
            <thead>
              <tr>
                <th>Strategy</th>
                <th>Kind</th>
                <th>Draws</th>
                <th>Avg matches (random)</th>
                <th>3+ hits (random)</th>
                <th title="Mean ranking z × √draws; about N(0,1) for a fair machine">Score z</th>
                <th title="Mean composite reward shrunk toward 0">Shrunk reward</th>
                <th>Hedge weight</th>
              </tr>
            </thead>
            <tbody>
              {leaderboard.slice(0, 20).map((r) => (
                <tr key={r.strategyName}>
                  <td>
                    {r.strategyName}
                    {r.strategyName === selection.activeName && <span className="badge">Active</span>}
                    {r.status === "Retired" && <span className="muted"> (retired)</span>}
                  </td>
                  <td>{r.kind}</td>
                  <td>{r.draws}</td>
                  <td>
                    {r.meanMatches.toFixed(3)} ({r.meanExpectedMatches.toFixed(3)})
                  </td>
                  <td>
                    {r.threePlusHits} ({r.expectedThreePlusHits.toFixed(1)})
                  </td>
                  <td>{r.scoreZTotal.toFixed(2)}</td>
                  <td>{r.shrunkReward.toFixed(4)}</td>
                  <td>{r.hedgeWeight === null ? "—" : `${(r.hedgeWeight * 100).toFixed(2)}%`}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {onlineModel && (
        <>
          <h3>Online model</h3>
          <p className="muted">
            A logistic ball model updated once per draw, using only earlier draws ({onlineModel.updates}{" "}
            updates, through draw {onlineModel.updatedThroughSequence}). Coefficients near zero mean the
            feature has carried no information so far.
          </p>
          <table>
            <thead>
              <tr>
                {onlineModel.coefficients.map((c) => (
                  <th key={c.feature}>{c.feature}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              <tr>
                {onlineModel.coefficients.map((c) => (
                  <td key={c.feature}>{c.weight.toFixed(4)}</td>
                ))}
              </tr>
            </tbody>
          </table>
        </>
      )}
    </>
  );
}
