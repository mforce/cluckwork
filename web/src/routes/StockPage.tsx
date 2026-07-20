import { useEffect, useState } from "react";
import { getStock, listEggLotMovements, listEggLots } from "../api/cluckwork";
import type { EggLotRow, EggMovementRow, StockRow } from "../api/cluckwork";

// F2 (#22): current sellable stock by grade; withdrawal-restricted quantities
// are shown separately — they exist but cannot be sold yet.
// #101: each grade expands into its lots, each lot into its movement ledger —
// the explicit rows behind every cached balance.
export function StockPage() {
  const [rows, setRows] = useState<StockRow[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [openGrade, setOpenGrade] = useState<string | null>(null);
  const [lots, setLots] = useState<EggLotRow[]>([]);
  const [openLot, setOpenLot] = useState<string | null>(null);
  const [movements, setMovements] = useState<EggMovementRow[] | null>(null);

  useEffect(() => {
    getStock()
      .then(setRows)
      .catch(() => setError("Could not load stock. Is the API up?"));
  }, []);

  async function toggleGrade(gradeId: string) {
    setOpenLot(null);
    setMovements(null);
    if (openGrade === gradeId) {
      setOpenGrade(null);
      return;
    }
    try {
      setLots(await listEggLots({ gradeId }));
      setOpenGrade(gradeId);
    } catch {
      setError("Could not load the grade's lots.");
    }
  }

  async function toggleLot(lotId: string) {
    if (openLot === lotId) {
      setOpenLot(null);
      setMovements(null);
      return;
    }
    try {
      setMovements(await listEggLotMovements(lotId));
      setOpenLot(lotId);
    } catch {
      setError("Could not load the lot's movements.");
    }
  }

  if (error && rows === null) {
    return <section><h2>Stock</h2><p className="error">{error}</p></section>;
  }
  if (rows === null) return <section><h2>Stock</h2><p className="muted">Loading…</p></section>;

  const totalAvailable = rows.reduce((a, r) => a + r.available, 0);

  return (
    <section>
      <h2>Stock</h2>
      {error && <p className="error" role="alert">{error}</p>}
      {rows.length === 0 ? (
        <p className="muted">No stock yet — record and submit a daily entry.</p>
      ) : (
        <>
          <table className="data">
            <thead>
              <tr><th>Grade</th><th>Available</th><th>Restricted</th><th></th></tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.eggGradeId}>
                  <td>{r.gradeName}</td>
                  <td>{r.available}</td>
                  <td>{r.restricted > 0 ? <span className="warn">{r.restricted}</span> : "—"}</td>
                  <td>
                    <button className="link" onClick={() => void toggleGrade(r.eggGradeId)}>
                      {openGrade === r.eggGradeId ? "hide lots" : "lots"}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="muted">{totalAvailable} eggs available across {rows.length} grade(s).
            Restricted = under medication withdrawal, blocked from sale.</p>

          {openGrade !== null && (
            <>
              <h3>Lots</h3>
              {lots.length === 0 ? (
                <p className="muted">No lots for this grade yet.</p>
              ) : (
                <table className="data">
                  <thead>
                    <tr><th>Produced on</th><th>Produced</th><th>Available</th><th></th></tr>
                  </thead>
                  <tbody>
                    {lots.map((l) => (
                      <tr key={l.id}>
                        <td>{l.productionDate}</td>
                        <td>{l.quantityProduced}</td>
                        <td>{l.quantityAvailable}</td>
                        <td>
                          <button className="link" onClick={() => void toggleLot(l.id)}>
                            {openLot === l.id ? "hide history" : "history"}
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}

              {openLot !== null && movements !== null && (
                <>
                  <h4>Movement ledger</h4>
                  <p className="muted">
                    Every change to this lot&apos;s available eggs — the running
                    sum always equals the balance above.
                  </p>
                  <table className="data">
                    <thead>
                      <tr><th>When (UTC)</th><th>Type</th><th>Change</th><th>Reason</th></tr>
                    </thead>
                    <tbody>
                      {movements.map((m) => (
                        <tr key={m.id}>
                          <td>{m.createdAtUtc.replace("T", " ").slice(0, 19)}</td>
                          <td>{m.movementType}</td>
                          <td>{m.quantityDelta > 0 ? `+${m.quantityDelta}` : m.quantityDelta}</td>
                          <td>{m.reason ?? "—"}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </>
              )}
            </>
          )}
        </>
      )}
    </section>
  );
}
