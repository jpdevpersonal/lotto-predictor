# Lotto Predictor

A local web application for UK National Lottery and EuroMillions draw histories. It computes statistical
features, runs walk-forward backtesting over hand-written and machine-learned scoring strategies,
and generates a ranked prediction targeting **at least three main-number matches on one line**.
Each analysis replays training from clean seeds and evaluates a later chronological holdout.
Analysis is manual for UK Lotto, EuroMillions and Set For Life: use **Recalculate** on the dashboard.
This objective gives more feedback than four-plus, but does not create predictive information:
fair independent draws give every single line the same odds.

## Run it

Both services:

```bash
./run.sh                              # UI: http://localhost:5173
```

The launcher creates a per-run local mutation key automatically and passes it to both services. If
you start the API and frontend in separate terminals, configure the matching keys manually as below.

Backend (imports both histories on first run and creates separate SQLite databases):

```bash
cd backend/LottoPredictor.Api
dotnet run --launch-profile http     # http://localhost:5080
```

Frontend:

```bash
cd frontend
npm install
npm run dev                          # http://localhost:5173
```

Mutating API calls are protected by a shared local key. Set the backend key with configuration
(`MutationApiKey`, `MutationApiKey=...`, or `--MutationApiKey=...`) and set the matching frontend
value in `frontend/.env.local` as `VITE_MUTATION_API_KEY=...`. Do not store a real key in git;
use [frontend/.env.example](frontend/.env.example) as the template.

Tests:

```bash
cd backend
dotnet test
```

## What was found in numbers.csv

- 3,226 data rows covering draw numbers 1–3202 (Nov 1994 – Aug 2026). Headers and values carry
  padding spaces; the importer trims everything.
- **Duplicate draw numbers**: draws 3179–3202 each appear **twice** with different number sets,
  machines ("Lotto 4/5/6") and ball sets. These are two genuinely independent draw events held
  under one draw number, not data errors. **Both rows are kept** as separate draw events — each is
  a real sample from the same ball pool, so discarding either would throw away information.
  Chronological order within a shared draw number preserves the file's own ordering.
- **Ball pool change**: draws 1–2065 use balls 1–49; from draw 2066 (Oct 2015) the pool is 1–59.
  The analysis is era-aware: numbers 50–59 are only counted as "eligible" from draw 2066 onward,
  otherwise their frequencies would be systematically understated. New results are validated
  against the current pool (1–59).
- `BN` is a bonus ball and is excluded from the N1–N6 prediction dataset as specified.

## EuroMillions

- The supplied history contains 1,977 draws from 13 February 2004 through 1 September 2026.
- EuroMillions is stored separately in `backend/LottoPredictor.Api/euromillions.db`.
- The engine analyzes and predicts five main numbers from 1–50 and two Lucky Stars from 1–12 as
  separate number pools. EuroMillions has one round per draw.
- Use the **Lottery** selector in the app header to switch games. API clients can select the same
  database by sending `X-Lottery: euromillions`; omitting the header selects UK Lotto.
- Override the import file with `--EuroMillionsCsvImportPath=/path/to/euromillions.csv`.

## How prediction works

### Manual recalculation

- Adding or correcting results saves and evaluates them immediately, but does not rerun analysis.
  The last analysis stays visible. The **Recalculate** button is **red** when analysis is missing
  or stale and **green** when current. Text labels convey the status without relying on colour.
  New predictions, portfolios and consensus lines are blocked until recalculation succeeds.
- **Use all available history for training/backtesting** defaults off independently per lottery.
  Off retains the usual 1,000-draw evaluation window; on evaluates all available history after
  the 150-draw warmup. Changing the checkbox immediately requests recalculation.
  Feature calculations already use full history and are unchanged. Full-history backtests can
  take considerably longer. Hit tests account for different random probabilities across pool eras.
- Status is refreshed on entering the dashboard, window focus, and every 15 seconds without
  recalculating. While calculating, status polls every second and a progress bar reports
  training/validation draw completion and the current stage. Percentages allocate 5% to
  preparation, 25% to training, 65% to validation and 5% to saving; they are not time estimates.
  Status is refreshed without
  recalculating. Results added during a calculation leave its result marked stale.
