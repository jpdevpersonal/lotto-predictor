# Lotto Predictor

A local web application for UK National Lottery and EuroMillions draw histories. It computes statistical
features, runs walk-forward backtesting over hand-written and machine-learned scoring strategies,
and generates a ranked prediction targeting **at least three main-number matches on one line**.
Each analysis replays training from clean seeds and evaluates a later chronological holdout.
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

1. `FeatureCalculator` computes per-number features (era-aware frequency rate, counts over the
   last 10/25/50/100 draws, draws since last seen, average gap, gap ratio, recent-vs-long-term
   ratio, positional counts, and a **Bayesian bias z-score** of observed appearances vs a
   fair-machine expectation) plus pair/triple co-occurrence counts, combination-level
   distributions (sum, range, odd/even, consecutive numbers, low/high split) and a **chi-square
   uniformity test** over the current era — always from a strict prefix of draws.
2. `PredictionEngine` z-scores the features and combines them with a strategy's weights
   (long-term, recent, gap, momentum, bias, bonus), then searches combinations of the top
   18 candidates, adding a pair-synergy bonus and a soft typicality penalty (extreme sum /
   odd-even balance / range / clustering are penalised, never excluded).
3. `Backtester` runs walk-forward validation: for each of the last 1,000 draws (or available history
   after 150 warmup draws) it predicts using
   only earlier draws and records matches, for every candidate strategy, for an **online hedge
   ensemble**, and for a seeded random baseline. Selection ranks three-plus hit rate first,
   then average matches, using only the earlier two-thirds; displayed results use the later
   one-third. Recency-weighted averages remain secondary diagnostics.
4. The verdict compares the strategy selected on earlier walk-forward draws against the untouched
  chronological holdout using an **exact one-sided binomial test** for the three-plus objective,
  while still reporting average matches against the analytic random expectation (36/59 ≈ 0.61
  matches). Random three-plus odds are approximately 1 in 92 for UK Lotto. The verdict warns
  when fewer than ten random hits are expected on the holdout. Longer windows still do not
  guarantee sufficient statistical power. A calibrated fixed-size-set log score versus uniform
  sampling without replacement, and average matches, provide denser secondary feedback.

### How learning works

- **Genetic optimizer** (`StrategyOptimizer`): each rebuild first ranks built-in seeds on a
  training prefix ending before the final holdout, then generates one deterministic generation.
  Candidates compete on selection-period three-plus hits with average matches as tie-breaker.
  Selection survivors are persisted for reporting only. Previously saved winners are never
  reused as seeds: they may already have seen today's historical holdout in earlier rebuilds.
  Repeated checks on overlapping historical windows remain exploratory; confirm any apparent
  advantage using predictions recorded before genuinely future draws.
- **Hedge ensemble** (`hedge-ensemble`): inside the backtest, every strategy's per-number scores
  are blended using multiplicative weights updated after each draw (`w ×= e^(0.1·I(matches≥3))`).
  Its prediction always uses past weights only; it competes as a predefined online algorithm.
- **Bias detection**: the per-number bias z-score and the `bias-detector` strategy target the only
  edge that could really exist — a physically biased machine or ball set. The chi-square
  uniformity verdict on the dashboard reports whether any such bias is measurable (currently:
  none, p ≈ 0.98).
- **Performance registry**: every rebuild logs each strategy's results to
  `StrategyPerformanceLogs`, so improvement (or the honest lack of it) is auditable over time.

### Three-plus portfolios and API changes

The builder heuristically reduces pairwise overlap of three-plus hit events; it does not prove a
globally optimal portfolio. Even two disjoint six-number lines can both match three numbers in
one draw. Odds are exact for one/two lines or mutually exclusive hit events; other portfolios use
100,000 seeded uniform simulations with a 95% confidence interval. Multi-round odds assume
independent rounds. Minimum line counts derived from the union bound are necessary lower bounds,
not guarantees of achieving a target chance. Bonus balls and Lucky Stars are outside this objective.

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
| `GET /api/learning` | Learning generation, learned strategies, hedge weights, uniformity test, performance history |

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
