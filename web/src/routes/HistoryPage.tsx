import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { Link } from "react-router";
import {
  adjustDailyEntry, getDailyEntry, listDailyEntries, listEggGrades, listFlocks, voidDailyEntry,
} from "../api/cluckwork";
import type { DailyEntry, EggGrade, Flock } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useAuth } from "../auth/useAuth";
import { Dialog } from "../components/Dialog";
import { useConfirm } from "../components/useConfirm";
import { StatusBadge } from "../components/StatusBadge";
import { newId } from "../lib/ids";

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
  const { askReason, confirmDialog } = useConfirm();
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

  // F131: the correction form is a dialog — it takes focus itself, so the
  // scroll-and-focus dance the old above-the-table panel needed is gone
  // (codex, PR #81).

  // Stable idempotency keys per logical mutation; see settleKey for the
  // rotation rules on this screen.
  const keys = useRef(new Map<string, string>());
  const keyFor = (scope: string) => {
    const existing = keys.current.get(scope);
    if (existing) return existing;
    const fresh = newId();
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
  // The Daily entry screen can't target archived flocks (capture excludes
  // them), so an edit link for one would silently fall back to a different
  // flock — worse than no link (codex review of #86).
  const flockEditable = (id: string) => {
    const f = flocks.find((x) => x.id === id);
    return f !== undefined && f.status !== "Archived";
  };
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

  // Key lifecycle differs from the create screens (codex review of PR #81):
  // a SERVER response — success or rejection — is a definite outcome, so the
  // key rotates immediately and an edited retry is a fresh request (the
  // version base already guards against double-apply). Only a transport
  // failure (no response) keeps the key for an exact replay.
  function settleKey(scope: string, err?: unknown) {
    if (err === undefined || err instanceof ApiError) clearKey(scope);
  }

  // On a 409 the correction lost a race. Reload, then re-bind the panel to
  // the fresh entry with the OTHER admin's values in every field — keeping
  // this admin's typed numbers could silently clobber a grade line the
  // winner just added (pi review of PR #81). Only the reason survives; if
  // the entry is no longer correctable (voided meanwhile), close the panel.
  async function rebindAfterConflict(entryId: string) {
    try {
      await load();
      const fresh = await getDailyEntry(entryId);
      if (correctable(fresh)) {
        const keptReason = reason;
        startAdjust(fresh);
        setReason(keptReason);
        setError("This entry was changed by someone else — the form shows the latest values; re-apply your correction.");
      } else {
        setAdjusting(null);
        setError(`This entry is now ${fresh.status.toLowerCase()} — nothing left to adjust.`);
      }
    } catch {
      setError("This entry was changed by someone else and the list could not be reloaded — reload the page before retrying.");
    }
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
      // Mirror the server's sellable-cap rule for an instant message; the
      // API remains the authority.
      const sellable = total - cracked - dirty - discarded;
      if (lines.reduce((sum, l) => sum + l.quantity, 0) > sellable) {
        setError("Graded quantities cannot exceed total eggs minus cracked/dirty/discarded.");
        setBusy(false);
        return;
      }
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
      settleKey(scope);
      setAdjusting(null);
      setMessage("Entry adjusted — stock and bird ledger updated to match.");
      await load().catch(() =>
        setError("The adjustment saved, but the list failed to reload — refresh the page."));
    } catch (err) {
      settleKey(scope, err);
      if (err instanceof ApiError && err.status === 409) {
        await rebindAfterConflict(adjusting.id);
      } else {
        setError(errText(err));
      }
    } finally {
      setBusy(false);
    }
  }

  async function onVoid(e: DailyEntry) {
    // F13-style: the reason ask doubles as the confirmation. F135: it is the
    // app's own dialog, so the required check is inline and the typed text
    // survives it — window.prompt validated only after it had closed.
    const voidReason = await askReason({
      title: `Void the ${e.date} entry for ${flockName(e.flockId)}?`,
      body: "Its egg lots empty and its deaths are reversed. The entry is kept as Voided. "
        + "Refused if any of its eggs were already sold.",
      confirmLabel: "Void entry",
      destructive: true,
    });
    if (voidReason === null) return;
    void (async () => {
      if (busy) return;
      setBusy(true);
      setError(null);
      setMessage(null);
      const scope = `void:${e.id}`;
      try {
        await voidDailyEntry(e.id, { version: e.version, reason: voidReason }, keyFor(scope));
        settleKey(scope);
        // A stale adjust panel for the now-voided entry would only 409.
        if (adjusting?.id === e.id) setAdjusting(null);
        setMessage("Entry voided — its egg lots were emptied and its deaths reversed.");
        await load().catch(() =>
          setError("The void saved, but the list failed to reload — refresh the page."));
      } catch (err) {
        settleKey(scope, err);
        if (err instanceof ApiError && err.status === 409) {
          setError("This entry was changed by someone else — the list has been reloaded; retry.");
          await load().catch(() => setError(
            "This entry was changed by someone else and the list could not be reloaded — reload the page."));
        } else {
          setError(errText(err));
        }
      } finally {
        setBusy(false);
      }
    })();
  }

  function statusCell(e: DailyEntry) {
    // Colored status pills (#52). The three states with tooltips keep an
    // explicit <span> so the title survives (StatusBadge takes no title);
    // plain states (Submitted → ok, Draft → neutral) go through StatusBadge.
    if (e.status === "Voided")
      return <span className="badge badge-danger" title={e.voidReason ?? undefined}>Voided</span>;
    if (e.status === "ManagerAdjusted")
      return <span className="badge badge-warn" title={e.adjustReason ?? undefined}>Adjusted</span>;
    if (e.status === "Locked")
      return <span className="badge badge-accent" title={e.lockedAtUtc ? `Locked ${e.lockedAtUtc}` : undefined}>Locked</span>;
    return <StatusBadge status={e.status} />;
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

      <Dialog
        open={adjusting !== null}
        title={adjusting ? `Adjust — ${adjusting.date}, ${flockName(adjusting.flockId)}` : "Adjust entry"}
        onClose={() => setAdjusting(null)}
        // A 409 swaps the server's newer entry into the open dialog; the record
        // identity changing pulls focus back to the first field, so the form is
        // not silently replaced under the user's cursor.
        focusKey={adjusting}
      >
        {adjusting && (
          <>
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
            {/* The 409 rebind reports here, beside the form it asks you to re-apply. */}
            {error && <p className="error" role="alert">{error}</p>}
            <div className="dialog-foot">
              <button type="button" className="link" onClick={() => setAdjusting(null)}>Cancel</button>
              <button type="submit" disabled={busy || !reason.trim()}>Save adjustment</button>
            </div>
            </form>
          </>
        )}
      </Dialog>

      {/* The dialog carries its own copy while it is up. */}
      {error && adjusting === null && <p className="error" role="alert">{error}</p>}
      {message && <p className="success" role="status">{message}</p>}

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
                <th></th>
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
                  <td>
                    {/* Drafts are edited on the Daily entry screen (#85) —
                        open to workers too; adjust/void stay admin-only. */}
                    {e.status === "Draft" && flockEditable(e.flockId) && (
                      <Link className="link"
                        to={`/daily-entry?flockId=${e.flockId}&date=${e.date}`}>
                        edit
                      </Link>
                    )}
                    {isAdmin && correctable(e) && (
                      <>
                        <button className="link" disabled={busy}
                          onClick={() => startAdjust(e)}>adjust</button>
                        <button className="link" disabled={busy}
                          onClick={() => void onVoid(e)}>void</button>
                      </>
                    )}
                  </td>
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

      {confirmDialog}
    </section>
  );
}
