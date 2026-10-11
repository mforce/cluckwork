import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useLocation, useSearchParams } from "react-router";
import {
  Checkbox, FormControlLabel, Typography,
} from "@mui/material";
import { listAuditEvents } from "../api/cluckwork";
import { currentUserId } from "../auth/claims";
import { newId } from "../lib/ids";
import { usePagedList } from "../components/usePagedList";
import { isIsoCalendarDate } from "../lib/dates";
import { AuditEventRow } from "./AuditEventRows";
import { AuditFilters } from "./AuditFilters";
import {
  AUDIT_ACTION_ENTITY_TYPE,
  AUDIT_ACTION_VALUES,
  ENTITY_TYPE_VALUES,
  entityTypeLabel,
  type EntityTypeValue,
} from "../i18n/enums";

const PAGE = 100;

// #800 — connected-app names already seen, keyed by the signed-in user and client id.
// AppLayout remounts the screen on every URL write, filters included, so component
// state would forget the name of a pivoted app whose filtered result turns out empty.
// Sign-out does not reload the page, so the user in the key keeps one session's names
// from another's. Names come from loaded rows and the pressed button, never the URL.
// ponytail: grows by one short entry per app seen; nothing evicts until a reload.
const knownAppNames = new Map<string, string | null>();
const appNameKey = (clientId: string) => `${currentUserId() ?? ""} ${clientId}`;

