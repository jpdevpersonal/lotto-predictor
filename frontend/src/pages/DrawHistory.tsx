import { useCallback, useEffect, useState } from "react";
import { api } from "../api";
import type { DrawDto, DrawHistoryDto } from "../types";

const emptyNumbers = () => ["", "", "", "", "", ""];

function parseNumbers(values: string[]): number[] | null {
  const numbers = values.map((value) => parseInt(value, 10));
  return numbers.some((number) => Number.isNaN(number)) ? null : numbers;
}

export default function DrawHistory() {
  const [history, setHistory] = useState<DrawHistoryDto | null>(null);
  const [showingAll, setShowingAll] = useState(false);
  const [loadingAll, setLoadingAll] = useState(false);
  const [editingId, setEditingId] = useState<number | null>(null);
  const [editValues, setEditValues] = useState<string[]>(emptyNumbers());
  const [showAddRound, setShowAddRound] = useState(false);
  const [roundValues, setRoundValues] = useState<string[]>(emptyNumbers());
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
    setShowAddRound(false);
    setError("");
  };

  const saveEdit = async () => {
    const numbers = parseNumbers(editValues);
    if (!numbers || editingId == null) {
      setError("Enter all six numbers before saving.");
      return;
    }

    setSaving(true);
    try {
      await api.updateDraw(editingId, numbers);
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
      setError("Enter all six numbers before adding the round.");
      return;
    }

    setSaving(true);
    try {
      await api.addLatestRound(numbers);
      setRoundValues(emptyNumbers());
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
  const canAddLatestRound = latestRoundCount === 1;

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
            Review every stored round and correct its six numbers.
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
                max={59}
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
                                max={59}
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
