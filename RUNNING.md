# Running Lotto Predictor locally (Ubuntu + VS Code)

## Tech stack

- **Backend**: C# / ASP.NET Core (**.NET 10**), Entity Framework Core with **SQLite**
  (`lotto.db` and `euromillions.db`, auto-created; each also stores its own learned strategies
  and performance log). Business
  logic lives in the `LottoPredictor.Core` class library, so the DB provider can be swapped for
  SQL Server without touching the logic.
- **Frontend**: **React 18 + TypeScript** on **Vite**, plain CSS, no UI framework.
- **Tests**: xUnit.

## Prerequisites

```bash
dotnet --version   # .NET 10 SDK required
node --version     # Node 18+ required
```

## Running it

Open the folder in VS Code:

```bash
code ~/Development/lottoPredictor
```

Start both services with one command:

```bash
./run.sh
```

The launcher installs frontend dependencies when needed, creates a per-run local mutation key,
passes it to both services, waits for the API, starts the UI at http://localhost:5173, and stops
both services when you press Ctrl+C. To run the services in separate terminals instead, use the
commands below.

**Terminal 1 — API** (first run creates both databases and imports their CSV histories):

```bash
cd backend/LottoPredictor.Api
dotnet run --launch-profile http -- --MutationApiKey=replace-with-local-dev-key
```

→ http://localhost:5080 — the log shows "Analysis ready: 3226 draws, pool 1-59, learning
generation N, active strategy '…'" plus the backtest verdict. A new optimiser generation is
created only for a new dataset size; restarting the app or refreshing analysis for unchanged data
reuses the existing logged generation.

**Terminal 2 — UI**:

```bash
cd frontend
npm install     # first time only
cp .env.example .env.local
# edit .env.local so VITE_MUTATION_API_KEY matches the backend MutationApiKey
npm run dev
```

→ http://localhost:5173 (Vite proxies `/api` to port 5080).

**Tests**:

```bash
cd backend
dotnet test
```

## Useful to know

- Use the **Lottery** selector in the header to switch between UK National Lottery and
  EuroMillions. Every API request is routed to the selected lottery's separate database.
- POST, PUT, PATCH, and DELETE requests under `/api` require `X-Api-Key`. The backend expected
  value comes from `MutationApiKey`; the frontend reads the matching value from
  `VITE_MUTATION_API_KEY`. Leave real local secrets in untracked files or shell environment
  variables only.
- UK result entry requires two rounds of six numbers with optional bonus balls. EuroMillions
  requires one round of five numbers plus two Lucky Stars. Both games support prediction,
  candidate lines, history, inline correction, statistics, learning, and backtesting.
- The History tab loads the newest 100 rounds initially. Use **Load All** to retrieve the complete
  history. Correcting numbers automatically invalidates and rebuilds the analysis; predictions
  evaluated against the corrected round are recalculated. A missing second round can be added to
  the newest draw group from this view.
- To re-import a CSV from scratch, stop the API and delete the corresponding database:
  `backend/LottoPredictor.Api/lotto.db` or `backend/LottoPredictor.Api/euromillions.db`.
  This also resets the learning state (learned strategies and the performance log live in the
  same database); existing databases are upgraded in place on startup.
- To point at a different CSV:
  `dotnet run --launch-profile http -- --CsvImportPath=/path/to/file.csv`
- To point at a different EuroMillions CSV:
  `dotnet run --launch-profile http -- --EuroMillionsCsvImportPath=/path/to/euromillions.csv`
- The dashboard portfolio control chooses fixed `K` lines, defaulting to `1`. The reported primary
  objective is `P(at least one of K fixed lines matches at least four main numbers)`. Main-number
  matches are evaluated per line; four numbers scattered across different lines are not counted as
  a hit. Bonus balls and Lucky Stars are shown separately.
- To reproduce the revised comparison, start the API and request the same `count` for each supported
  game:

```bash
curl -H 'X-Lottery: uk-lotto' 'http://localhost:5080/api/backtesting'
curl -H 'X-Lottery: uk-lotto' 'http://localhost:5080/api/predictions/lines?count=50'
curl -H 'X-Lottery: euromillions' 'http://localhost:5080/api/backtesting'
curl -H 'X-Lottery: euromillions' 'http://localhost:5080/api/predictions/lines?count=50'
```

  The backtesting response contains exact single-line random four-plus probability, expected random
  four-plus hits, observed strategy hit counts/rates, and confidence intervals. The lines response
  contains coverage-optimised portfolio probability and a matched random-distinct portfolio baseline
  estimated with reproducible simulation. These comparisons do not assume historical results contain
  a predictive advantage.
- Recommended VS Code extensions: **C# Dev Kit** for backend debugging (F5 works against the
  `http` launch profile); the built-in TypeScript support handles the frontend.
- The API port is pinned to 5080 in
  `backend/LottoPredictor.Api/Properties/launchSettings.json`; if you change it, update the
  proxy target in `frontend/vite.config.ts` to match.
