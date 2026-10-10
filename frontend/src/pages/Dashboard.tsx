import { useCallback, useEffect, useState } from "react";
import { api } from "../api";
import type {
  BacktestingDto,
  BestOfLinesDto,
  LearningDto,
  PredictionDto,
  PortfolioDto,
  PortfolioLineDto,
  StatisticsDto,
  LotteryProfile,
} from "../types";

function Balls({
  numbers,
  matched,
  stars = false,
}: {
  numbers: number[];
  matched?: number[];
  stars?: boolean;
}) {
  return (
    <div className="balls">
      {numbers.map((n) => (
        <span
          key={n}
          className={`ball${stars ? " star" : ""}${matched?.includes(n) ? " matched" : ""}`}
        >
          {n}
        </span>
      ))}
    </div>
  );
}

function formatPct(value: number) {
  return `${(value * 100).toFixed(value < 0.001 ? 4 : 2)}%`;
}

function oneIn(probability: number) {
  return `1 in ${Math.round(1 / probability).toLocaleString()}`;
}

/** Necessary minimum from the union bound, not an achievable-odds guarantee. */
function linesForTarget(target: number, singleLine: number, rounds: number) {
  return Math.ceil((1 - Math.pow(1 - target, 1 / rounds)) / singleLine - 1e-9);
}

/** Numbers of a line that appeared in any evaluated round. */
function matchedNumbers(line: PortfolioLineDto) {
  const actual = new Set(
    line.prediction?.evaluations.flatMap((e) => e.actualNumbers) ?? [],
  );
  return line.numbers.filter((n) => actual.has(n));
}

