import { useCallback, useEffect, useState } from "react";
import { listDailyEntries, listEggGrades, listFlocks } from "../api/cluckwork";
import type { DailyEntry, EggGrade, Flock } from "../api/cluckwork";

const PAGE = 50;

// #24 (entries half): browse recorded daily entries, newest first, with
// flock + date-range filters and offset paging.
export function HistoryPage() {
  const [entries, setEntries] = useState<DailyEntry[] | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [flocks, setFlocks] = useState<Flock[]>([]);
  const [grades, setGrades] = useState<EggGrade[]>([]);
  const [flockFilter, setFlockFilter] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    // includeInactive: historical entries may reference deactivated grades and
    // their names must still resolve.
    Promise.all([listFlocks(), listEggGrades({ includeInactive: true })])
      .then(([f, g]) => { setFlocks(f); setGrades(g); })
      .catch(() => setError("Could not load flocks/grades."));
  }, []);

  const load = useCallback(async (offset = 0) => {
    const page = await listDailyEntries({
      flockId: flockFilter || undefined,
      from: from || undefined,
      to: to || undefined,
      limit: PAGE,
      offset,
    });
    setHasMore(page.length === PAGE);
    setEntries((prev) => (offset === 0 ? page : [...(prev ?? []), ...page]));
  }, [flockFilter, from, to]);

  useEffect(() => {
    load().catch(() => setError("Could not load entries."));
  }, [load]);

  const flockName = (id: string) => flocks.find((f) => f.id === id)?.name ?? id.slice(0, 8);
  const gradeName = (id: string) => grades.find((g) => g.id === id)?.name ?? id.slice(0, 8);

  if (error) return <section><h2>History</h2><p className="error">{error}</p></section>;

  return (
    <section>
      <h2>Daily entry history</h2>

      <div className="form-grid">
        <label>Flock
          <select value={flockFilter} onChange={(e) => setFlockFilter(e.target.value)}>
            <option value="">All flocks</option>
            {flocks.map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}
          </select>
        </label>
        <label>From
          <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label>To
          <input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </label>
      </div>

      {entries === null ? (
        <p className="muted">Loading…</p>
      ) : entries.length === 0 ? (
        <p className="muted">No entries match — record one on the Daily entry page.</p>
      ) : (
        <>
          <table className="data">
            <thead>
              <tr>
                <th>Date</th><th>Flock</th><th>Status</th><th>Total</th>
                <th>Losses (cr/di/ds)</th><th>Mortality</th><th>Graded</th>
              </tr>
            </thead>
            <tbody>
              {entries.map((e) => (
                <tr key={e.id}>
                  <td>{e.date}</td>
                  <td>{flockName(e.flockId)}</td>
                  <td>{e.status}</td>
                  <td>{e.totalEggs}</td>
                  <td>{e.crackedEggs}/{e.dirtyEggs}/{e.discardedEggs}</td>
                  <td>{e.mortalityCount}</td>
                  <td>
                    {e.grades.length === 0
                      ? "—"
                      : e.grades.map((g) => `${gradeName(g.eggGradeId)} ${g.quantity}`).join(", ")}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {hasMore && (
            <button className="link"
              onClick={() => void load(entries.length).catch(() => setError("Could not load more."))}>
              load more
            </button>
          )}
        </>
      )}
    </section>
  );
}
