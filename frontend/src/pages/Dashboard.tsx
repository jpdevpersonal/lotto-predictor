import { useCallback, useEffect, useState } from "react";
import { api } from "../api";
import type {
  BacktestingDto,
  BestOfLinesDto,
  LearningDto,
  PredictionDto,
  PredictionLinesDto,
  StatisticsDto,
} from "../types";

function Balls({
  numbers,
  matched,
}: {
  numbers: number[];
  matched?: number[];
}) {
  return (
    <div className="balls">
      {numbers.map((n) => (
        <span
          key={n}
          className={`ball${matched?.includes(n) ? " matched" : ""}`}
        >
          {n}
        </span>
      ))}
    </div>
  );
}

export default function Dashboard({
  onAddResult,
}: {
  onAddResult: () => void;
}) {
  const [stats, setStats] = useState<StatisticsDto | null>(null);
  const [backtest, setBacktest] = useState<BacktestingDto | null>(null);
  const [prediction, setPrediction] = useState<PredictionDto | null>(null);
  const [history, setHistory] = useState<PredictionDto[]>([]);
  const [learning, setLearning] = useState<LearningDto | null>(null);
  const [error, setError] = useState("");
  const [generating, setGenerating] = useState(false);
  const [lines, setLines] = useState<PredictionLinesDto | null>(null);
  const [linesLoading, setLinesLoading] = useState(false);
  const [bestOf, setBestOf] = useState<BestOfLinesDto | null>(null);
  const [bestLoading, setBestLoading] = useState(false);

  const load = useCallback(async () => {
    try {
      const [s, b, p, h, l] = await Promise.all([
        api.statistics(),
        api.backtesting(),
        api.latestPrediction(),
        api.predictionHistory(),
        api.learning(),
      ]);
      setStats(s);
      setBacktest(b);
      setPrediction(p);
      setHistory(h ?? []);
      setLearning(l);
      setError("");
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to load data");
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const generate = async () => {
    setGenerating(true);
    try {
      setPrediction(await api.generatePrediction());
      setHistory(await api.predictionHistory());
      setError("");
    } catch (e) {
      setError(
        e instanceof Error ? e.message : "Failed to generate prediction",
      );
    } finally {
      setGenerating(false);
    }
  };

  const loadLines = async () => {
    setLinesLoading(true);
    setBestOf(null);
    try {
      setLines(await api.predictionLines(50));
      setError("");
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to generate lines");
    } finally {
      setLinesLoading(false);
    }
  };

  const loadBestOf = async () => {
    setBestLoading(true);
    try {
      setBestOf(await api.bestOfLines(50));
      setError("");
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to compute best-of-50");
    } finally {
      setBestLoading(false);
    }
  };

  if (!stats) return <p className="loading-state">{error || "Loading…"}</p>;

  return (
    <div>
      {error && <p className="error">{error}</p>}

      <section>
        <div className="hero">
          <div className="hero-stats">
            <div className="stat">
              <div className="stat-value">{stats.drawCount}</div>
              <div className="stat-label">Historical draws</div>
              <div className="stat-sub">
                numbers 1–{stats.poolSize}
                {stats.poolChangeDrawNumber != null &&
                  `, enlarged from 1–49 at draw ${stats.poolChangeDrawNumber}`}
              </div>
            </div>
          </div>
        </div>

        <p className="label">
          Latest result
          {stats.latestDraw && (
            <>
              {" "}
              <strong>on {stats.latestDraw.date}</strong>
            </>
          )}
        </p>
        {stats.latestRounds.length > 0 ? (
          <div className="latest-rounds">
            {stats.latestRounds.map((round, index) => (
              <div className="latest-round" key={round.id}>
                <span className="round-label">Round {index + 1}</span>
                <Balls numbers={round.numbers} />
              </div>
            ))}
          </div>
        ) : (
          <p className="muted">none</p>
        )}

        <p className="label">
          Current prediction
          {prediction && (
            <>
              {" "}
              <strong>
                {prediction.strategyName} · after draw #
                {prediction.cutoffDrawNumber}
              </strong>
            </>
          )}
        </p>
        {prediction ? (
          <Balls numbers={prediction.numbers} />
        ) : (
          <p className="muted">none yet</p>
        )}

        <div className="actions">
          <button onClick={generate} disabled={generating}>
            {generating ? "Generating…" : "Generate Prediction"}
          </button>
          <button
            className="secondary"
            onClick={loadLines}
            disabled={linesLoading}
          >
            {linesLoading ? "Generating…" : "Generate 50 Lines"}
          </button>
          <button className="secondary" onClick={onAddResult}>
            Add New Result
          </button>
        </div>
      </section>

      {lines && (
        <section>
          <div className="section-heading">
            <div>
              <h2>Top 50 lines</h2>
              <p className="muted">
                The 50 best-scoring lines from strategy{" "}
                <strong>{lines.strategyName}</strong> after draw #
                {lines.cutoffDrawNumber}. Playing many lines is the only
                reliable way to raise the chance of a 3+ match (roughly 60%
                across 50 lines vs ~2% for one). Shown on screen only — not
                saved.
              </p>
            </div>
            <button onClick={loadBestOf} disabled={bestLoading}>
              {bestLoading ? "Computing…" : "Compute Best-of-50"}
            </button>
          </div>

          {bestOf && (
            <div className="best-of-card">
              <p className="label">
                Best-of-50 consensus{" "}
                <strong>
                  — the six numbers appearing most across all 50 lines
                </strong>
              </p>
              <div className="balls">
                {bestOf.numbers.map((n, i) => (
                  <span key={n} className="ball consensus">
                    {n}
                    <small>{bestOf.frequencies[i]}×</small>
                  </span>
                ))}
              </div>
              <p className="muted">
                Frequency shows how many of the {bestOf.linesConsidered} lines
                include each number. On-screen only — not stored or evaluated.
              </p>
            </div>
          )}

          <div className="lines-grid">
            {lines.lines.map((line) => (
              <div className="line-row" key={line.rank}>
                <span className="line-rank">#{line.rank}</span>
                <Balls numbers={line.numbers} />
                <span className="line-score" title="Model line score">
                  {line.score.toFixed(3)}
                </span>
              </div>
            ))}
          </div>
        </section>
      )}

      {prediction?.explanation && (
        <section>
          <h2>Prediction explanation</h2>
          <p className="muted">
            Model {prediction.modelVersion}; scores computed from the{" "}
            {prediction.cutoffSequence} draws available at prediction time.
          </p>
          <table>
            <thead>
              <tr>
                <th>Number</th>
                <th>Overall Frequency</th>
                <th>Recent Frequency (10/25/50/100)</th>
                <th>Draws Since Seen</th>
                <th>Average Gap</th>
                <th>Model Score</th>
              </tr>
            </thead>
            <tbody>
              {prediction.explanation.map((e) => (
                <tr key={e.number}>
                  <td>
                    <strong>{e.number}</strong>
                  </td>
                  <td>
                    {e.overallFrequency} ({(e.overallRate * 100).toFixed(2)}%)
                  </td>
                  <td>
                    {e.count10} / {e.count25} / {e.count50} / {e.count100}
                  </td>
                  <td>{e.drawsSinceSeen}</td>
                  <td>{e.averageGap}</td>
                  <td>{e.modelScore.toFixed(4)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}

      {backtest && (
        <section>
          <h2>Backtesting results</h2>
          <p className="muted">
            Walk-forward over the last {backtest.evaluatedDraws} draws: each
            historical prediction used only draws before its target. Active
            strategy: <strong>{backtest.activeStrategyName}</strong>.
          </p>
          <table>
            <thead>
              <tr>
                <th>Strategy</th>
                <th>Avg Matches</th>
                <th>Recent Avg</th>
                <th>0 match</th>
                <th>1 match</th>
                <th>2 match</th>
                <th>3+ match</th>
              </tr>
            </thead>
            <tbody>
              {backtest.strategies.map((s) => (
                <tr key={s.name} className={s.isBest ? "best" : ""}>
                  <td title={s.weights}>
                    {s.name}
                    {s.isBest && <span className="badge">Best</span>}
                    {s.isLearned && <span className="badge">Learned</span>}
                  </td>
                  <td>{s.avgMatches.toFixed(4)}</td>
                  <td>{s.recencyWeightedAvg.toFixed(4)}</td>
                  <td>{s.pct0}%</td>
                  <td>{s.pct1}%</td>
                  <td>{s.pct2}%</td>
                  <td>{s.pct3Plus}%</td>
                </tr>
              ))}
              <tr className="baseline">
                <td>random baseline (simulated)</td>
                <td>{backtest.randomSimulatedAvgMatches.toFixed(4)}</td>
                <td>—</td>
                <td>{backtest.randomPct0}%</td>
                <td>{backtest.randomPct1}%</td>
                <td>{backtest.randomPct2}%</td>
                <td>{backtest.randomPct3Plus}%</td>
              </tr>
              <tr className="baseline">
                <td>random baseline (theoretical)</td>
                <td>{backtest.randomExpectedMatches.toFixed(4)}</td>
                <td colSpan={5} className="muted">
                  expected matches for uniform random picks
                </td>
              </tr>
            </tbody>
          </table>
          <p className="verdict">{backtest.verdict}</p>
        </section>
      )}

      {learning && (
        <section>
          <h2>Learning</h2>
          <p className="muted">
            Generation <strong>{learning.generation}</strong>: each new draw
            triggers one genetic-optimizer generation — elite weight sets are
            mutated, crossed over, and challenged by random immigrants, all
            judged by the same walk-forward backtest. An online hedge ensemble
            (multiplicative weights) blends every strategy and competes too.
            Active strategy: <strong>{learning.activeStrategyName}</strong>
            {learning.activeIsLearned && (
              <span className="badge">Learned</span>
            )}{" "}
            <span className="muted">({learning.activeWeights})</span>
          </p>

          <p className="verdict">
            Bias check (χ² uniformity test over{" "}
            {learning.uniformity.windowDraws} current-era draws, χ²=
            {learning.uniformity.chiSquare.toFixed(1)}, df=
            {learning.uniformity.degreesOfFreedom}):{" "}
            {learning.uniformity.assessment}
          </p>

          {learning.hedgeWeights.length > 0 && (
            <>
              <h3>Hedge ensemble weights</h3>
              <p className="muted">
                Learned online during the walk-forward run — strategies that
                matched more get exponentially more say in the blended
                prediction.
              </p>
              <table>
                <thead>
                  <tr>
                    <th>Strategy</th>
                    <th>Weight</th>
                  </tr>
                </thead>
                <tbody>
                  {learning.hedgeWeights.slice(0, 8).map((w) => (
                    <tr key={w.strategyName}>
                      <td>{w.strategyName}</td>
                      <td>{(w.weight * 100).toFixed(2)}%</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </>
          )}

          {learning.learnedStrategies.length > 0 ? (
            <table>
              <thead>
                <tr>
                  <th>Learned strategy</th>
                  <th>Generation</th>
                  <th>Avg Matches</th>
                  <th>Recent Avg</th>
                  <th>Discovered (UTC)</th>
                </tr>
              </thead>
              <tbody>
                {learning.learnedStrategies.map((s) => (
                  <tr key={s.name}>
                    <td title={s.weights}>{s.name}</td>
                    <td>{s.generation}</td>
                    <td>{s.avgMatches.toFixed(4)}</td>
                    <td>{s.recencyWeightedAvg.toFixed(4)}</td>
                    <td>{s.createdUtc.replace("T", " ").slice(0, 16)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : (
            <p className="muted">
              No learned strategy currently beats the hand-written ones — the
              expected state for a fair lottery.
            </p>
          )}

          {learning.history.length > 0 && (
            <>
              <h3>Active strategy performance over time</h3>
              <table>
                <thead>
                  <tr>
                    <th>Dataset size</th>
                    <th>Active strategy</th>
                    <th>Avg Matches</th>
                    <th>Recent Avg</th>
                    <th>Random expected</th>
                    <th>Edge</th>
                  </tr>
                </thead>
                <tbody>
                  {learning.history
                    .filter((h) => h.wasActive)
                    .slice(0, 25)
                    .map((h) => (
                      <tr key={h.drawCount}>
                        <td>{h.drawCount} draws</td>
                        <td>{h.strategyName}</td>
                        <td>{h.avgMatches.toFixed(4)}</td>
                        <td>{h.recencyWeightedAvg.toFixed(4)}</td>
                        <td>{h.randomExpected.toFixed(4)}</td>
                        <td>{(h.avgMatches - h.randomExpected).toFixed(4)}</td>
                      </tr>
                    ))}
                </tbody>
              </table>
            </>
          )}
        </section>
      )}

      {history.length > 0 && (
        <section>
          <h2>Prediction history</h2>
          <table>
            <thead>
              <tr>
                <th>ID</th>
                <th>Created (UTC)</th>
                <th>Predicted</th>
                <th>After draw</th>
                <th>Model</th>
                <th>Actual</th>
                <th>Matches</th>
              </tr>
            </thead>
            <tbody>
              {history.map((p) => (
                <tr key={p.id}>
                  <td>{p.id}</td>
                  <td>{p.createdUtc.replace("T", " ").slice(0, 16)}</td>
                  <td>{p.numbers.join(" ")}</td>
                  <td>#{p.cutoffDrawNumber}</td>
                  <td>{p.modelVersion}</td>
                  <td>{p.actualNumbers ? p.actualNumbers.join(" ") : "—"}</td>
                  <td>{p.matches ?? "pending"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}
    </div>
  );
}
