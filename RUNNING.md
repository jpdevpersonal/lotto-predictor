# Running Lotto Predictor locally (Ubuntu + VS Code)

## Tech stack

- **Backend**: C# / ASP.NET Core (**.NET 10**), Entity Framework Core with **SQLite**
  (`lotto.db` and `euromillions.db`, auto-created; each also stores its own learned strategies
  and performance log). Business
  logic lives in the `LottoPredictor.Core` class library, so the DB provider can be swapped for
  SQL Server without touching the logic.
- **Frontend**: **React 18 + TypeScript** on **Vite**, plain CSS, no UI framework.
- **Tests**: xUnit (70 tests).

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

The launcher installs frontend dependencies when needed, waits for the API, starts the UI at
http://localhost:5173, and stops both services when you press Ctrl+C. To run the services in
separate terminals instead, use the commands below.

**Terminal 1 — API** (first run creates both databases and imports their CSV histories):

```bash
cd backend/LottoPredictor.Api
dotnet run --launch-profile http
```

→ http://localhost:5080 — the log shows "Analysis ready: 3226 draws, pool 1-59, learning
generation N, active strategy '…'" plus the backtest verdict. Each startup (and each new draw)
runs one learning generation, so the first request after adding a result takes a few seconds
while the walk-forward backtest recomputes.

**Terminal 2 — UI**:

```bash
cd frontend
npm install     # first time only
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
- Recommended VS Code extensions: **C# Dev Kit** for backend debugging (F5 works against the
  `http` launch profile); the built-in TypeScript support handles the frontend.
- The API port is pinned to 5080 in
  `backend/LottoPredictor.Api/Properties/launchSettings.json`; if you change it, update the
  proxy target in `frontend/vite.config.ts` to match.
