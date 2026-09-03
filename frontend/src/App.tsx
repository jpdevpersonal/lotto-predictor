import { useState } from "react";
import Dashboard from "./pages/Dashboard";
import AddResult from "./pages/AddResult";
import DrawHistory from "./pages/DrawHistory";

export default function App() {
  const [page, setPage] = useState<"dashboard" | "add" | "history">(
    "dashboard",
  );

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
      </header>
      {page === "dashboard" ? (
        <Dashboard onAddResult={() => setPage("add")} />
      ) : page === "history" ? (
        <DrawHistory />
      ) : (
        <AddResult onDone={() => setPage("dashboard")} />
      )}
    </div>
  );
}
