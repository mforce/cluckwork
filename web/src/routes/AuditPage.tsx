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

// #493 — pure, extracted deliberately (review round 1): usePagedList's
// identity-change reload is asynchronous — `fetchPage`'s identity (and so
// `entityId`) already reflects the NEW scope on the render where a
// navigation lands, but `reloading` only flips true inside a useEffect that
// runs AFTER that render commits. Trusting `reloading` alone missed that one
// render: `rows` can still hold the PREVIOUS entity's data while `entityId`
// already names the new one. Fixed for the case that matters — rows is
// non-empty and belongs to the wrong entity, so the heading/table would
// otherwise show genuinely wrong data for as long as the new fetch takes.
//
// Known, accepted gap (review round 2): if the STALE rows are `[]` (the old
// scope's own empty result), there's no row left to compare against, so
// this returns "not stale" for that one render — a scoped empty-message can
// flash under a blank heading before the new fetch lands. Narrower and far
// less severe than the bug above: it self-corrects on the very next render
// once `reloading` flips true, rather than persisting for the fetch's full
// duration. A fully general fix needs a ref tracking whether a fetch cycle
// has been OBSERVED for the current entityId (independent of row content),
// which would cost this function its purity — not taken for a single-frame
// residual.
//
// A component-level RTL test with a synchronously-resolving mock can't
// observe the fixed race at all — fireEvent's own act() flushes the passive
// effect that sets `reloading` before any assertion runs, so `reloading`
// and the new rows land in the same flush the test sees. (A test built with
// fake timers or a genuinely delayed mock could observe it through the
// component; this is why the test suite proves the fix via direct unit
// tests of this function instead, not a claim that the race is impossible
// to reproduce through the DOM by any means.)
export function isScopedDataStale(
  entityId: string | undefined,
  reloading: boolean,
  rows: { entityId: string }[] | null,
): boolean {
  if (reloading) return true;
  if (entityId === undefined) return false;
  if (rows === null || rows.length === 0) return false;
  return rows[0].entityId !== entityId;
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
  // Lowercased (review round: codex found the previous fix regressed on an
  // uppercase entityId): the API returns EntityId as a .NET Guid, which
  // System.Text.Json serializes lowercase regardless of the request's
  // casing, but the regex accepting it is case-insensitive. Without
  // normalizing here, isScopedDataStale's row comparison would never match
  // an uppercase URL against the lowercase rows the server returns — stuck
  // on "Loading…" forever, not just briefly stale. Normalized once here so
  // every downstream use (the API call, the comparison) agrees.
  const entityId = rawEntityId && isLikelyGuid(rawEntityId) ? rawEntityId.toLowerCase() : undefined;

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

  // #493 — see isScopedDataStale's own comment (codex review of #516): this
  // catches the render where entityId already names a new scope but
  // usePagedList's rows/reloading haven't caught up yet, which the
  // heading/table below both depend on.
  const isScopedReloading = isScopedDataStale(entityId, events.reloading, events.rows);

  const scopedEntityType = entityId && !isScopedReloading
    ? events.rows?.[0]?.entityType
    : undefined;

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
          table is gated, so the filter itself stays usable throughout.
          isScopedReloading, not events.reloading, so the stale-scope window
          above is caught here too — otherwise this branch would render the
          PREVIOUS entity's rows for one render (codex review of #516). */}
      {events.rows === null || isScopedReloading ? (
        <p className="muted">{tc("loading")}</p>
      ) : events.rows.length === 0 ? (
        // #493, Slice 2 — a scoped view with no events reads as "the whole
        // log is empty" under the generic message, which is wrong: it's this
        // record's history that's clean, not the audit trail overall.
        <p className="muted">{entityId ? t("scopedEmptyMessage") : t("emptyMessage")}</p>
      ) : (
        <>
          <table className="data">
            <thead>
              <tr>
                <th>{t("whenHeader")}</th><th>{t("whoHeader")}</th><th>{t("actionHeader")}</th>
                {/* #493, Slice 2 — every row in a scoped view shares the same
                    entity; repeating it up to 100 times is noise, not a
                    neutral no-op, so it's hidden rather than left in. */}
                {!entityId && <th>{t("entityHeader")}</th>}
                <th>{t("reasonHeader")}</th>
              </tr>
            </thead>
            <tbody>
              {events.rows.map((e) => (
                <tr key={e.id} title={e.detailsJson ?? undefined}>
                  <td>{e.occurredAtUtc.replace("T", " ").slice(0, 19)}</td>
                  <td>{e.actorEmail}</td>
                  <td>{auditActionLabel(e.action)}</td>
                  {!entityId && <td>{entityTypeLabel(e.entityType)} {e.entityId.slice(0, 8)}</td>}
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
