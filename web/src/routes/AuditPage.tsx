import { useCallback, useEffect, useState } from "react";
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

  const load = useCallback(async (offset = 0) => {
    setBusy(true);
    try {
      const page = await listAuditEvents({
        action: actionFilter || undefined, limit: PAGE, offset,
      });
      setEvents((prev) => (offset === 0 || prev === null) ? page : [...prev, ...page]);
      setHasMore(page.length === PAGE);
      setError(null);
    } catch (err) {
      setError(errText(err));
    } finally {
      setBusy(false);
    }
  }, [actionFilter]);

  useEffect(() => { void load(); }, [load]);

  const actions = [
    "DailyEntry.Adjust", "DailyEntry.Void", "SalesOrder.Void", "Payment.Void",
    "Expense.Adjust", "ExpenseCategory.Update", "InventoryItem.Adjust",
    "WaterUsage.Correct", "Flock.BirdMovement", "Flock.Update", "Flock.Deplete",
    "Flock.Archive", "Flock.Reactivate", "EggGrade.Update", "EggGrade.Activate",
    "EggGrade.Deactivate", "User.Create",
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
