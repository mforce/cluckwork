import { useCallback, useState } from "react";
import { useTranslation } from "react-i18next";
import { listAuditEvents } from "../api/cluckwork";
import { usePagedList } from "../components/usePagedList";
import { AUDIT_ACTION_VALUES, auditActionLabel, entityTypeLabel } from "../i18n/enums";

const PAGE = 100;

// #93 — read-only audit trail (admin). Deliberately no mutation surface: the
// rows are written by the server inside the transactions they record.
export function AuditPage() {
  const { t } = useTranslation("audit");
  const { t: tc } = useTranslation("common");

  const [actionFilter, setActionFilter] = useState("");

  // #469 — the ticket/dedupe/busy-ownership discipline this screen grew for
  // itself (codex review of #94) now lives in usePagedList, shared with every
  // other paged screen. The filter is expressed as the fetcher's identity, so
  // "the filter changed" and "reload from the top" cannot drift apart.
  const events = usePagedList({
    fetchPage: useCallback(
      (offset: number, limit: number) =>
        listAuditEvents({ action: actionFilter || undefined, limit, offset }),
      [actionFilter],
    ),
    pageSize: PAGE,
  });

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

      {events.error && <p className="error" role="alert">{events.error}</p>}

      {/* Blanked while a filter reload is in flight, not just on first load:
          the previous filter's rows under the new filter's control are
          mislabeled even for the second the request takes (#94). Only this
          table is gated, so the filter itself stays usable throughout. */}
      {events.rows === null || events.reloading ? (
        <p className="muted">{tc("loading")}</p>
      ) : events.rows.length === 0 ? (
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
              {events.rows.map((e) => (
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
          {events.canLoadMore && (
            <button className="link" onClick={() => void events.loadMore()}>
              {t("loadMoreButton")}
            </button>
          )}
        </>
      )}
    </section>
  );
}
