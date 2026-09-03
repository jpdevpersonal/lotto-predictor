import { useState } from "react";
import { api } from "../api";

const emptyRound = () => ["", "", "", "", "", ""];

export default function AddResult({ onDone }: { onDone: () => void }) {
  const [rounds, setRounds] = useState<string[][]>([
    emptyRound(),
    emptyRound(),
  ]);
  const [error, setError] = useState("");
  const [saving, setSaving] = useState(false);

  const setValue = (roundIndex: number, numberIndex: number, value: string) => {
    setRounds((current) =>
      current.map((round, index) => {
        if (index !== roundIndex) return round;
        const next = [...round];
        next[numberIndex] = value;
        return next;
      }),
    );
  };

  const save = async () => {
    const numbers = rounds.map((round) =>
      round.map((value) => parseInt(value, 10)),
    );
    if (numbers.some((round) => round.some((number) => Number.isNaN(number)))) {
      setError("Please enter all six numbers in both rounds.");
      return;
    }
    setSaving(true);
    try {
      await api.addDrawRounds(numbers);
      onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to save result");
    } finally {
      setSaving(false);
    }
  };

  return (
    <section className="card-form">
      <h2>Add New Results</h2>
      <p className="muted">
        Enter both rounds of six main numbers. They are stored as independent
        rounds under the same draw number, then statistics, learning, and
        backtesting are recomputed using both rounds.
      </p>
      <div className="rounds-input">
        {rounds.map((round, roundIndex) => (
          <fieldset className="round-input" key={roundIndex}>
            <legend>Round {roundIndex + 1}</legend>
            <div className="number-inputs">
              {round.map((value, numberIndex) => (
                <input
                  key={numberIndex}
                  type="number"
                  min={1}
                  max={59}
                  value={value}
                  aria-label={`Round ${roundIndex + 1}, number ${numberIndex + 1}`}
                  placeholder={`N${numberIndex + 1}`}
                  onChange={(event) =>
                    setValue(roundIndex, numberIndex, event.target.value)
                  }
                />
              ))}
            </div>
          </fieldset>
        ))}
      </div>
      {error && <p className="error">{error}</p>}
      <div className="actions">
        <button onClick={save} disabled={saving}>
          {saving ? "Saving…" : "Save Both Rounds"}
        </button>
        <button className="secondary" onClick={onDone} disabled={saving}>
          Cancel
        </button>
      </div>
    </section>
  );
}
