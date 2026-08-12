import { useCallback, useRef } from "react";
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

// #493 — usePagedList's identity-change reload is asynchronous:
// `fetchPage`'s identity already reflects a NEW query on the render where a
// navigation lands (a different entityId, a different action, or leaving a
// scope entirely), but `reloading` only flips true inside a useEffect that
// runs AFTER that render commits. Two earlier versions of this fix compared
// the loaded ROWS' own content against the current entityId — that caught
// the common case (rows belong to the wrong entity) but review kept finding
// variants it missed: an empty stale page (no row left to compare), and
// leaving a scope entirely (entityId -> undefined, which a content check
// exits early on by design, so old scoped rows could render as if they were
// the global log). Two misses of the same shape means the METHOD was
// wrong, not just missing a case — comparing `fetchPage`'s own reference
// instead of inferring staleness from content closes every variant
// uniformly: the moment ANYTHING the query depends on changes, rows are
// stale until a completed reload confirms otherwise, full stop, regardless
// of what's in them.
export function isFetchStale(committedFetchPage: unknown, currentFetchPage: unknown): boolean {
  return committedFetchPage !== currentFetchPage;
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
  // Lowercased (codex review of #516): the API returns EntityId as a .NET
  // Guid, which System.Text.Json serializes lowercase regardless of the
  // request's casing, but the regex accepting it is case-insensitive.
  // Without normalizing here, an uppercase URL value would build a
  // DIFFERENT fetchPage identity than the same record's lowercase form
  // does elsewhere, which is exactly what isFetchStale below treats as "a
  // different query" — normalized once here so it doesn't fight its own
  // staleness check.
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
  // triggers the same reload as an action-filter change. Hoisted out of the
  // usePagedList call (#493) so its own identity is available below for the
  // stale-scope check, not just handed straight in.
  const fetchPage = useCallback(
    (offset: number, limit: number) =>
      listAuditEvents({ action: actionFilter || undefined, entityId, limit, offset }),
    [actionFilter, entityId],
  );
  const events = usePagedList({ fetchPage, pageSize: PAGE });

  // #493 — see isFetchStale's own comment (codex review of #516): rows are
  // stale (heading/table below both depend on this) whenever `fetchPage`'s
  // identity has moved on since the last completed reload, regardless of
  // what's currently in `events.rows`.
  //
  // Updating the ref here, in the render body, gated on `!events.reloading`
  // — rather than in an effect after a reload completes — is safe SPECIFICALLY
  // because of usePagedList's own ticket system (a later review round asked
  // about a rapid double-switch, e.g. click record B then click record C
  // before B's fetch resolves; verified against usePagedList.ts directly,
  // not just reasoned about): `reload()`/`loadMore()` claim `req.current`
  // synchronously, before any await, and `setLoadingOwned` — the only path
  // that ever flips `reloading` — no-ops entirely for a call whose `seq` no
  // longer matches `req.current`. So a superseded fetch (B, once C's
  // navigation has claimed a new ticket) can NEVER reset `reloading` on C's
  // behalf, successful or not — `events.reloading` transitioning false only
  // ever reflects the CURRENTLY active ticket's own completion. That's what
  // makes "not reloading" a trustworthy signal to commit the ref to
  // whatever `fetchPage` identity is current at that moment, even though
  // the update itself runs before this specific render's own fetch (if any)
  // has been issued.
  const committedFetchPageRef = useRef(fetchPage);
  const isScopedReloading = events.reloading || isFetchStale(committedFetchPageRef.current, fetchPage);
  if (!events.reloading) {
    committedFetchPageRef.current = fetchPage;
  }

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