- Analysis snapshots and options are held in memory. Restarting the API resets the option to off
  and requires manual recalculation; stored draw/prediction histories remain intact. No startup
  backtest runs automatically.

API: `GET /api/analysis/status` reads the selected lottery's status;
`POST /api/analysis/recalculate` accepts `{"useAllHistory": false}` (or `true`) and requires the
usual mutation key. All games use `X-Lottery`. Missing/stale analysis blocks generation with
HTTP 409 Problem Details. See [Analysis.http](backend/LottoPredictor.Api/Analysis.http).

1. `FeatureCalculator` computes per-number features (era-standardised frequency rate, counts over
   the last 10/25/50/100 draws, multi-horizon EWMA rate, draws since last seen, era-scaled average
   gap, gap ratio, renewal gap-hazard residual, recent-vs-long-term ratio, positional counts,
   active `Machine` / `BallSet` physical equipment affinity, and a **Bayesian bias z-score** of
   observed appearances vs a fair-machine expectation) plus pair/triple co-occurrence counts,
   combination-level distributions (sum, range, odd/even, consecutive numbers, low/high split) and
   a **chi-square uniformity test** over the current constant-pool era — always from a strict
   prefix of draws.
2. `PredictionEngine` orthogonalises and z-scores the features and combines them with a strategy's
   weights (long-term, recent, gap, momentum, bias, bonus/secondary), then searches combinations of
   the top 18 candidates followed by 1-opt full-pool ball swap refinement, adding a pair-synergy
   bonus and a soft typicality penalty (extreme sum / odd-even balance / range / clustering are
   penalised, never excluded).
3. `Backtester` runs walk-forward validation: for each of the last 1,000 draws (or available history
   after 150 warmup draws) it predicts using
   only earlier draws and records matches, for every candidate strategy, for an **online hedge
   ensemble** (including walk-forward `online-logit`), and for a seeded random baseline. Selection
   ranks three-plus hit rate first, then average matches, recency-weighted matches, and dense
   per-step `scoreZ`, using only the earlier two-thirds; displayed results use the later one-third.
   When a lottery has a separate bonus pool (such as EuroMillions Lucky Stars or Set For Life Life
   Ball), a dedicated strategy and hedge weighting are calibrated for that pool.
4. The verdict compares the strategy selected on earlier walk-forward draws against the untouched
  chronological holdout using an **exact one-sided binomial test** for the three-plus objective,
  while still reporting average matches against the analytic random expectation (36/59 ≈ 0.61
  matches). Random three-plus odds are approximately 1 in 92 for UK Lotto. The verdict warns
  when fewer than ten random hits are expected on the holdout. Longer windows still do not
  guarantee sufficient statistical power. A calibrated fixed-size-set log score versus uniform
  sampling without replacement, and average matches, provide denser secondary feedback.

### How learning works

Learning is **forward-only**. A strategy is judged and rewarded only on draws that happened after
its prediction was frozen. Backtests are still used to bootstrap, but real results take over as
they accumulate.

- **Shadow ledger** (`ShadowLedger`, table `ShadowPredictions`): after every saved result (and
  after each recalculation), every live strategy freezes the line it would play for the next
  draw. It also stores its standardised per-ball scores. When that draw is saved, the lines are
  scored: matches, three-plus, and the reward below. Draws added in one request are processed in
  sequence order, so each step sees only earlier draws. Correcting a draw re-scores the lines
  that targeted it. Unscored lines that could have seen it are frozen again. Scored lines that
  saw it are marked invalid and excluded. Hedge and online-model updates already applied are not
  rewound. Learning runs after the result is committed, so a failure never blocks data entry; it
  is recorded and shown on the dashboard.
- **Composite reward** (`LearningSignals`):
  `reward = scoreZ + 0.25·(I(3+) − p₃)/√(p₃(1−p₃))`. `scoreZ` is the standardised sum of the
  drawn balls' scores; under fair draws it has mean 0 and variance 1 exactly, whatever the
  ranking. It is a dense signal for every draw, unlike three-plus hits, which occur about once in
  92 draws for UK Lotto. The three-plus term keeps the original objective in the mix. Both terms are 0 in
  expectation for a fair machine.
