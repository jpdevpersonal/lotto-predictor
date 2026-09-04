import { useCallback, useEffect, useState } from "react";
import { api } from "../api";
import type { DrawDto, DrawHistoryDto, LotteryProfile } from "../types";

const emptyNumbers = (count: number) => Array<string>(count).fill("");

function parseNumbers(values: string[]): number[] | null {
  const numbers = values.map((value) => parseInt(value, 10));
  return numbers.some((number) => Number.isNaN(number)) ? null : numbers;
}

export default function DrawHistory({ lottery }: { lottery: LotteryProfile }) {
  const [history, setHistory] = useState<DrawHistoryDto | null>(null);
  const [showingAll, setShowingAll] = useState(false);
  const [loadingAll, setLoadingAll] = useState(false);
  const [editingId, setEditingId] = useState<number | null>(null);
  const [editValues, setEditValues] = useState<string[]>(() =>
    emptyNumbers(lottery.mainNumberCount),
  );
  const [editBonus, setEditBonus] = useState("");
  const [editStars, setEditStars] = useState<string[]>(() =>
    emptyNumbers(lottery.luckyStarCount),
  );
  const [showAddRound, setShowAddRound] = useState(false);
  const [roundValues, setRoundValues] = useState<string[]>(() =>
    emptyNumbers(lottery.mainNumberCount),
  );
  const [roundBonus, setRoundBonus] = useState("");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");

  const load = useCallback(async (loadAll = false) => {
    try {
      setHistory(await api.drawHistory(loadAll));
      setError("");
    } catch (loadError) {
      setError(
        loadError instanceof Error
          ? loadError.message
          : "Failed to load history",
      );
    }
  }, []);

  useEffect(() => {
    void load(false);
  }, [load]);

  const beginEdit = (draw: DrawDto) => {
    setEditingId(draw.id);
    setEditValues(draw.numbers.map(String));
    setEditBonus(draw.bonus?.toString() ?? "");
    setEditStars(draw.luckyStars.map(String));
    setShowAddRound(false);
    setError("");
  };

  const saveEdit = async () => {
    const numbers = parseNumbers(editValues);
    if (!numbers || editingId == null) {
      setError(`Enter all ${lottery.mainNumberCount} numbers before saving.`);
      return;
    }

    setSaving(true);
    try {
      const bonus = editBonus.trim() === "" ? null : parseInt(editBonus, 10);
      const stars = editStars.map((value) => parseInt(value, 10));
      await api.updateDraw(editingId, numbers, bonus, stars);
      setEditingId(null);
      await load(showingAll);
    } catch (saveError) {
      setError(
        saveError instanceof Error
          ? saveError.message
          : "Failed to update draw",
      );
    } finally {
      setSaving(false);
    }
  };

  const addMissingRound = async () => {
    const numbers = parseNumbers(roundValues);
    if (!numbers) {
      setError(`Enter all ${lottery.mainNumberCount} numbers before adding the round.`);
      return;
    }

    setSaving(true);
    try {
      const bonus = roundBonus.trim() === "" ? null : parseInt(roundBonus, 10);
      await api.addLatestRound(numbers, bonus);
      setRoundValues(emptyNumbers(lottery.mainNumberCount));
      setRoundBonus("");
      setShowAddRound(false);
      await load(showingAll);
    } catch (saveError) {
      setError(
        saveError instanceof Error ? saveError.message : "Failed to add round",
      );
    } finally {
      setSaving(false);
    }
  };

  const setNumberValue = (
    values: string[],
    setter: (next: string[]) => void,
    index: number,
    value: string,
  ) => {
    const next = [...values];
    next[index] = value;
    setter(next);
  };

  const latestDrawNumber = history?.items[0]?.drawNumber;
  const latestRoundCount = history?.items.filter(
    (draw) => draw.drawNumber === latestDrawNumber,
  ).length;
  const canAddLatestRound = lottery.roundCount > 1 && latestRoundCount === 1;

  const loadAll = async () => {
    setLoadingAll(true);
    try {
      await load(true);
      setShowingAll(true);
    } finally {
      setLoadingAll(false);
    }
  };

  return (
    <section className="history-view">
      <div className="history-heading">
        <div>
          <h2>Draw History</h2>
          <p className="muted">
            Review every stored {lottery.name} draw and correct its numbers
            {lottery.luckyStarCount > 0 ? " or Lucky Stars" : " or bonus ball"}.
          </p>
        </div>
        {canAddLatestRound && (
          <button
            className="secondary"
            onClick={() => {
              setShowAddRound((visible) => !visible);
              setEditingId(null);
              setError("");
            }}
          >
            Add Missing Round
          </button>
        )}
      </div>

      {showAddRound && latestDrawNumber != null && (
        <div className="history-editor">
          <strong>Add round 2 to draw #{latestDrawNumber}</strong>
          <div className="compact-number-inputs">
            {roundValues.map((value, index) => (
              <input
                key={index}
                type="number"
                min={1}
                max={lottery.mainPoolSize}
                value={value}
                aria-label={`New round number ${index + 1}`}
                onChange={(event) =>
                  setNumberValue(
                    roundValues,
                    setRoundValues,
                    index,
                    event.target.value,
                  )
                }
              />
            ))}
            <input
              type="number"
              min={1}
              max={59}
              value={roundBonus}
              aria-label="New round bonus ball"
              placeholder="Bonus"
              onChange={(event) => setRoundBonus(event.target.value)}
            />
          </div>
          <div className="inline-actions">
            <button onClick={addMissingRound} disabled={saving}>
              {saving ? "Saving…" : "Save Round"}
            </button>
            <button
              className="secondary"
              onClick={() => setShowAddRound(false)}
              disabled={saving}
            >
              Cancel
            </button>
          </div>
        </div>
      )}

      {error && <p className="error">{error}</p>}

      {!history ? (
        <p className="loading-state">Loading…</p>
      ) : (
        <>
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th>Draw</th>
                  <th>Date</th>
                  <th>Round</th>
                  <th>Numbers</th>
                  <th>{lottery.luckyStarCount > 0 ? "Lucky Stars" : "Bonus"}</th>
                  <th>Source</th>
                  <th>Action</th>
                </tr>
              </thead>
              <tbody>
                {history.items.map((draw) => {
                  const isEditing = editingId === draw.id;
                  return (
                    <tr key={draw.id}>
                      <td>#{draw.drawNumber}</td>
                      <td>{draw.date}</td>
                      <td>{draw.machine || "—"}</td>
                      <td>
                        {isEditing ? (
                          <div className="compact-number-inputs">
                            {editValues.map((value, index) => (
                              <input
                                key={index}
                                type="number"
                                min={1}
                                max={lottery.mainPoolSize}
                                value={value}
                                aria-label={`Draw ${draw.drawNumber}, number ${index + 1}`}
                                onChange={(event) =>
                                  setNumberValue(
                                    editValues,
                                    setEditValues,
                                    index,
                                    event.target.value,
                                  )
                                }
                              />
                            ))}
                          </div>
                        ) : (
                          <span className="history-numbers">
                            {draw.numbers.join("  ")}
                          </span>
                        )}
                      </td>
                      <td>
                        {isEditing && lottery.luckyStarCount > 0 ? (
                          <div className="compact-number-inputs">
                            {editStars.map((value, index) => (
                              <input
                                className="star-input"
                                key={index}
                                type="number"
                                min={1}
                                max={lottery.luckyStarPoolSize}
                                value={value}
                                aria-label={`Draw ${draw.drawNumber}, Lucky Star ${index + 1}`}
                                onChange={(event) =>
                                  setNumberValue(
                                    editStars,
                                    setEditStars,
                                    index,
                                    event.target.value,
                                  )
                                }
                              />
                            ))}
                          </div>
                        ) : isEditing ? (
                          <input
                            type="number"
                            min={1}
                            max={lottery.mainPoolSize}
                            value={editBonus}
                            aria-label={`Draw ${draw.drawNumber}, bonus ball`}
                            placeholder="Bonus"
                            onChange={(event) =>
                              setEditBonus(event.target.value)
                            }
                          />
                        ) : (
                          (lottery.luckyStarCount > 0
                            ? draw.luckyStars.join("  ")
                            : (draw.bonus ?? "—"))
                        )}
                      </td>
                      <td>{draw.source}</td>
                      <td>
                        {isEditing ? (
                          <div className="inline-actions">
                            <button onClick={saveEdit} disabled={saving}>
                              Save
                            </button>
                            <button
                              className="secondary"
                              onClick={() => setEditingId(null)}
                              disabled={saving}
                            >
                              Cancel
                            </button>
                          </div>
                        ) : (
                          <button
                            className="secondary"
                            onClick={() => beginEdit(draw)}
                          >
                            Edit
                          </button>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>

          <div className="pagination">
            <span className="muted">
              Showing {history.items.length} of {history.total} rounds
            </span>
            {!showingAll && history.items.length < history.total && (
              <button
                className="secondary"
                disabled={loadingAll}
                onClick={loadAll}
              >
                {loadingAll ? "Loading…" : "Load All"}
              </button>
            )}
          </div>
        </>
      )}
    </section>
  );
}
