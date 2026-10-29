import { useState } from "react";
import Dashboard from "./pages/Dashboard";
import AddResult from "./pages/AddResult";
import DrawHistory from "./pages/DrawHistory";
import { setApiLottery } from "./api";
import { LOTTERIES, type LotteryKey } from "./types";

const TABS = [
  { key: "dashboard", title: "Dashboard", hint: "Predictions & analysis" },
  { key: "add", title: "Add Result", hint: "Enter the latest draw" },
  { key: "history", title: "History", hint: "Browse & correct draws" },
] as const;

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
        <div className="app-header-inner">
          <div className="app-header-top">
            <div className="brand">
              <span className="brand-mark">LP</span>
              <div>
                <h1>Lotto Predictor</h1>
                <span className="tagline">Statistical analysis, not guesswork</span>
              </div>
            </div>
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
                <option value="set-for-life">Set For Life</option>
              </select>
            </label>
          </div>
          <nav className="nav-tabs" aria-label="Main navigation">
            {TABS.map((tab) => (
              <button
                key={tab.key}
                className={page === tab.key ? "active" : ""}
                aria-current={page === tab.key ? "page" : undefined}
                onClick={() => setPage(tab.key)}
              >
                <span className="tab-title">{tab.title}</span>
                <span className="tab-hint">{tab.hint}</span>
              </button>
            ))}
          </nav>
        </div>
      </header>
      <main className="app-main">
        {page === "dashboard" ? (
          <Dashboard key={lotteryKey} lottery={lottery} onAddResult={() => setPage("add")} />
        ) : page === "history" ? (
          <DrawHistory key={lotteryKey} lottery={lottery} />
        ) : (
          <AddResult key={lotteryKey} lottery={lottery} onDone={() => setPage("dashboard")} />
        )}
      </main>
      <footer className="app-footer">
        Lottery draws are random. This tool measures historical patterns and
        tests them honestly against chance — it cannot guarantee winnings. Play responsibly.
      </footer>
    </div>
  );
}