// Show only's focus request, as the nonce it put in navigation state. The remounted
// screen focuses only for the pending nonce and clears it, so Back, Forward or a
// reload that restores the entry's state does not move focus again.
let pendingPivotFocus: string | null = null;

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
// REPLACES the whole query string rather than merging, so every write here —
// `updateActionFilter`, `updateEntityTypeFilter` and `updateDateFilter` —
// builds a full copy from the CURRENT params rather than a partial object
// (INV-2).
export function AuditPage() {
  const { t } = useTranslation("audit");
  const { t: tc } = useTranslation("common");

  const [searchParams, setSearchParams] = useSearchParams();
  const rawActionFilter = searchParams.get("action") ?? "";
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

  // #666 — the date window. Read from the URL like every other filter on this
  // screen (INV-1), and validated the same fail-soft way `action` and
  // `entityType` are: a hand-edited or shared URL carrying a value the control
  // cannot display must not be sent to the server either, or the visible filter
  // and the query drift apart (the #521 finding, applied to a new param).
  //
  // Validated by isIsoCalendarDate (lib/dates.ts), whose boundary table is the
  // specification for what this accepts — this screen deliberately does not
  // define its own date validation, because doing so got it wrong three times.
  //
  // The API takes inclusive calendar days over the UTC timestamp
  // (AuditEventRepository.ListAsync), which is what this screen's own
  // "When (UTC)" column already shows. That is deliberately NOT the farm-local
  // business date Expenses filters on — same-looking control, different day
  // boundary, each matching what its own screen displays.
  const rawFrom = searchParams.get("from") ?? "";
  const rawTo = searchParams.get("to") ?? "";
  const fromFilter = isIsoCalendarDate(rawFrom) ? rawFrom : "";
  const toFilter = isIsoCalendarDate(rawTo) ? rawTo : "";

  // #800 — the connected-app filters. A row's "Show only" pivot names one app, which
  // implies the checkbox, so the box reads checked whenever either is in the URL.
  const appClientIdFilter = searchParams.get("connectedAppClientId") ?? "";
  const appsOnlyFilter = appClientIdFilter !== "" || searchParams.get("connectedAppsOnly") === "true";

  // Unchecking also drops the one-app pivot; otherwise the box would stay checked.
  const updateAppsOnlyFilter = useCallback((checked: boolean) => {
    const next = new URLSearchParams(searchParams);
    if (checked) next.set("connectedAppsOnly", "true");
    else {
      next.delete("connectedAppsOnly");
      next.delete("connectedAppClientId");
    }
    setSearchParams(next);
  }, [searchParams, setSearchParams]);

  // The pressed button leaves with the remount, so the new instance moves focus to
  // the caption, which reads out the app now shown.
  const showOnlyApp = useCallback((clientId: string, name: string | null) => {
    knownAppNames.set(appNameKey(clientId), name);
    const next = new URLSearchParams(searchParams);
    next.set("connectedAppClientId", clientId);
    pendingPivotFocus = newId();
    setSearchParams(next, { state: { focusAppPivot: pendingPivotFocus } });
  }, [searchParams, setSearchParams]);
  const pivotCaptionRef = useRef<HTMLParagraphElement>(null);
  const focusAppPivot = (useLocation().state as { focusAppPivot?: string } | null)?.focusAppPivot;
  useEffect(() => {
    if (!focusAppPivot || focusAppPivot !== pendingPivotFocus) return;
    pendingPivotFocus = null;
    pivotCaptionRef.current?.focus();
  }, [focusAppPivot]);

  const updateActionFilter = useCallback((action: string) => {
    const next = new URLSearchParams(searchParams);
    if (action) next.set("action", action);
    else next.delete("action");
    setSearchParams(next);
  }, [searchParams, setSearchParams]);

  // #666 — PROTECTED. One thing here is load-bearing and one thing is not,
  // and the difference was got wrong once already.
  //
  // LOAD-BEARING: `{ replace: true }`. An <input type="date"> emits onChange
  // per keystroke on some browsers — a year retype alone yields 0002-, 0020-,
  // 0202-, 2026- — so pushing would fill the history stack with values the
  // user never chose to keep and make Back walk them one by one. Pinned by
  // `does not leave an intermediate half-typed date on the history stack`,
  // which fails if this argument is removed.
  //
  // NOT a protection, despite an earlier version of this comment claiming it
  // was: the FUNCTIONAL updater form is not safer than copying `searchParams`
  // from the render closure the way updateActionFilter above does. Verified in
  // the installed router (react-router 8.3,
  // dist/development/lib/dom/lib.js:761): the callback is invoked as
  // `nextInit(new URLSearchParams(searchParams))`, where `searchParams` is the
  // render-closure memo of location.search — so both forms read the same
  // snapshot, and the router's own docs warn that "multiple calls to
  // setSearchParams in the same tick will not build on the prior value".
  //
  // What that means for anyone extending this screen: a handler that writes
  // BOTH fields in one tick — a "clear both dates" button, say — will lose one
  // of the two writes in EITHER form. Write both in a single call that builds
  // one URLSearchParams, or hold the pair in state. This is latent today only
  // because from and to are separate inputs, one event tick each.
  // INV-2 still holds either way: every write here builds a FULL copy of the
  // current params, never a partial object, because setSearchParams REPLACES
  // the whole query string.
  const updateDateFilter = useCallback((field: "from" | "to", value: string) => {
    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
      if (value) next.set(field, value);
      else next.delete(field);
      return next;
    }, { replace: true });
  }, [setSearchParams]);

  // Changing the record type drops any selected action: the old action can
  // fall outside the new type's option list and remain hidden but active.
  const rawEntityTypeFilter = searchParams.get("entityType");
  const entityTypeFilter: EntityTypeValue | "" =
    rawEntityTypeFilter && (ENTITY_TYPE_VALUES as readonly string[]).includes(rawEntityTypeFilter)
      ? (rawEntityTypeFilter as EntityTypeValue)
      : "";

  const updateEntityTypeFilter = useCallback((type: string) => {
    const next = new URLSearchParams(searchParams);
    if (type) next.set("entityType", type);
    else next.delete("entityType");
    next.delete("action");
    setSearchParams(next);
  }, [searchParams, setSearchParams]);

  // #679 — every filter control in one write, not one per control. The PROTECTED
  // comment above says exactly why: separate writes in one tick lose all but
  // one, in either setSearchParams form. `entityId` is deliberately NOT
  // cleared — it is the scope this view was opened in (a per-row "Audit
  // history" link), not a filter the user set here, and dropping it would
  // silently widen the page from one record to the whole farm.
  const clearFilters = useCallback(() => {
    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
      next.delete("entityType");
      next.delete("action");
      next.delete("from");
      next.delete("to");
      next.delete("connectedAppsOnly");
      next.delete("connectedAppClientId");
      return next;
    }, { replace: true });
  }, [setSearchParams]);

  const availableActions = entityTypeFilter
    ? AUDIT_ACTION_VALUES.filter((a) => AUDIT_ACTION_ENTITY_TYPE[a] === entityTypeFilter)
    : AUDIT_ACTION_VALUES;

  // codex review of #521 — a hand-edited or shared URL can carry a valid but
  // INCOMPATIBLE pair, e.g. ?entityType=Flock&action=User.Create: the raw
  // action isn't in `availableActions` for that type, so the <select> falls
  // back to showing no match while `fetchPage` would silently keep querying
  // the hidden action. Ignoring an out-of-scope action here, not just when
  // the user changes the type dropdown, keeps the visible filter and the
  // query in sync for BOTH entry paths (a click, and a direct URL load).
  const actionFilter =
    rawActionFilter && (availableActions as readonly string[]).includes(rawActionFilter)
      ? rawActionFilter
      : "";

  // The non-scope narrowings. `entityId` is the scope axis that can empty this
  // view and is deliberately NOT here — it selects which of the two sentence
  // families applies, in the ternary below, rather than whether the view is
  // narrowed at all.
  const isNarrowed = Boolean(entityTypeFilter || actionFilter || fromFilter || toFilter || appsOnlyFilter);

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
      listAuditEvents({
        action: actionFilter || undefined,
        entityType: entityTypeFilter || undefined,
        entityId,
        from: fromFilter || undefined,
        to: toFilter || undefined,
        connectedAppsOnly: appsOnlyFilter || undefined,
        connectedAppClientId: appClientIdFilter || undefined,
        limit,
        offset,
      }),
    // INV-3 — every value the request body uses is named here. The whole
    // stale-window discipline below (isFetchStale, committedFetchPage, the
    // blanked table) keys on this identity: a filter missing from these deps
    // renders the previous window's rows under the new window's controls.
    [actionFilter, entityTypeFilter, entityId, fromFilter, toFilter, appsOnlyFilter, appClientIdFilter],
  );
  const events = usePagedList({ fetchPage, pageSize: PAGE });

  // #493 — see isFetchStale's own comment (codex review of #516): rows are
  // stale (heading/table below both depend on this) whenever `fetchPage`'s
  // identity has moved on since the last completed reload, regardless of
  // what's currently in `events.rows`.
  //
  // STATE, not a ref (codex review — a ref-based version of this shipped
  // and broke on a delayed double-switch: click record B, then click record
  // C before B's fetch resolves). Trace: on the render where C's OWN reload
  // completes (`events.reloading` flips true -> false), that render still
  // needs to read the PRE-update committed value to correctly show
  // "reloading" one more time — but mutating a ref doesn't schedule a
  // re-render, so nothing ever re-evaluates with the corrected value
  // afterward. The page got stuck on "Loading…" forever, not just briefly
  // stale — worse than every version before it.
  //
  // Committing via `setCommittedFetchPage` inside an effect keyed ONLY on
  // `events.reloading` (not on `fetchPage`) fixes both halves at once: the
  // effect fires exactly once per genuine reloading TRANSITION (mount, and
  // every true<->false flip), reading that render's own fresh `fetchPage`
  // from its closure — never on a render where entityId/action changed but
  // reloading hasn't caught up yet, since the dependency didn't change on
  // that render. And because it's a state update, not a ref mutation, the
  // render that needed the corrected value gets scheduled — closing the
  // stuck-forever gap a ref architecturally cannot.
  //
  // Safe against the specific double-switch trace above (verified against
  // usePagedList.ts's ticket system, not just reasoned about): B's fetch,
  // once superseded by C's own reload claiming a new ticket, can never flip
  // `reloading` on C's behalf — `setLoadingOwned` no-ops for any call whose
  // ticket no longer matches `req.current`, and tickets are claimed
  // synchronously before any await. So the ONE reloading-transition my
  // effect observes for scope C is genuinely C's own completion, never B's.
  // useState(() => fetchPage) / setCommittedFetchPage(() => fetchPage), not
  // the bare function: React treats a function ARGUMENT to useState/a state
  // setter as a lazy initializer/updater, not a value to store — passing
  // fetchPage directly would have React CALL it rather than store its
  // reference (TypeScript caught this at the call sites before it shipped).
  const [committedFetchPage, setCommittedFetchPage] = useState(() => fetchPage);
  useEffect(() => {
    if (!events.reloading) {
      setCommittedFetchPage(() => fetchPage);
    }
    // Deliberately NOT depending on `fetchPage`: this must fire only on a
    // genuine reloading transition, not on every identity change (that's
    // exactly the premature-commit bug the ref-based version had).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [events.reloading]);
  const isScopedReloading = events.reloading || isFetchStale(committedFetchPage, fetchPage);
  // A later review round questioned whether this effect, once dormant while
  // reloading stays true across an intermediate scope change (click B, then
  // click C before B's fetch resolves — reloading is already true, so the
  // dependency doesn't change and the effect doesn't fire for C's click),
  // could commit a STALE closure once it finally does fire. It can't: React
  // never queues up an earlier render's discarded effect callback — the
  // callback that actually RUNS is always defined by the render where the
  // dependency change was observed, closing over THAT render's fetchPage,
  // which useCallback guarantees is the current identity (unchanged since
  // whatever scope is currently selected). Proven both by this reasoning
  // and empirically: the tests below use separate, individually-awaited
  // act() calls per click (not one batched flush) through a two-way and a
  // three-way switch, and pass — confirmed via mutation that they fail
  // (page stuck permanently) against the earlier ref-based version.

  const scopedEntityType = entityId && !isScopedReloading
    ? events.rows?.[0]?.entityType
    : undefined;

  const previewEntityId = isScopedReloading ? undefined : events.rows?.[0]?.entityId;

  // The pivoted app's name comes from a loaded row or knownAppNames, never the URL,
  // like scopedEntityType. A URL whose app this session has not seen gets a neutral
  // label.
  const pivotedAppRow = appClientIdFilter && !isScopedReloading
    ? events.rows?.find((row) => row.connectedAppClientId === appClientIdFilter)
    : undefined;
  const pivotedAppKey = appNameKey(appClientIdFilter);
  if (pivotedAppRow) knownAppNames.set(pivotedAppKey, pivotedAppRow.connectedAppName);
  const pivotedAppLabel = !appClientIdFilter ? null
    : !knownAppNames.has(pivotedAppKey) ? t("selectedConnectedApp")
    : knownAppNames.get(pivotedAppKey) ?? t("unnamedConnectedApp");
  const updateRecordPreview = useCallback((checked: boolean) => {
    const next = new URLSearchParams(searchParams);
    if (checked && previewEntityId) next.set("entityId", previewEntityId);
    else next.delete("entityId");
    setSearchParams(next);
  }, [previewEntityId, searchParams, setSearchParams]);

  return (
    <section>
      <Typography variant="overline" color="text.secondary" component="p" sx={{ m: 0 }}>
        {t("eyebrow")}
      </Typography>
      <Typography variant="h1">
        {entityId
          ? (scopedEntityType
              ? t("scopedHeading", { entityType: entityTypeLabel(scopedEntityType) })
              : t("scopedHeadingFallback"))
          : t("heading")}
      </Typography>
      <p className="muted">{t("intro")}</p>

      <div className="audit-scope" data-testid="audit-scope">
        <small>{entityId ? t("scopeRetainedCaption") : t("utcTimestampsCaption")}</small>
        <span className="audit-scope-controls">
          <FormControlLabel
            className="audit-scope-control"
            label={t("connectedAppsOnlyLabel")}
            control={(
              <Checkbox
                size="small"
                checked={appsOnlyFilter}
                onChange={(event) => updateAppsOnlyFilter(event.target.checked)}
              />
            )}
          />
          <FormControlLabel
            className="audit-scope-control"
            label={t("previewRecordHistoryLabel")}
            control={(
              <Checkbox
                size="small"
                checked={entityId !== undefined}
                disabled={entityId === undefined && previewEntityId === undefined}
                onChange={(event) => updateRecordPreview(event.target.checked)}
              />
            )}
          />
        </span>
      </div>

      <AuditFilters
        entityTypeFilter={entityTypeFilter}
        actionFilter={actionFilter}
        availableActions={availableActions}
        fromFilter={fromFilter}
        toFilter={toFilter}
        isNarrowed={isNarrowed}
        onEntityTypeChange={updateEntityTypeFilter}
        onActionChange={updateActionFilter}
        onDateChange={updateDateFilter}
        onClear={clearFilters}
      />
      <p className="muted audit-app-pivot" role="status" tabIndex={-1} ref={pivotCaptionRef}>
        {pivotedAppLabel !== null && t("connectedAppPivotCaption", { app: pivotedAppLabel })}
      </p>

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
        //
        // #666 — and the same is true of a date window. "No audit events yet."
        // under an active range is a false statement about the log (INV-4).
        // Deliberately still a muted paragraph, not an <EmptyState>: #655
        // classified this screen's empty state and did not list it among the
        // thirteen EmptyState sites, so this stays out of
        // emptyStates.guard.test.ts's registries.
        // A filtered window can be empty even when the full log has events.
        <p className="muted">
          {entityId
            ? (isNarrowed ? t("scopedFilteredEmptyMessage") : t("scopedEmptyMessage"))
            : (entityTypeFilter ? t("recordTypeEmptyMessage")
              : isNarrowed ? t("filteredEmptyMessage") : t("emptyMessage"))}
        </p>
      ) : (
        <>
          <div className="audit-events">
            {events.rows.map((e) => (
              <AuditEventRow
                key={e.id}
                event={e}
                appClientIdFilter={appClientIdFilter}
                showOnlyApp={showOnlyApp}
              />
            ))}
          </div>
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
