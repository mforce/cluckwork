import { useEffect, useState } from "react";
import { getStock } from "../api/cluckwork";
import type { StockRow } from "../api/cluckwork";

// F2 (#22): current sellable stock by grade; withdrawal-restricted quantities
// are shown separately — they exist but cannot be sold yet.
export function StockPage() {
  const [rows, setRows] = useState<StockRow[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    getStock()
      .then(setRows)
      .catch(() => setError("Could not load stock. Is the API up?"));
  }, []);

  if (error) return <section><h2>Stock</h2><p className="error">{error}</p></section>;
  if (rows === null) return <section><h2>Stock</h2><p className="muted">Loading…</p></section>;

  const totalAvailable = rows.reduce((a, r) => a + r.available, 0);

  return (
    <section>
      <h2>Stock</h2>
      {rows.length === 0 ? (
        <p className="muted">No stock yet — record and submit a daily entry.</p>
      ) : (
        <>
          <table className="data">
            <thead>
              <tr><th>Grade</th><th>Available</th><th>Restricted</th></tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.eggGradeId}>
                  <td>{r.gradeName}</td>
                  <td>{r.available}</td>
                  <td>{r.restricted > 0 ? <span className="warn">{r.restricted}</span> : "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="muted">{totalAvailable} eggs available across {rows.length} grade(s).
            Restricted = under medication withdrawal, blocked from sale.</p>
        </>
      )}
    </section>
  );
}
