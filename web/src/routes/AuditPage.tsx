import { useCallback, useEffect, useRef, useState } from "react";
import { listAuditEvents } from "../api/cluckwork";
import type { AuditEvent } from "../api/cluckwork";
import { ApiError } from "../api/client";

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

const PAGE = 100;

// #93 — read-only audit trail (admin). Deliberately no mutation surface: the
// rows are written by the server inside the transactions they record.
export function AuditPage() {
  const [events, setEvents] = useState<AuditEvent[] | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [actionFilter, setActionFilter] = useState("");
  const [busy, setBusy] = useState(false);
  // Stale responses for a previous filter must neither replace nor append to
  // the current view, nor flip its busy state (codex review of #94).
  const requestSeq = useRef(0);

  const load = useCallback(async (offset = 0) => {
    const seq = ++requestSeq.current;
    setBusy(true);
    if (offset === 0) setEvents(null); // fresh filter view; no mislabeled rows
    try {
      const page = await listAuditEvents({
        action: actionFilter || undefined, limit: PAGE, offset,
      });
      if (seq !== requestSeq.current) return;
      // Dedupe by id on append: the log is append-only and newest-first, so a
      // row inserted between pages can only push rows DEEPER — the next page
      // may re-show the tail (duplicates), never skip (codex review of #94;
      // its "skipped rows" half needs deletions, which never happen here).
      setEvents((prev) => (offset === 0 || prev === null)
        ? page
        : [...prev, ...page.filter((p) => !prev.some((x) => x.id === p.id))]);
      setHasMore(page.length === PAGE);
      setError(null);
    } catch (err) {
      if (seq !== requestSeq.current) return;
      setError(errText(err));
      // End the "Loading…" state so the error is the only thing shown.
      setEvents((prev) => prev ?? []);
    } finally {
      if (seq === requestSeq.current) setBusy(false);
    }
  }, [actionFilter]);

  useEffect(() => { void load(); }, [load]);

  // Must list every server-side capture-point action code — a missing entry
  // is a silent filter gap ("All actions" still shows the rows). Centralizing
  // these codes is part of the magic-strings debt (#84).
  const actions = [
    "DailyEntry.Adjust", "DailyEntry.Void", "SalesOrder.Void", "Payment.Void",
    "Expense.Adjust", "ExpenseCategory.Update", "InventoryItem.Adjust",
    "WaterUsage.Correct", "Flock.BirdMovement", "Flock.Update", "Flock.Deplete",
    "Flock.Archive", "Flock.Reactivate", "EggGrade.Update", "EggGrade.Activate",
    "EggGrade.Deactivate", "User.Create", "User.Update", "User.PasswordSet",
    "User.PasswordChanged", "User.FlockAssign",
    "User.FlockUnassign", "Account.Export",
    "Product.Create", "Product.Update", "Product.Activate",
    "Product.Deactivate", "EggUnitConversion.Update",
  ];

  return (
    <section>
      <h2>Audit log</h2>
      <p className="muted">
        Every corrective, destructive, or configuration change — who did it,
        when, and why. Rows are written with the change itself and never edited.
      </p>

      <div className="filters">
        <label>Action
          <select value={actionFilter} onChange={(e) => setActionFilter(e.target.value)}>
            <option value="">All actions</option>
            {actions.map((a) => <option key={a} value={a}>{a}</option>)}
          </select>
        </label>
      </div>

      {error && <p className="error" role="alert">{error}</p>}

      {events === null ? (
        <p className="muted">Loading…</p>
      ) : events.length === 0 ? (
        <p className="muted">No audit events yet.</p>
      ) : (
        <>
          <table className="data">
            <thead>
              <tr><th>When (UTC)</th><th>Who</th><th>Action</th><th>Entity</th><th>Reason</th></tr>
            </thead>
            <tbody>
              {events.map((e) => (
                <tr key={e.id} title={e.detailsJson ?? undefined}>
                  <td>{e.occurredAtUtc.replace("T", " ").slice(0, 19)}</td>
                  <td>{e.actorEmail}</td>
                  <td>{e.action}</td>
                  <td>{e.entityType} {e.entityId.slice(0, 8)}</td>
                  <td>{e.reason ?? "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {hasMore && (
            <button className="link" disabled={busy}
              onClick={() => void load(events.length)}>
              load more
            </button>
          )}
        </>
      )}
    </section>
  );
}