export default function Dashboard({
  lottery,
  onAddResult,
}: {
  lottery: LotteryProfile;
  onAddResult: () => void;
}) {
  const [stats, setStats] = useState<StatisticsDto | null>(null);
  const [backtest, setBacktest] = useState<BacktestingDto | null>(null);
  const [prediction, setPrediction] = useState<PredictionDto | null>(null);
  const [history, setHistory] = useState<PredictionDto[]>([]);
  const [learning, setLearning] = useState<LearningDto | null>(null);
  const [error, setError] = useState("");
  const [generating, setGenerating] = useState(false);
  const [portfolio, setPortfolio] = useState<PortfolioDto | null>(null);
  const [portfolioLoading, setPortfolioLoading] = useState(false);
  const [bestOf, setBestOf] = useState<BestOfLinesDto | null>(null);
  const [bestLoading, setBestLoading] = useState(false);
  const [excludeLastDrawNumbers, setExcludeLastDrawNumbers] = useState(false);
  const [portfolioSize, setPortfolioSize] = useState(3);

  const load = useCallback(async () => {
    try {
      const [s, b, p, h, l, f] = await Promise.all([
        api.statistics(),
        api.backtesting(),
        api.latestPrediction(),
        api.predictionHistory(),
        api.learning(),
        api.latestPortfolio(),
      ]);
      setStats(s);
      setBacktest(b);
      setPrediction(p);
      setHistory(h ?? []);
      setLearning(l);
      setPortfolio(f);
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
      setPrediction(await api.generatePrediction(excludeLastDrawNumbers));
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

  const generatePortfolio = async () => {
    setPortfolioLoading(true);
    setBestOf(null);
    try {
      setPortfolio(
        await api.generatePortfolio(portfolioSize, excludeLastDrawNumbers),
      );
      setHistory(await api.predictionHistory());
      setError("");
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to generate portfolio");
    } finally {
      setPortfolioLoading(false);
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
                {lottery.name} · numbers 1–{stats.poolSize}
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
                {round.luckyStars.length > 0 && (
                  <Balls numbers={round.luckyStars} stars />
                )}
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
          <div className="prediction-result">
            <Balls numbers={prediction.numbers} />
            {prediction.luckyStars.length > 0 && (
              <Balls numbers={prediction.luckyStars} stars />
            )}
          </div>
        ) : (
          <p className="muted">none yet</p>
        )}
        {backtest && (
          <p className="muted">
            Any single line has a {oneIn(backtest.randomThreePlusProbability)}{" "}
            chance of 3+ matches per round and averages{" "}
            {backtest.randomExpectedMatches.toFixed(2)} matches; no choice of
            numbers changes that in a fair independent draw. More distinct lines
            can improve coverage, at greater cost. Even disjoint lines can both
            match three numbers in the same six-ball draw; portfolio odds are
            generally estimates, not K times single-line odds.
          </p>
        )}

        <div className="actions">
          <label className="prediction-option">
            <input
              type="checkbox"
              checked={excludeLastDrawNumbers}
              onChange={(event) =>
                setExcludeLastDrawNumbers(event.target.checked)
              }
            />
            Exclude last draw numbers from this prediction
          </label>
          <label className="prediction-option portfolio-size">
            Lines K
            <input
              type="number"
              min={1}
              max={200}
              value={portfolioSize}
              onChange={(event) =>
                setPortfolioSize(
                  Math.max(1, Math.min(200, Number(event.target.value) || 1)),
                )
              }
            />
          </label>
          <button onClick={generate} disabled={generating}>
            {generating ? "Generating…" : "Generate Prediction"}
          </button>
          <button
            className="secondary"
            onClick={generatePortfolio}
            disabled={portfolioLoading}
          >
            {portfolioLoading
              ? "Generating…"
              : `Play ${portfolioSize} Line${portfolioSize === 1 ? "" : "s"}`}
          </button>
          <button className="secondary" onClick={onAddResult}>
            Add New Result
          </button>
        </div>
      </section>

      {portfolio && (
        <section>
          <div className="section-heading">
            <div>
              <h2>Played portfolio</h2>
              <p className="muted">
                <strong>K={portfolio.lineCount}</strong> lines from strategy{" "}
                <strong>{portfolio.strategyName}</strong> after draw #
                {portfolio.cutoffDrawNumber}, saved{" "}
                {portfolio.createdUtc.replace("T", " ").slice(0, 16)} UTC and
                scored against every round of the next draw. Lines pairwise
                share at most {portfolio.maxPairwiseOverlap} ball
                {portfolio.maxPairwiseOverlap === 1 ? "" : "s"}
                {portfolio.probabilityIsExact
                  ? ". Odds below are exact for this portfolio."
                  : ". Odds below are reproducible Monte Carlo estimates, not guaranteed or globally optimal."}
              </p>
              <p className="verdict compact">
                Chance of at least one line matching 3+ main numbers:{" "}
                <strong>
                  {formatPct(portfolio.anyRoundThreePlusProbability)}
                </strong>{" "}
                ({oneIn(portfolio.anyRoundThreePlusProbability)}) across{" "}
                {portfolio.roundCount} round
                {portfolio.roundCount === 1 ? "" : "s"};{" "}
                {formatPct(portfolio.portfolioThreePlusProbability)} per round.
                Single line:{" "}
                {formatPct(portfolio.singleLineThreePlusProbability)} per round (
                {oneIn(portfolio.singleLineThreePlusProbability)}).
                {portfolio.simulation &&
                  ` Monte Carlo check: ${formatPct(portfolio.simulation.probability)} (95% CI ${formatPct(portfolio.simulation.ciLow)}–${formatPct(portfolio.simulation.ciHigh)}, ${portfolio.simulation.trials.toLocaleString()} draws).`}
                {portfolio.simulation?.randomDistinctProbability != null &&
                  ` ${portfolio.lineCount} random distinct lines: ${formatPct(portfolio.simulation.randomDistinctProbability)}.`}
              </p>
              {portfolio.bestMatches != null && (
                <p
                  className={`verdict compact${portfolio.bestMatches >= 3 ? " hit" : ""}`}
                >
                  Result: best line #{portfolio.bestMatchesRank} matched{" "}
                  {portfolio.bestMatches} in round {portfolio.bestMatchesRound}
                  {portfolio.bestMatches >= 3
                    ? " — 3+ hit."
                    : " — no 3+ hit this draw."}
                </p>
              )}
            </div>
            <button onClick={loadBestOf} disabled={bestLoading}>
              {bestLoading ? "Computing…" : "Show Consensus"}
            </button>
          </div>

          <table className="compact">
            <thead>
              <tr>
                <th>
                  Target chance of a 3+ match on one draw night (
                  {portfolio.roundCount} round
                  {portfolio.roundCount === 1 ? "" : "s"})
                </th>
                <th>Minimum possible lines (bound only)</th>
              </tr>
            </thead>
            <tbody>
              {[0.01, 0.05, 0.1, 0.25, 0.5, 0.9].map((target) => (
                <tr key={target}>
                  <td>{Math.round(target * 100)}%</td>
                  <td>
                    {linesForTarget(
                      target,
                      portfolio.singleLineThreePlusProbability,
                      portfolio.roundCount,
                    ).toLocaleString()}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="muted">
            These minimum counts do not guarantee the target chance. Use the
            reported portfolio estimate and its confidence interval.
          </p>

          {bestOf && (
            <div className="best-of-card">
              <p className="label">
                Best-of-50 consensus{" "}
                <strong>
                  — the {lottery.mainNumberCount} numbers appearing most across
                  all {portfolio.lineCount} lines
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
              {bestOf.luckyStars.length > 0 && (
                <div className="balls">
                  {bestOf.luckyStars.map((number, index) => (
                    <span key={number} className="ball star consensus">
                      {number}
                      <small>{bestOf.luckyStarFrequencies[index]}×</small>
                    </span>
                  ))}
                </div>
              )}
              <p className="muted">
                Frequency shows how many of the {bestOf.linesConsidered} lines
                include each number. It is descriptive only, not higher
                confidence.
              </p>
            </div>
          )}

          <div className="lines-grid">
            {portfolio.lines.map((line) => (
              <div className="line-row" key={line.rank}>
                <span className="line-rank">#{line.rank}</span>
                <Balls numbers={line.numbers} matched={matchedNumbers(line)} />
                {line.luckyStars.length > 0 && (
                  <Balls numbers={line.luckyStars} stars />
                )}
                {line.prediction?.evaluations.map((evaluation) => (
                  <span
                    key={evaluation.evaluatedDrawId}
                    className={`line-score${evaluation.matches >= 3 ? " hit" : ""}`}
                    title={`Round ${evaluation.round}: ${evaluation.actualNumbers.join(" ")}`}
                  >
                    R{evaluation.round}: {evaluation.matches}
                  </span>
                ))}
                {line.score != null && (
                  <span
                    className="line-score"
                    title="Model line score (tie-break only; cannot change the odds)"
                  >
                    {line.score.toFixed(3)}
                  </span>
                )}
                <span
                  className="line-score"
                  title="Most balls shared with any earlier line"
                >
                  ∩{line.maxOverlapWithEarlier}
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
            Results cover the last {backtest.evaluatedDraws} chronological holdout
            draws. Genetic training and strategy selection used earlier data;
            each holdout prediction used only data before its target. Active
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
                <th>3+ hits</th>
                <th>3+ rate</th>
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
                  <td>{s.threePlusHits}</td>
                  <td
                    title={`95% CI ${formatPct(s.threePlusCiLow)}-${formatPct(s.threePlusCiHigh)}`}
                  >
                    {formatPct(s.threePlusRate)}
                  </td>
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
                <td>{backtest.randomExpectedThreePlusHits.toFixed(3)} exp.</td>
                <td>{formatPct(backtest.randomThreePlusProbability)}</td>
              </tr>
              <tr className="baseline">
                <td>random baseline (theoretical)</td>
                <td>{backtest.randomExpectedMatches.toFixed(4)}</td>
                <td colSpan={7} className="muted">
                  exact single-line random 3+ probability:{" "}
                  {formatPct(backtest.randomThreePlusProbability)}; expected 3+
                  hits: {backtest.randomExpectedThreePlusHits.toFixed(4)}
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
            Generation <strong>{learning.generation}</strong> across{" "}
            <strong>{learning.analyzedDrawCount} draws</strong>
            {learning.refreshedUtc
              ? `, refreshed ${learning.refreshedUtc.replace("T", " ").slice(0, 16)} UTC`
              : ""}
            : each analysis replays training from clean built-in seeds using
            only pre-holdout draws. One generation of mutations, crossovers and
            random candidates competes on three-plus hits, with average matches
            as a tie-breaker. Saved winners are for reporting, not reused as
            training seeds. An online hedge ensemble also competes. Active strategy:{" "}
            <strong>{learning.activeStrategyName}</strong>
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
                achieved more three-plus hits get exponentially more say in the blended
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
                  <th>Selection Avg Matches</th>
                  <th>Selection Recent Avg</th>
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
              No genetic candidate currently beats the built-in ones on the
              selection data.
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
                  <td>
                    {p.id}
                    {p.portfolioRank != null && (
                      <span className="badge">line {p.portfolioRank}</span>
                    )}
                  </td>
                  <td>{p.createdUtc.replace("T", " ").slice(0, 16)}</td>
                  <td>{p.numbers.join(" ")}</td>
                  <td>#{p.cutoffDrawNumber}</td>
                  <td>{p.modelVersion}</td>
                  <td>
                    {p.evaluations.length > 0
                      ? p.evaluations.map((evaluation) => (
                          <div key={evaluation.evaluatedDrawId}>
                            Round {evaluation.round}:{" "}
                            {evaluation.actualNumbers.join(" ")}
                            {evaluation.bonus != null
                              ? ` + bonus ${evaluation.bonus}`
                              : ""}
                            {evaluation.actualLuckyStars.length > 0
                              ? ` + ${lottery.bonusLabel.toLowerCase()}s ${evaluation.actualLuckyStars.join(" ")}`
                              : ""}
                          </div>
                        ))
                      : p.actualNumbers
                        ? p.actualNumbers.join(" ")
                        : "—"}
                  </td>
                  <td>
                    {p.evaluations.length > 0
                      ? p.evaluations.map((evaluation) => (
                          <div key={evaluation.evaluatedDrawId}>
                            Round {evaluation.round}: {evaluation.matches} main
                            {evaluation.bonusMatches
                              ? ` + ${evaluation.bonusMatches} bonus`
                              : ""}
                            {evaluation.luckyStarMatches != null
                              ? ` + ${evaluation.luckyStarMatches} ${lottery.bonusLabel.toLowerCase()}s`
                              : ""}
                          </div>
                        ))
                      : (p.matches ?? "pending")}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}
    </div>
  );
}
