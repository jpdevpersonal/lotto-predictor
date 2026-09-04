import { useState } from "react";
import Dashboard from "./pages/Dashboard";
import AddResult from "./pages/AddResult";
import DrawHistory from "./pages/DrawHistory";
import { setApiLottery } from "./api";
import { LOTTERIES, type LotteryKey } from "./types";

export default function App() {
  const [page, setPage] = useState<"dashboard" | "add" | "history">(
    "dashboard",
  );
  const [lotteryKey, setLotteryKey] = useState<LotteryKey>("uk-lotto");
  const lottery = LOTTERIES[lotteryKey];
  setApiLottery(lotteryKey);

  return (
    <div className="app-shell">
      <header className="app-header">
        <div className="brand">
          <span className="brand-mark">LP</span>
          <div>
            <h1>Lotto Predictor</h1>
            <span className="tagline">Statistical analysis, not guesswork</span>
          </div>
        </div>
        <div className="header-actions">
          <label className="lottery-switcher">
            <span>Lottery</span>
            <select
              value={lotteryKey}
              onChange={(event) => {
                setLotteryKey(event.target.value as LotteryKey);
                setPage("dashboard");
              }}
            >
              <option value="uk-lotto">UK National Lottery</option>
              <option value="euromillions">EuroMillions</option>
            </select>
          </label>
          <nav className="nav-pill">
          <button
            className={page === "dashboard" ? "active" : ""}
            onClick={() => setPage("dashboard")}
          >
            Dashboard
          </button>
          <button
            className={page === "add" ? "active" : ""}
            onClick={() => setPage("add")}
          >
            Add Result
          </button>
          <button
            className={page === "history" ? "active" : ""}
            onClick={() => setPage("history")}
          >
            History
          </button>
          </nav>
        </div>
      </header>
      {page === "dashboard" ? (
        <Dashboard key={lotteryKey} lottery={lottery} onAddResult={() => setPage("add")} />
      ) : page === "history" ? (
        <DrawHistory key={lotteryKey} lottery={lottery} />
      ) : (
        <AddResult key={lotteryKey} lottery={lottery} onDone={() => setPage("dashboard")} />
      )}
    </div>
  );
}
