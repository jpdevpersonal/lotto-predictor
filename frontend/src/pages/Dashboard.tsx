import { useCallback, useEffect, useState } from "react";
import { api } from "../api";
import { ProspectiveLearning } from "../components/ProspectiveLearning";
import type {
  AnalysisStatusDto,
  BacktestingDto,
  BestOfLinesDto,
  LearningDto,
  PredictionDto,
  PortfolioDto,
  PortfolioLineDto,
  DrawHistoryDto,
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

function SectionHead({
  step,
  title,
  children,
}: {
  step?: number;
  title: string;
  children: React.ReactNode;
}) {
  return (
    <div className="section-head">
      {step != null && <span className="section-step">{step}</span>}
      <div>
        <h2>{title}</h2>
        <p className="section-desc">{children}</p>
      </div>
    </div>
  );
}

function HowToRead({ children, warn = false }: { children: React.ReactNode; warn?: boolean }) {
  return (
    <div className={`how-to-read${warn ? " callout-warn" : ""}`}>{children}</div>
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
  const [draws, setDraws] = useState<DrawHistoryDto | null>(null);
  const [analysisStatus, setAnalysisStatus] = useState<AnalysisStatusDto | null>(null);
  const [recalculating, setRecalculating] = useState(false);
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
      const [status, d, p, h, f] = await Promise.all([
        api.analysisStatus(),
        api.drawHistory(),
        api.latestPrediction(),
        api.predictionHistory(),
        api.latestPortfolio(),
      ]);
      const [b, l] = status.hasAnalysis
        ? await Promise.all([api.backtesting(), api.learning()])
        : [null, null];
      setAnalysisStatus(status);
      setDraws(d);
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

  useEffect(() => {
    let active = true;
    let pending = false;
    const refreshStatus = () => {
      if (pending) return;
      pending = true;
      void api.analysisStatus().then((status) => {
        if (active) setAnalysisStatus(status);
      }).catch((e: unknown) => {
        if (active) setError(e instanceof Error ? e.message : "Failed to refresh analysis status");
      }).finally(() => { pending = false; });
    };
    window.addEventListener("focus", refreshStatus);
    const timer = window.setInterval(refreshStatus,
      recalculating || analysisStatus?.isCalculating ? 1000 : 15000);
    return () => {
      active = false;
      window.removeEventListener("focus", refreshStatus);
      window.clearInterval(timer);
    };
  }, [recalculating, analysisStatus?.isCalculating]);

  const recalculate = async (useAllHistory: boolean) => {
    setRecalculating(true);
    setAnalysisStatus((current) => current && ({
      ...current, useAllHistory, needsRecalculation: true, isCalculating: true,
      progressPercent: 0, progressStage: "Starting recalculation",
    }));
    try {
      await api.recalculate(useAllHistory);
      await load();
      setBestOf(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Recalculation failed");
      setAnalysisStatus((current) => current && ({
        ...current, needsRecalculation: true, isCalculating: false,
      }));
    } finally {
      setRecalculating(false);
    }
  };

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

  if (!draws || !analysisStatus)
    return <p className="loading-state">{error || "Loading…"}</p>;
  const latestDraw = draws.items[0];
  const latestRounds = draws.items.filter((draw) => draw.date === latestDraw?.date)
    .sort((a, b) => a.sequence - b.sequence);
  const busy = recalculating || analysisStatus.isCalculating;
  const predictionBlocked = busy || analysisStatus.needsRecalculation;

  return (
    <div>
      {error && <p className="error">{error}</p>}

      <div className="page-intro">
        <h2>{lottery.name} dashboard</h2>
        <p>
          Work top to bottom: refresh the analysis, generate your numbers, then
          scroll down to see how the model is performing against pure chance.
        </p>
      </div>

      <section aria-label="Manual analysis controls">
        <SectionHead step={1} title="Analysis engine">
          Before any numbers can be predicted, the app studies every past draw,
          tests several scoring strategies against history, and keeps the one
          that performs best. Press <strong>Recalculate</strong> after adding new
          results so predictions use the latest data.
        </SectionHead>
        <div className="actions">
          <button
            className={`recalculate ${analysisStatus.needsRecalculation || busy ? "needs-recalculation" : "up-to-date"}`}
            disabled={busy || !analysisStatus.canRecalculate}
            onClick={() => void recalculate(analysisStatus.useAllHistory)}
          >
            {busy ? "Recalculating…" : analysisStatus.needsRecalculation
              ? "Recalculate — needed" : "Recalculate — up to date"}
          </button>
          <label className="prediction-option">
            <input
              type="checkbox"
              checked={analysisStatus.useAllHistory}
              disabled={busy || !analysisStatus.canRecalculate}
              onChange={(event) => void recalculate(event.target.checked)}
            />
            Use all available history for training/backtesting
          </label>
        </div>
        {busy && (
          <div className="recalculation-progress">
            <label htmlFor="recalculation-progress">
              {analysisStatus.progressStage} — {analysisStatus.progressPercent}%
            </label>
            <progress
              id="recalculation-progress"
              max={100}
              value={analysisStatus.progressPercent}
              aria-label="Recalculation progress"
            />
          </div>
        )}
        <p className="muted" role="status">
          {!analysisStatus.canRecalculate
            ? "More historical draws are needed before analysis is available."
            : analysisStatus.needsRecalculation
              ? `Recalculation required. ${analysisStatus.drawCount} draws available; ${analysisStatus.analyzedDrawCount} in the last analysis. Previous analysis remains visible; new predictions are blocked.`
              : `Analysis is current for ${analysisStatus.analyzedDrawCount} draws.`}
          {" "}The option defaults off (up to 1,000 evaluation draws). On uses all
          history after warmup and may take considerably longer. Features always use full history.
        </p>
      </section>

      <section>
        <SectionHead step={2} title="Latest draw and your prediction">
          See the most recent official result, then generate numbers from the
          best-performing strategy. <strong>Generate Prediction</strong> gives a
          single line; <strong>Play K Lines</strong> builds several distinct
          lines that overlap as little as possible to widen coverage.
        </SectionHead>

        <div className="stat-grid">
          <div className="stat-tile">
            <div className="stat-value">{draws.total.toLocaleString()}</div>
            <div className="stat-label">Historical draws</div>
            <div className="stat-sub">
              {lottery.name} · numbers 1–{lottery.mainPoolSize}
            </div>
          </div>
          <div className="stat-tile">
            <div className="stat-value">
              <span className={`status-dot${analysisStatus.needsRecalculation ? " stale" : ""}`} />
              {analysisStatus.needsRecalculation ? "Stale" : "Current"}
            </div>
            <div className="stat-label">Analysis status</div>
            <div className="stat-sub">
              {analysisStatus.analyzedDrawCount.toLocaleString()} draws analysed
            </div>
          </div>
          {backtest && (
            <div className="stat-tile">
              <div className="stat-value">{backtest.activeStrategyName}</div>
              <div className="stat-label">Active strategy</div>
              <div className="stat-sub">Chosen by backtesting</div>
            </div>
          )}
        </div>

        <p className="label">
          Latest result
          {latestDraw && (
            <>
              {" "}
              <strong>on {latestDraw.date}</strong>
            </>
          )}
        </p>
        {latestRounds.length > 0 ? (
          <div className="latest-rounds">
            {latestRounds.map((round, index) => (
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
            match three numbers in the same six-ball draw, so portfolio odds are
            below K times single-line odds; they are counted exactly over every
            possible draw.
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
          <button onClick={generate} disabled={generating || predictionBlocked}>
            {generating ? "Generating…" : "Generate Prediction"}
          </button>
          <button
            className="secondary"
            onClick={generatePortfolio}
            disabled={portfolioLoading || predictionBlocked}
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
              <h2>Your played lines (portfolio)</h2>
              <p className="section-desc">
                A portfolio is a set of K different lines played together. This
                card shows how likely at least one line is to match 3+ numbers,
                and how each line actually scored once the draw happened.
              </p>
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
                  : ". Odds below are reproducible Monte Carlo estimates."}
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
                  (portfolio.simulation.exhaustive
                    ? ` Counted over all ${portfolio.simulation.trials.toLocaleString()} possible draws.`
                    : ` Monte Carlo check: ${formatPct(portfolio.simulation.probability)} (95% CI ${formatPct(portfolio.simulation.ciLow)}–${formatPct(portfolio.simulation.ciHigh)}, ${portfolio.simulation.trials.toLocaleString()} draws).`)}
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
            <button onClick={loadBestOf} disabled={bestLoading || predictionBlocked}>
              {bestLoading ? "Computing…" : "Show Consensus"}
            </button>
          </div>

          <HowToRead>
            <strong>How to read this:</strong> the table below is a planning aid
            — it shows the fewest lines you would need to even have a chance of
            reaching a target probability. <strong>Show Consensus</strong> counts
            which numbers recur most across your lines.
          </HowToRead>
          <div className="table-wrap">
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
          </div>
          <p className="muted">
            These minimum counts do not guarantee the target chance. Use the
            portfolio chance reported above for the lines actually played.
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
          <SectionHead step={3} title="Why these numbers? (prediction explanation)">
            Every number in the pool gets a score from the active strategy. The
            highest-scoring numbers became your prediction. This table shows
            the raw evidence behind each score.
          </SectionHead>
          <p className="muted">
            Model {prediction.modelVersion}; scores computed from the{" "}
            {prediction.cutoffSequence} draws available at prediction time.
          </p>
          <div className="table-wrap">
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
          </div>
          <HowToRead>
            <strong>Columns:</strong> <em>Overall frequency</em> — how often the
            number has been drawn ever. <em>Recent frequency</em> — appearances in
            the last 10/25/50/100 draws. <em>Draws since seen</em> — how long it
            has been absent. <em>Average gap</em> — typical wait between
            appearances. <em>Model score</em> — the combined result.
          </HowToRead>
        </section>
      )}

      {backtest && (
        <section>
          <SectionHead step={4} title="Backtesting: does it beat chance?">
            We replay history: for each past draw, every strategy predicts using
            only the data that existed before that draw, then we count matches.
            A strategy only matters if it clearly beats the random baselines at
            the bottom of the table.
          </SectionHead>
          <p className="muted">
            Results cover the last {backtest.evaluatedDraws} chronological holdout
            draws. Genetic training and strategy selection used earlier data;
            each holdout prediction used only data before its target. Active
            strategy: <strong>{backtest.activeStrategyName}</strong>.
          </p>
          <div className="table-wrap">
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
          </div>
          <HowToRead>
            <strong>Reading the table:</strong> <em>Avg matches</em> is how many
            numbers a strategy gets right per draw. <em>0/1/2/3+ match</em> show
            how often each outcome happened. <em>3+ rate</em> is the share of
            draws with at least three correct; hover for its 95% confidence
            range. Green rows are the best strategy.
          </HowToRead>
          <p className="verdict">{backtest.verdict}</p>
        </section>
      )}

      {learning && (
        <section>
          <SectionHead step={5} title="Learning: how the model improves">
            The app keeps evolving new strategies and tests them only on draws
            that happen <em>after</em> they were created, so results can't be
            flattered by hindsight. It also checks whether the draw itself shows
            any measurable bias.
          </SectionHead>
          <p className="muted">
            Generation <strong>{learning.generation}</strong> across{" "}
            <strong>{learning.analyzedDrawCount} draws</strong>
            {learning.refreshedUtc
              ? `, refreshed ${learning.refreshedUtc.replace("T", " ").slice(0, 16)} UTC`
              : ""}
            . Each analysis breeds a new generation from training-prefix winners and
            validates it on a chronological holdout. The best fresh candidates then
            join a live challenger population. From then on they are judged only on
            real draws that happen after they were frozen, and the weakest are
            retired. Active strategy:{" "}
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

          {learning.prospective && (
            <ProspectiveLearning prospective={learning.prospective} />
          )}

          {learning.hedgeWeights.length > 0 && (
            <>
              <h3>Hedge ensemble weights</h3>
              <p className="muted">
                Persistent across draws. After each real result, every live strategy
                gains or loses weight exponentially in its reward (how highly it ranked
                the drawn balls, plus a three-plus bonus). A small share is mixed back
                toward uniform, so a strategy can recover.
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
            <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Learned strategy</th>
                  <th>Status</th>
                  <th>Generation</th>
                  <th>Live after draw</th>
                  <th>Selection Avg Matches</th>
                  <th>Selection Recent Avg</th>
                  <th>Discovered (UTC)</th>
                </tr>
              </thead>
              <tbody>
                {learning.learnedStrategies.map((s) => (
                  <tr key={s.name}>
                    <td title={s.weights}>{s.name}</td>
                    <td>{s.status}</td>
                    <td>{s.generation}</td>
                    <td>{s.bornAfterSequence || "—"}</td>
                    <td>{s.avgMatches.toFixed(4)}</td>
                    <td>{s.recencyWeightedAvg.toFixed(4)}</td>
                    <td>{s.createdUtc.replace("T", " ").slice(0, 16)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            </div>
          ) : (
            <p className="muted">
              No challengers yet. Recalculate to admit the first generation.
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
          <SectionHead step={6} title="Prediction history">
            A permanent record of every prediction you have generated and how it
            scored once the real draw arrived. “Pending” means the draw has not
            happened (or been entered) yet.
          </SectionHead>
          <div className="table-wrap">
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
          </div>
        </section>
      )}
    </div>
  );
}
