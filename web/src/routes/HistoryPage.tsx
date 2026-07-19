import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import {
  adjustDailyEntry, listDailyEntries, listEggGrades, listFlocks, voidDailyEntry,
} from "../api/cluckwork";
import type { DailyEntry, EggGrade, Flock } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useAuth } from "../auth/useAuth";

const PAGE = 50;

function errText(err: unknown): string {
  // Concurrent-correction conflicts get a human message instead of raw problem text.
  if (err instanceof ApiError && err.status === 409)
    return "This entry was just changed elsewhere — the list has been reloaded; retry.";
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// #24 (entries half): browse recorded daily entries, newest first, with
// flock + date-range filters and offset paging. #69 (part 2): admins can
// adjust or void submitted/locked entries from here — the API reconciles
// stock and the bird ledger and enforces the role either way.
export function HistoryPage() {
  const { isAdmin } = useAuth();
  const [entries, setEntries] = useState<DailyEntry[] | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [flocks, setFlocks] = useState<Flock[]>([]);
  const [grades, setGrades] = useState<EggGrade[]>([]);
  const [flockFilter, setFlockFilter] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // adjust panel: one entry at a time; the version it was loaded with rides
  // along so a concurrent correction surfaces as a 409, not an overwrite.
  const [adjusting, setAdjusting] = useState<DailyEntry | null>(null);
  const [total, setTotal] = useState(0);
  const [cracked, setCracked] = useState(0);
  const [dirty, setDirty] = useState(0);
  const [discarded, setDiscarded] = useState(0);
  const [mortality, setMortality] = useState(0);
  const [reason, setReason] = useState("");
  const [lineQty, setLineQty] = useState<Record<string, number>>({});

  // Stable idempotency keys per logical mutation, rotated only after the full
  // action (write + refresh) succeeds — same contract as the other screens.
  const keys = useRef(new Map<string, string>());
  const keyFor = (scope: string) => {
    const existing = keys.current.get(scope);
    if (existing) return existing;
    const fresh = crypto.randomUUID();
    keys.current.set(scope, fresh);
    return fresh;
  };
  const clearKey = (scope: string) => keys.current.delete(scope);

  useEffect(() => {
    // includeInactive/includeArchived: historical entries may reference
    // deactivated grades or archived flocks and their names must still resolve.
    Promise.all([listFlocks({ includeArchived: true }), listEggGrades({ includeInactive: true })])
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
  const correctable = (e: DailyEntry) =>
    e.status === "Submitted" || e.status === "Locked" || e.status === "ManagerAdjusted";

  function startAdjust(e: DailyEntry) {
    setAdjusting(e);
    setTotal(e.totalEggs);
    setCracked(e.crackedEggs);
    setDirty(e.dirtyEggs);
    setDiscarded(e.discardedEggs);
    setMortality(e.mortalityCount);
    setReason("");
    setLineQty(Object.fromEntries(e.grades.map((g) => [g.eggGradeId, g.quantity])));
  }

  // The entry's own lines (possibly deactivated grades — still correctable)
  // plus the active saleable catalog for adding a missed grade.
  function panelGrades(e: DailyEntry): EggGrade[] {
    const onEntry = new Set(e.grades.map((g) => g.eggGradeId));
    return grades.filter((g) => onEntry.has(g.id) || (g.active && g.isSaleable));
  }

  async function refreshAfter(scope: string) {
    await load();
    clearKey(scope);
  }

  async function onAdjustSubmit(ev: FormEvent) {
    ev.preventDefault();
    if (!adjusting || busy) return;
    setBusy(true);
    setError(null);
    setMessage(null);
    const scope = `adjust:${adjusting.id}`;
    try {
      const lines = Object.entries(lineQty)
        .filter(([, q]) => q > 0)
        .map(([eggGradeId, quantity]) => ({ eggGradeId, quantity }));
      await adjustDailyEntry(adjusting.id, {
        version: adjusting.version,
        totalEggs: total,
        crackedEggs: cracked,
        dirtyEggs: dirty,
        discardedEggs: discarded,
        mortalityCount: mortality,
        reason: reason.trim(),
        grades: lines, // [] explicitly clears all lines
      }, keyFor(scope));
      await refreshAfter(scope);
      setMessage("Entry adjusted — stock and bird ledger updated to match.");
      setAdjusting(null);
    } catch (err) {
      setError(errText(err));
      if (err instanceof ApiError && err.status === 409) {
        await load().catch(() => undefined);
        setAdjusting(null);
      }
    } finally {
      setBusy(false);
    }
  }

  function onVoid(e: DailyEntry) {
    // F13-style: the reason prompt doubles as the confirmation.
    const voidReason = window.prompt(
      `Void the ${e.date} entry for ${flockName(e.flockId)}? Its egg lots empty and its `
      + "deaths are reversed; the entry is kept as Voided. Refused if any of its "
      + "eggs were already sold.\n\nReason (required):");
    if (voidReason === null) return;
    if (!voidReason.trim()) {
      setError("A void reason is required.");
      return;
    }
    void (async () => {
      if (busy) return;
      setBusy(true);
      setError(null);
      setMessage(null);
      const scope = `void:${e.id}`;
      try {
        await voidDailyEntry(e.id, { version: e.version, reason: voidReason.trim() }, keyFor(scope));
        await refreshAfter(scope);
        setMessage("Entry voided — stock returned and deaths reversed.");
      } catch (err) {
        setError(errText(err));
        if (err instanceof ApiError && err.status === 409) await load().catch(() => undefined);
      } finally {
        setBusy(false);
      }
    })();
  }

  function statusCell(e: DailyEntry) {
    if (e.status === "Voided")
      return <span className="warn" title={e.voidReason ?? undefined}>Voided</span>;
    if (e.status === "ManagerAdjusted")
      return <span title={e.adjustReason ?? undefined}>Adjusted</span>;
    if (e.status === "Locked")
      return <span className="muted" title={e.lockedAtUtc ? `Locked ${e.lockedAtUtc}` : undefined}>Locked</span>;
    return <>{e.status}</>;
  }

  if (error && entries === null) return <section><h2>History</h2><p className="error">{error}</p></section>;

  return (
    <section>
      <h2>Daily entry history</h2>
      {isAdmin && (
        <p className="muted">
          Submitted and locked entries can be adjusted or voided here — stock
          and the bird ledger follow automatically; eggs already sold never
          move. A reason is always required.
        </p>
      )}

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

      {adjusting && (
        <div className="order-panel">
          <h3>Adjust — {adjusting.date}, {flockName(adjusting.flockId)}</h3>
          {adjusting.adjustedFrom && (
            <p className="muted">
              Previously adjusted (was total {adjusting.adjustedFrom.totalEggs},
              mortality {adjusting.adjustedFrom.mortalityCount} — "{adjusting.adjustReason}").
            </p>
          )}
          <form className="form-grid" onSubmit={onAdjustSubmit}>
            <label>Total eggs
              <input type="number" min={0} value={total} required
                onChange={(e) => setTotal(Math.max(0, e.target.valueAsNumber || 0))} />
            </label>
            <label>Cracked
              <input type="number" min={0} value={cracked} required
                onChange={(e) => setCracked(Math.max(0, e.target.valueAsNumber || 0))} />
            </label>
            <label>Dirty
              <input type="number" min={0} value={dirty} required
                onChange={(e) => setDirty(Math.max(0, e.target.valueAsNumber || 0))} />
            </label>
            <label>Discarded
              <input type="number" min={0} value={discarded} required
                onChange={(e) => setDiscarded(Math.max(0, e.target.valueAsNumber || 0))} />
            </label>
            <label>Deaths
              <input type="number" min={0} value={mortality} required
                onChange={(e) => setMortality(Math.max(0, e.target.valueAsNumber || 0))} />
            </label>
            {panelGrades(adjusting).map((g) => (
              <label key={g.id}>{g.name}{g.active ? "" : " (inactive)"}
                <input type="number" min={0} value={lineQty[g.id] ?? 0}
                  onChange={(e) => setLineQty((prev) => ({
                    ...prev, [g.id]: Math.max(0, e.target.valueAsNumber || 0),
                  }))} />
              </label>
            ))}
            <label>Reason *
              <input value={reason} maxLength={500} required
                onChange={(e) => setReason(e.target.value)} />
            </label>
            <button type="submit" disabled={busy || !reason.trim()}>Save adjustment</button>
            <button type="button" className="link" onClick={() => setAdjusting(null)}>cancel</button>
          </form>
        </div>
      )}

      {error && <p className="error">{error}</p>}
      {message && <p className="success">{message}</p>}

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
                {isAdmin && <th></th>}
              </tr>
            </thead>
            <tbody>
              {entries.map((e) => (
                <tr key={e.id} className={e.status === "Voided" ? "inactive" : undefined}>
                  <td>{e.date}</td>
                  <td>{flockName(e.flockId)}</td>
                  <td>{statusCell(e)}</td>
                  <td>{e.totalEggs}</td>
                  <td>{e.crackedEggs}/{e.dirtyEggs}/{e.discardedEggs}</td>
                  <td>{e.mortalityCount}</td>
                  <td>
                    {e.grades.length === 0
                      ? "—"
                      : e.grades.map((g) => `${gradeName(g.eggGradeId)} ${g.quantity}`).join(", ")}
                  </td>
                  {isAdmin && (
                    <td>
                      {correctable(e) && (
                        <>
                          <button className="link" disabled={busy}
                            onClick={() => startAdjust(e)}>adjust</button>
                          <button className="link" disabled={busy}
                            onClick={() => onVoid(e)}>void</button>
                        </>
                      )}
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
          {hasMore && (
            <button className="link" disabled={busy}
              onClick={() => void load(entries.length).catch(() => setError("Could not load more."))}>
              load more
            </button>
          )}
        </>
      )}
    </section>
  );
}
