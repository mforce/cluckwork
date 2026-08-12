import { useCallback } from "react";
import { useTranslation } from "react-i18next";
import { useSearchParams } from "react-router";
import { listAuditEvents } from "../api/cluckwork";
import { usePagedList } from "../components/usePagedList";
import { AUDIT_ACTION_VALUES, auditActionLabel, entityTypeLabel } from "../i18n/enums";

const PAGE = 100;

// Canonical 8-4-4-4-12 hex form only, not full Guid.TryParse permissiveness
// (which also accepts braced/no-hyphen forms). This is a correctness guard,
// not just hygiene: the endpoint's model binder is stricter than TryParse,
// so a looser check would let some malformed values through to a guaranteed
// 400. Every server-issued link (row.id) is always canonical; this only
// guards a hand-edited or pasted URL — and, as a known trade-off, also
// rejects a hand-typed but valid noncanonical GUID, silently falling back to
// the unscoped view rather than engineering fuller parsing for that
// low-probability case (#493).
const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function isLikelyGuid(value: string): boolean {
  return GUID_PATTERN.test(value);
}

// #93 — read-only audit trail (admin). Deliberately no mutation surface: the
// rows are written by the server inside the transactions they record.
//
// #493 — also reachable entity-scoped, via ?entityId=<guid> (a "View
// history" link on a record's own screen). The URL is the single source of
// truth for BOTH `action` and `entityId`: react-router's setSearchParams
// REPLACES the whole query string rather than merging, so every write here
// goes through updateActionFilter, which builds a full copy from the
// CURRENT params rather than a partial object.
export function AuditPage() {
  const { t } = useTranslation("audit");
  const { t: tc } = useTranslation("common");

  const [searchParams, setSearchParams] = useSearchParams();
  const actionFilter = searchParams.get("action") ?? "";
  const rawEntityId = searchParams.get("entityId");
  const entityId = rawEntityId && isLikelyGuid(rawEntityId) ? rawEntityId : undefined;

  const updateActionFilter = useCallback((action: string) => {
    const next = new URLSearchParams(searchParams);
    if (action) next.set("action", action);
    else next.delete("action");
    setSearchParams(next);
  }, [searchParams, setSearchParams]);

  // #469 — the ticket/dedupe/busy-ownership discipline this screen grew for
  // itself (codex review of #94) now lives in usePagedList, shared with every
  // other paged screen. The filter is expressed as the fetcher's identity, so
  // "the filter changed" and "reload from the top" cannot drift apart —
  // `entityId` changing (a fresh entity-scoped link, or leaving the scope)
  // triggers the same reload as an action-filter change.
  const events = usePagedList({
    fetchPage: useCallback(
      (offset: number, limit: number) =>
        listAuditEvents({ action: actionFilter || undefined, entityId, limit, offset }),
      [actionFilter, entityId],
    ),
    pageSize: PAGE,
  });

  // Slice 1 (naive): does not yet gate on events.reloading, so a filter
  // change or entity switch can transiently show the PREVIOUS entity's type
  // while the new page loads. Hardened in Slice 2 (#493).
  const scopedEntityType = entityId ? events.rows?.[0]?.entityType : undefined;

  return (
    <section>
      <h2>
        {entityId
          ? (scopedEntityType
              ? t("scopedHeading", { entityType: entityTypeLabel(scopedEntityType) })
              : t("scopedHeadingFallback"))
          : t("heading")}
      </h2>
      <p className="muted">{t("intro")}</p>

      <div className="filters">
        <label>{t("actionFilterLabel")}
          <select value={actionFilter} onChange={(e) => updateActionFilter(e.target.value)}>
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
