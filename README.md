# Lotto Predictor

A local web application for UK National Lottery and EuroMillions draw histories. It computes statistical
features, runs walk-forward backtesting over hand-written and machine-learned scoring strategies,
and generates a ranked prediction from the best currently-validated strategy. The engine **learns**:
each new draw triggers a genetic-optimizer generation and an online hedge ensemble re-weighting —
while honestly reporting whether anything actually beats random selection (on current data it does
not, which is the expected outcome for a fair lottery).

## Run it

Both services:

```bash
./run.sh                              # UI: http://localhost:5173
```

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

1. `FeatureCalculator` computes per-number features (era-aware frequency rate, counts over the
   last 10/25/50/100 draws, draws since last seen, average gap, gap ratio, recent-vs-long-term
   ratio, positional counts, and a **Bayesian bias z-score** of observed appearances vs a
   fair-machine expectation) plus pair/triple co-occurrence counts, combination-level
   distributions (sum, range, odd/even, consecutive numbers, low/high split) and a **chi-square
   uniformity test** over the current era — always from a strict prefix of draws.
2. `PredictionEngine` z-scores the features and combines them with a strategy's weights
   (long-term, recent, gap, momentum, bias), then searches all 6-number combinations of the top
   14 candidates, adding a pair-synergy bonus and a soft typicality penalty (extreme sum /
   odd-even balance / range / clustering are penalised, never excluded).
3. `Backtester` runs walk-forward validation: for each of the last 200 draws it predicts using
   only earlier draws and records matches, for every candidate strategy, for an **online hedge
   ensemble**, and for a seeded random baseline. The strategy (or ensemble) with the best
   **recency-weighted** walk-forward average (exponential decay, half-life 50 draws) becomes the
   active strategy.
4. The verdict compares the best strategy against the analytic random expectation (36/59 ≈ 0.61
   matches) using a **Bonferroni-corrected** significance band — picking the best of N tested
   strategies inflates apparent skill, so the noise band widens with N — and states plainly when
   the difference is within statistical noise.

### How learning works

- **Genetic optimizer** (`StrategyOptimizer`): every analysis rebuild is one generation. Elite
  strategies are mutated with an annealed step size, pairs of parents are crossed over, and
  random immigrants keep diversity. All candidates are judged by the same leak-free walk-forward
  backtest; the top survivors are persisted to the `LearnedStrategies` table so learning
  accumulates across restarts and new draws.
- **Hedge ensemble** (`hedge-ensemble`): inside the backtest, every strategy's per-number scores
  are blended using multiplicative weights updated draw-by-draw (`w ×= e^(0.1·matches)`), so
  strategies that match more get exponentially more say. This algorithm has provably near-optimal
  regret vs the best single strategy in hindsight, and it competes as a candidate itself.
- **Bias detection**: the per-number bias z-score and the `bias-detector` strategy target the only
  edge that could really exist — a physically biased machine or ball set. The chi-square
  uniformity verdict on the dashboard reports whether any such bias is measurable (currently:
  none, p ≈ 0.98).
- **Performance registry**: every rebuild logs each strategy's results to
  `StrategyPerformanceLogs`, so improvement (or the honest lack of it) is auditable over time.

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
| `GET /api/draws/history?limit=100&loadAll=false` | Newest 100 rounds; set `loadAll=true` only for the complete history |
| `GET /api/draws/latest` | Latest draw |
| `POST /api/draws` | Add one result (compatibility endpoint) `{ "numbers": [n1..n6] }` |
| `POST /api/draws/rounds` | Atomically add two rounds `{ "rounds": [[n1..n6], [n1..n6]] }` |
| `POST /api/draws/latest/round` | Add round 2 when the newest draw has only one round |
| `PUT /api/draws/{id}` | Correct a stored round's numbers `{ "numbers": [n1..n6] }` |
| `GET /api/predictions/latest` | Latest prediction with explanation |
| `POST /api/predictions/generate` | Generate and store a prediction |
| `GET /api/predictions/history` | Prediction history with match results |
| `GET /api/statistics` | Per-number and combination-level statistics |
| `GET /api/backtesting` | Walk-forward results, random baseline, verdict |
| `GET /api/learning` | Learning generation, learned strategies, hedge weights, uniformity test, performance history |

All endpoints accept `X-Lottery: uk-lotto` or `X-Lottery: euromillions`.

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
