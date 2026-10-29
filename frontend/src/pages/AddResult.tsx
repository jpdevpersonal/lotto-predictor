import { useEffect, useState } from "react";
import { api } from "../api";
import type { LotteryProfile } from "../types";

const emptyValues = (count: number) => Array<string>(count).fill("");

export default function AddResult({
  lottery,
  onDone,
}: {
  lottery: LotteryProfile;
  onDone: () => void;
}) {
  const [drawNumber, setDrawNumber] = useState("");
  const [date, setDate] = useState(() =>
    new Date().toISOString().slice(0, 10),
  );
  const [rounds, setRounds] = useState<string[][]>(() =>
    Array.from({ length: lottery.roundCount }, () =>
      emptyValues(lottery.mainNumberCount),
    ),
  );
  const [bonuses, setBonuses] = useState(() =>
    emptyValues(lottery.roundCount),
  );
  const [luckyStars, setLuckyStars] = useState<string[][]>(() =>
    Array.from({ length: lottery.roundCount }, () =>
      emptyValues(lottery.luckyStarCount),
    ),
  );
  const [drawNumberEdited, setDrawNumberEdited] = useState(false);
  const [error, setError] = useState("");
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    let active = true;
    api
      .drawHistory(0, 10)
      .then((history) => {
        if (!active || history.items.length === 0) return;
        const next = Math.max(...history.items.map((d) => d.drawNumber)) + 1;
        setDrawNumber((current) => (current === "" ? String(next) : current));
      })
      .catch(() => {});
    return () => {
      active = false;
    };
  }, []);

  const fullDate = date
    ? new Date(`${date}T00:00:00`).toLocaleDateString("en-GB", {
        weekday: "long",
        day: "numeric",
        month: "long",
        year: "numeric",
      })
    : "";

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
      setError(
        `Please enter all ${lottery.mainNumberCount} numbers in every round.`,
      );
      return;
    }
    const parsedDrawNumber =
      drawNumber.trim() === "" ? null : parseInt(drawNumber, 10);
    if (drawNumber.trim() !== "" && Number.isNaN(parsedDrawNumber)) {
      setError("Draw number must be a valid number.");
      return;
    }
    if (!date) {
      setError("Please enter the draw date.");
      return;
    }
    const parsedBonuses = bonuses.map((value) =>
      value.trim() === "" ? null : parseInt(value, 10),
    );
    if (parsedBonuses.some((bonus) => bonus != null && Number.isNaN(bonus))) {
      setError("Bonus balls must be valid numbers or left blank.");
      return;
    }
    const parsedStars = luckyStars.map((round) =>
      round.map((value) => parseInt(value, 10)),
    );
    if (
      parsedStars.some((round) =>
        round.some((number) => Number.isNaN(number)),
      )
    ) {
      setError(
        `Please enter all ${lottery.luckyStarCount} ${lottery.bonusLabel}${lottery.luckyStarCount > 1 ? "s" : ""}.`,
      );
      return;
    }
    setSaving(true);
    try {
      await api.addDrawRounds(
        numbers,
        parsedBonuses,
        parsedStars,
        parsedDrawNumber,
        date,
      );
      onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to save result");
    } finally {
      setSaving(false);
    }
  };

  return (
    <section className="card-form">
      <h2>Add new results</h2>
      <p className="section-desc">
        Enter the official numbers after each draw. This keeps the analysis
        honest: the more real results the model has, the better it can test itself.
      </p>
      <p className="muted">
        Enter {lottery.roundCount === 1 ? "the" : "both"} {lottery.name} round
        {lottery.roundCount === 1 ? "" : "s"}. Statistics, learning, and
        backtesting are recomputed after saving.
      </p>
      <div className="rounds-input">
        <fieldset className="round-input">
          <legend>Draw Details</legend>
          <div className="draw-details">
            <label className="field">
              <span>Draw number</span>
              <input
                type="number"
                min={1}
                value={drawNumber}
                placeholder="Auto"
                onChange={(event) => {
                  setDrawNumber(event.target.value);
                  setDrawNumberEdited(true);
                }}
              />
              <small>
                {drawNumberEdited
                  ? "Manually set."
                  : "Auto-set to the next draw; edit if needed."}
              </small>
            </label>
            <label className="field">
              <span>Draw date</span>
              <input
                type="date"
                value={date}
                onChange={(event) => setDate(event.target.value)}
              />
              <small>{fullDate}</small>
            </label>
          </div>
        </fieldset>
        {rounds.map((round, roundIndex) => (
          <fieldset className="round-input" key={roundIndex}>
            <legend>Round {roundIndex + 1}</legend>
            <div className="number-inputs">
              {round.map((value, numberIndex) => (
                <input
                  key={numberIndex}
                  type="number"
                  min={1}
                  max={lottery.mainPoolSize}
                  value={value}
                  aria-label={`Round ${roundIndex + 1}, number ${numberIndex + 1}`}
                  placeholder={`N${numberIndex + 1}`}
                  onChange={(event) =>
                    setValue(roundIndex, numberIndex, event.target.value)
                  }
                />
              ))}
              {lottery.luckyStarCount > 0
                ? luckyStars[roundIndex].map((value, starIndex) => (
                    <input
                      className="star-input"
                      key={`star-${starIndex}`}
                      type="number"
                      min={1}
                      max={lottery.luckyStarPoolSize}
                      value={value}
                      aria-label={`${lottery.bonusLabel} ${starIndex + 1}`}
                      placeholder={`${lottery.bonusLabel} ${starIndex + 1}`}
                      onChange={(event) =>
                        setLuckyStars((current) =>
                          current.map((stars, index) =>
                            index === roundIndex
                              ? stars.map((star, position) =>
                                  position === starIndex
                                    ? event.target.value
                                    : star,
                                )
                              : stars,
                          ),
                        )
                      }
                    />
                  ))
                : (
                    <input
                      type="number"
                      min={1}
                      max={lottery.mainPoolSize}
                      value={bonuses[roundIndex]}
                      aria-label={`Round ${roundIndex + 1}, bonus ball`}
                      placeholder="Bonus"
                      onChange={(event) =>
                        setBonuses((current) =>
                          current.map((value, index) =>
                            index === roundIndex ? event.target.value : value,
                          ),
                        )
                      }
                    />
                  )}
            </div>
          </fieldset>
        ))}
      </div>
      {error && <p className="error">{error}</p>}
      <div className="actions">
        <button onClick={save} disabled={saving}>
          {saving ? "Saving…" : `Save ${lottery.roundCount === 1 ? "Result" : "Both Rounds"}`}
        </button>
        <button className="secondary" onClick={onDone} disabled={saving}>
          Cancel
        </button>
      </div>
    </section>
  );
}