- **Persistent hedge ensemble** (`hedge-ensemble`, table `HedgeWeightStates`): weights start
  from the backtest's final walk-forward weights. After each real draw they are updated as
  `log w += 0.05·reward`, followed by a 1% fixed-share mix toward uniform, so a strategy that
  falls behind can recover. The weights carry over between draws and restarts instead of being
  rebuilt from uniform on every analysis. The backtest uses the same reward.
- **Champion/challenger evolution** (`StrategyOptimizer`, `LearnedStrategies.Status`): each
  rebuild runs multi-generation tournament evolution strictly inside the pre-holdout training
  prefix using variance-reduced composite fitness and blend crossover (BLX-α). The generation number
  advances only when a new draw exists, so rebuilding the same data is reproducible. At most once
  every 8 new draws, the 4 best fresh candidates (by the backtest's selection metric) join a live
  population of up to 16 challengers. Each records `BornAfterSequence`. Live challengers are never
  backtested again: they are judged only by the shadow ledger from their birth onward. When room
  is needed, the challenger with the lowest **shrunk** prospective reward
  (`mean·n/(n+20)`) is retired, but only after 20 scored draws, and never the current champion.
- **Online model** (`OnlineBallModel`, `online-logit`): a per-ball conditional-logit model over
  the same six standardised features. After each draw it takes one adaptive gradient step (with an
  EMA squared-gradient accumulator window so adaptation never freezes on long histories), with
  L2 shrinkage toward the fair-machine prior of zero weights, using features from earlier draws
  only. On a fair machine its coefficients stay near zero; a real bias would pull them away.
  The first recalculation warm-starts it walk-forward over the evaluation window and calibrates
  its initial hedge weight. It then joins the hedge ensemble and the ledger like any other strategy.
- **Promotion gate** (`ProspectiveEvaluator`): the active strategy comes from the backtest until
  100 prospective draws are scored. After that the incumbent (the hedge ensemble by default)
  is kept unless a challenger with at least 100 scored draws beats it on a **paired** test of
  per-draw reward. The test needs Bonferroni-corrected one-sided confidence of
  `1 − 0.05/m` over the `m` contenders. The challenger's three-plus rate must also not be credibly
  below random (Beta posterior). A promoted strategy is held for at least 20 draws. The decision
  and its reason are persisted in `LearningStateEntries` and shown on the dashboard.
- **Bias detection**: the per-number bias z-score and the `bias-detector` strategy target the only
  edge that could really exist: a physically biased machine or ball set. The chi-square
  uniformity verdict on the dashboard reports whether any such bias is measurable (currently:
  none, p ≈ 0.98).
- **Performance registry**: every rebuild logs each strategy's backtest results to
  `StrategyPerformanceLogs`. The dashboard's prospective leaderboard and chart show each live
  strategy's cumulative matches above random expectation and cumulative score z on real draws.
  With no edge, these wander around zero with spread growing like √draws.

All settings live in `AnalysisOptions`. New tables and columns are created on startup by
`LearningSchema`, so existing databases upgrade in place.

**Limitations.** If draws are independent and uniform (as the χ² test suggests), every reward
has mean zero. No amount of learning can then produce a lasting edge; the system is designed to
show that honestly rather than chase noise. Prospective evidence accumulates at one data point
per draw, so promotions will be rare and slow by design.

### Three-plus portfolios and API changes

The builder first greedily reduces pairwise overlap of three-plus hit events, then a seeded local
search swaps single balls to reduce the summed pairwise loss further. The refined portfolio is
kept only when its exact three-plus chance is higher. This improves coverage but does not prove
the portfolio is globally optimal. Even two disjoint six-number lines can both match three
numbers in one draw. Portfolio odds are **exact**: every possible draw (45,057,474 for UK Lotto)
is counted using bit-sliced per-line match counters, typically in well under a second, and the
same exact count is shown for an equal number of random distinct lines for comparison.
Multi-round odds assume independent rounds. Minimum line counts derived from the union bound are
necessary lower bounds, not guarantees of achieving a target chance. Bonus balls and Lucky Stars
are outside this objective.

Measured on UK Lotto (exact chance of at least one 3+ line per round; random = same number of
random distinct lines):

| Lines K | Random lines | Previous builder | Current builder | K × single-line odds (upper bound) |
|---|---|---|---|---|
| 10 | 10.50% | 10.78% | 10.81% | 10.86% |
| 20 | 19.34% | 20.86% | 20.91% | 21.73% |
| 30 | 27.68% | 30.25% | 30.30% | 32.59% |
| 50 | 41.78% | 47.02% | 47.07% | 54.32% |

The gains come only from line overlap, not from forecasting numbers: every individual line still
has the same 1 in 92 chance of 3+ matches.

API objective fields now use `ThreePlus` / `threePlus` instead of `FourPlus` / `fourPlus`;
`minimumLinesForEvenOdds` replaces `linesForEvenOdds`. New predictions use model version
`v4-three-plus`. Existing draws, predictions and evaluations remain intact; saved portfolio odds
are recalculated for the new objective. Old performance logs remain historical/exploratory.

For UK Lotto, the add-result screen accepts two rounds of six numbers in one submission. They are validated and
stored atomically as consecutive draw events with the same draw number and date. Both rounds feed
the statistics, optimizer, and future backtests. A prediction targets one chronological draw event,
so an outstanding prediction is evaluated against round 1; generate a new prediction after saving
the result to target the next future event.

The History view initially loads only the newest 100 stored rounds for a fast response; **Load All**
explicitly retrieves the complete history. Any round's six numbers can be corrected; this
invalidates the cached analysis and recalculates predictions that were evaluated against that
round. If the newest draw has only one round, the view also offers **Add Missing Round**. Missing
rounds cannot be inserted into older draw groups because doing so would rewrite the chronological
training data seen by already-stored predictions and invalidate their audit trail.

For EuroMillions, add and edit screens accept one five-number round and two distinct Lucky Stars.
Saved predictions and candidate lines include both the main-number and Lucky Star recommendations.

## API

| Endpoint | Description |
|---|---|
| `GET /api/draws?limit=` | Recent draws (newest first) |
| `GET /api/draws/history?offset=0&limit=100` | Paged draw history (newest first) |
| `GET /api/draws/latest` | Latest draw |
| `POST /api/draws` | Authenticated: add one result (compatibility endpoint) `{ "numbers": [n1..n6] }` |
| `POST /api/draws/rounds` | Authenticated: atomically add two rounds `{ "rounds": [[n1..n6], [n1..n6]] }` |
| `POST /api/draws/latest/round` | Authenticated: add round 2 when the newest draw has only one round |
| `PUT /api/draws/{id}` | Authenticated: correct a stored round's numbers `{ "numbers": [n1..n6] }` |
| `GET /api/predictions/latest` | Latest prediction with explanation |
| `POST /api/predictions/generate` | Authenticated: generate and store a prediction |
| `GET /api/predictions/history` | Prediction history with match results |
| `GET /api/statistics` | Per-number and combination-level statistics |
| `GET /api/backtesting` | Walk-forward results, random baseline, verdict |
| `GET /api/learning` | Learning generation, learned strategies (with challenger status), live hedge weights, uniformity test, performance history, and `prospective` (selection decision, forward-only leaderboard, cumulative series, online-model coefficients) |

All endpoints accept `X-Lottery: uk-lotto`, `X-Lottery: euromillions`, or `X-Lottery: set-for-life`.
POST, PUT, PATCH, and DELETE requests under `/api` also require `X-Api-Key`. If the server has no
`MutationApiKey` configured, mutation requests return `503`; missing or invalid keys return `401`
or `403`. GET endpoints and CORS preflight requests do not require the mutation key.

## Structure

- `backend/LottoPredictor.Core` — models, EF Core `LottoDbContext` (SQLite; swappable for SQL
  Server by changing the provider registration), CSV importer, analysis engine (feature
  calculator, prediction engine, backtester, genetic optimizer, statistical functions), services,
  DTOs.
- `backend/LottoPredictor.Api` — thin controllers + startup seeding and schema upgrades.
- `backend/LottoPredictor.Tests` — 70 xUnit tests including real-CSV integration tests,
  future-data-leakage proofs, bias-detection and hedge-convergence tests.
- `frontend` — React + TypeScript (Vite): lottery selector, adaptive dashboard and result entry,
  and paginated draw history with inline corrections.
