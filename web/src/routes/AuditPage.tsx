import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { listAuditEvents } from "../api/cluckwork";
import type { AuditEvent } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { AUDIT_ACTION_VALUES, auditActionLabel, entityTypeLabel } from "../i18n/enums";

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

const PAGE = 100;

// #93 — read-only audit trail (admin). Deliberately no mutation surface: the
// rows are written by the server inside the transactions they record.
export function AuditPage() {
  const { t } = useTranslation("audit");
  const { t: tc } = useTranslation("common");

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

  return (
    <section>
      <h2>{t("heading")}</h2>
      <p className="muted">{t("intro")}</p>

      <div className="filters">
        <label>{t("actionFilterLabel")}
          <select value={actionFilter} onChange={(e) => setActionFilter(e.target.value)}>
            <option value="">{t("allActionsOption")}</option>
            {AUDIT_ACTION_VALUES.map((a) => (
              <option key={a} value={a}>{auditActionLabel(a)}</option>
            ))}
          </select>
        </label>
      </div>

      {error && <p className="error" role="alert">{error}</p>}

      {events === null ? (
        <p className="muted">{tc("loading")}</p>
      ) : events.length === 0 ? (
        <p className="muted">{t("emptyMessage")}</p>
      ) : (
        <>
          <table className="data">
            <thead>
              <tr>
                <th>{t("whenHeader")}</th><th>{t("whoHeader")}</th><th>{t("actionHeader")}</th>
                <th>{t("entityHeader")}</th><th>{t("reasonHeader")}</th>
              </tr>
            </thead>
            <tbody>
              {events.map((e) => (
                <tr key={e.id} title={e.detailsJson ?? undefined}>
                  <td>{e.occurredAtUtc.replace("T", " ").slice(0, 19)}</td>
                  <td>{e.actorEmail}</td>
                  <td>{auditActionLabel(e.action)}</td>
                  <td>{entityTypeLabel(e.entityType)} {e.entityId.slice(0, 8)}</td>
                  <td>{e.reason ?? "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {hasMore && (
            <button className="link" disabled={busy}
              onClick={() => void load(events.length)}>
              {t("loadMoreButton")}
            </button>
          )}
        </>
      )}
    </section>
  );
}
