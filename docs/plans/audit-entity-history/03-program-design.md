# Program Design: Entity-scoped audit history

## Files

- `web/src/routes/AuditPage.tsx` — modify. URL becomes the single source of truth for both `action` and `entityId`; scoped heading derived from the first response row; entity column hidden when scoped; malformed-`entityId` guard.
- `web/src/routes/FlocksPage.tsx`, `GradesPage.tsx`, `SalesPage.tsx`, `ExpensesPage.tsx`, `HistoryPage.tsx` — modify. One `<Link>` added per row, next to the existing `ProvenanceCell`.
- `web/src/routes/StockPage.tsx` — modify. One `<Link>` added per lot row, alongside (not replacing) the existing movement-ledger "History" toggle — the two must stay visually distinct.
- `web/src/i18n/en.ts`, `es.ts`, `tl.ts` — modify. New keys under `common.recordHistory` (the link label, reused across all six pages — same namespace `ProvenanceCell` already uses) and under `audit` (scoped heading, generic fallback).
- `specs/product/GLOSSARY.md` — modify. Extend the existing "Record history (#494)" / "Audit log (#93)" entries to name the new per-record link, per AGENTS.md's doc-sync rule.
- `web/src/routes/HelpPage.tsx` — modify. One new bullet alongside the existing `auditRecordHistory*` bullets (lines 385-390 today). Its copy key lives in `HelpPage`'s own `help` i18n namespace (confirmed: the page calls `useTranslation("help")`, separate from the `common`/`audit` namespace keys elsewhere in this doc) — not previously named, adding it here so the i18n footprint is fully mapped.
- `src/Cluckwork.Api/Program.cs` — modify. One `EnrichDiagnosticContext` assignment inside the existing `UseSerilogRequestLogging` call.
- `tests/Cluckwork.Api.IntegrationTests/RequestLoggingTests.cs` — modify. New test(s) using the existing `RequestLoggingFactory`/`CollectingSink` harness.
- `web/src/routes/AuditPage.test.tsx`, and one test each in `FlocksPage.test.tsx`, `GradesPage.test.tsx`, `SalesPage.test.tsx`, `ExpensesPage.test.tsx`, `HistoryPage.test.tsx`, `StockPage.test.tsx` — modify.

No new files.

## Types & signatures

```ts
// AuditPage.tsx — replaces the local `useState<string>` for actionFilter.
// Both filters now read from (and write to) the URL via react-router's
// useSearchParams. NOTE: FeedPage.tsx, this repo's only other useSearchParams
// user, only ever READS params (local useState seeded from the initial URL,
// never written back) — there is no working setSearchParams precedent here.
// react-router's setSearchParams REPLACES the whole query string; it does not
// merge. Every call below must pass a complete params object.

function isLikelyGuid(value: string): boolean;
// Canonical 8-4-4-4-12 hex form only — not full Guid.TryParse permissiveness.
// This is a correctness requirement, not just hygiene: the endpoint's model
// binder is stricter than TryParse's more permissive accepted forms, so a
// looser client check would let some malformed values through to a
// guaranteed 400. A value from a rendered <Link> always already matches
// (it originates from row.id, server-issued); this only guards a
// hand-edited or pasted URL.
// Known, accepted trade-off: ASP.NET's Guid? binder is MORE permissive than
// this canonical-only check (it also accepts braced/no-hyphen forms), so a
// hand-typed valid-but-noncanonical GUID gets silently treated as absent
// (falls back to the unscoped view) rather than being sent to the server.
// Every server-issued link is always canonical, so this only affects a
// user typing a GUID by hand in a nonstandard format — low-probability,
// not engineered around.

function scopedEntityType(rows: AuditEvent[] | null, reloading: boolean): string | null;
// rows[0]?.entityType, but ONLY when NOT reloading. usePagedList's `load`
// leaves the previous window's rows in place until the new page lands
// ("the rows stay put for the duration" — usePagedList.ts's own comment);
// AuditPage's existing table already gates on `rows === null || reloading`
// for exactly this reason. The heading must use the same gate, or a filter
// change or entity switch shows the PREVIOUS entity's type while the new
// data is still in flight. Returns null (generic fallback) while reloading,
// while rows is null, or once loaded if the entity has zero audit events —
// deliberately not distinguishing those three cases.

function updateActionFilter(
  searchParams: URLSearchParams,
  action: string,
  setSearchParams: SetURLSearchParams,
): void;
// Builds a NEW URLSearchParams from the current one (preserving entityId
// and anything else present), sets or deletes `action` on the copy, then
// calls setSearchParams with the whole copy — never a partial object.
```

```ts
// common namespace (web/src/i18n/en.ts) — same group ProvenanceCell reads.
recordHistory: {
  // ...existing createdBy/lastChangedBy/submittedAt/confirmedAt keys...
  viewHistoryLink: string; // "Audit history" — the new per-row link label
}
```

```ts
// audit namespace (web/src/i18n/en.ts)
audit: {
  // ...existing heading/intro/actionFilterLabel/... keys...
  scopedHeading: string;         // "History for this {{entityType}}" — entityType interpolated via entityTypeLabel()
  scopedHeadingFallback: string; // "Record history" — used before first row loads or on zero results
  scopedEmptyMessage: string;    // "No audit events for this record yet." — replaces the generic
                                  // emptyMessage ("No audit events yet.") when scoped. Real gap found
                                  // in review: the generic message reads as "the whole audit log is
                                  // empty," which is wrong and confusing for a record with a clean history.
}
```

```csharp
// Program.cs — inside the existing app.UseSerilogRequestLogging(options => { ... }) call.
options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
{
    var isEntityScopedAuditRead =
        httpContext.Request.Path == "/api/v1/audit"
        && httpContext.Request.Query.ContainsKey("entityId")
        && httpContext.Response.StatusCode is >= 200 and < 300;

    if (isEntityScopedAuditRead)
        diagnosticContext.Set("EntityScopedAuditRequest", true);
};
```

Range check, not an exact `== 200` — the endpoint only returns `Results.Ok` today, but coupling `Program.cs`'s observability to one endpoint's exact status literal is a latent break waiting for an unrelated future change.

**Known, accepted limitation, not fixed here**: this counts any successful-shaped request, including (a) a syntactically valid `entityId` that matches no record (e.g. all-zeros) and (b) a cross-tenant read — `AuditTests.cs`'s own `Viewer_NeverCrossesTenants` test proves a foreign account's `entityId` returns `200` with an empty array, same as case (a). Both return 200 with zero rows and both count as a "successful scoped read." Distinguishing either would need response-body inspection, which is out of proportion for a one-line log enrichment on a metric that was already scoped down to "is the affordance being exercised," not an exact or security-relevant count.

No change to `listAuditEvents`, `AuditEndpoints.cs`, `IAuditEventRepository`, or any domain/application type — all already support everything this feature needs.

## Call stack

**Flow A — click "Audit history" on a list row (fresh navigation, `AuditPage` not yet mounted):**
`FlocksPage` row render → `<Link to="/audit?entityId=<row.id>">` → react-router navigation → `AuditPage` mounts → `useSearchParams()` reads `action`/`entityId` → `isLikelyGuid` passes → `fetchPage`'s `useCallback` deps are the *derived* `action`/`entityId` string values, not the `URLSearchParams` object itself (that object's identity changes on every navigation regardless of content, which would otherwise reload on every render) → `listAuditEvents({ action, entityId, limit, offset })` → `GET /api/v1/audit?entityId=...` → `AuditEndpoints.ListAuditEvents` → `IAuditEventRepository.ListAsync` → tenant-scoped Postgres read → `AuditEventResponse[]` → SPA renders rows, `scopedEntityType(rows, reloading)` drives the heading, entity column hidden.

**Flow A′ — click a different record's "Audit history" link while already on `/audit`:** no remount — `AuditPage` re-renders with a new `entityId` from `useSearchParams`. Same downstream chain as Flow A from the `fetchPage` identity change on; the doc originally said "mounts," which only covers the fresh-navigation case.

**Flow B — change action filter while scoped:**
`<select>` onChange → `updateActionFilter(searchParams, action, setSearchParams)` → builds a full replacement `URLSearchParams` (action changed, entityId preserved) → `setSearchParams` → re-render → `action`/`entityId` re-derived from URL → `fetchPage` identity changes as a *consequence* (there is no direct `entityId`-to-`reload` wiring, it's the fetcher identity that #469 watches) → `usePagedList`'s existing "identity changed → reload from top" fires, `reloading` goes true → heading and table both fall back to their loading/generic states → same chain as Flow A from `listAuditEvents` on, `reloading` clears once the new page lands.

**Flow C — malformed `entityId` (hand-edited URL):**
`AuditPage` mounts → raw `entityId` present but `isLikelyGuid` fails → `entityId` resolves to `undefined` → page renders the same as today's unscoped audit log for every behavior that matters (no request carries the bad value, entity column shown, generic action-filter-only view) — "the same," not a claim of byte-identical DOM; the frontend test asserts those specific behaviors, not a DOM diff against the true-unscoped path.

**Flow D — metric:**
`GET /api/v1/audit?entityId=...` completes with 200 → `UseSerilogRequestLogging`'s `EnrichDiagnosticContext` sets `EntityScopedAuditRequest=true` → one compact-JSON completion line (#404) carries that property → queried from the log store out of band (no application code reads it back).

## Test plan

**The `entityId` filter mechanism is already integration-tested for 2 of the 6 entity types this feature links to.** `AuditTests.cs` exercises `GET /api/v1/audit?entityId=...` end to end against a real HTTP client and a real DB for Flocks and Daily Entries, including tenant isolation (lines ~145-150). It does not exercise Grades, Sales Orders, Expenses, or Egg Lots specifically. The repository query is a generic `WHERE EntityId = @id` with no per-entity-type branching, so this is calling the risk low for the other four, not claiming full coverage — softened from an earlier "no new backend test needed" that overstated it. Still no new backend test added for the filter itself: the risk is judged low enough given the generic query shape, not zero.

On Gate 2's "first production caller" framing: this doc's revised test plan deliberately does **not** add a Vitest test that drives the SPA through a real HTTP call to a real backend. Checked against this codebase's own convention (`AuditPage.test.tsx` and every other page test mocks `listAuditEvents`/equivalent API-client functions at the module boundary) and AGENTS.md's own architecture (genuine browser-driven E2E lives in Playwright, `tools/simulation/ui/`, deliberately separate from Vitest, #277/#385) — a hybrid SPA-through-real-HTTP Vitest test would be a new testing pattern not used anywhere else in this repo, disproportionate for this ticket. The frontend/backend halves are each tested at their own layer, per the established split; that's a design choice this codebase already made, not a gap this feature introduces.

**Backend** (`RequestLoggingTests.cs`, `RequestLoggingFactory`/`CollectingSink` harness — already exists, used as-is):
- `Entity_scoped_audit_read_sets_diagnostic_property` — `GET /api/v1/audit?entityId=<seeded valid id>` returns 200; asserts a completion `LogEvent` for that request exists, and that it carries `EntityScopedAuditRequest=true`.
- `Malformed_entity_id_does_not_set_diagnostic_property` — `GET /api/v1/audit?entityId=not-a-guid` returns 400; asserts a completion event still exists for it (confirmed possible: `Program.cs`'s #398 comment establishes `BindingFailureResponse` does not rethrow, so `UseSerilogRequestLogging` still logs a 400 completion, exception null) and that the property is absent on it — not just "absent," but "absent from a real event," so the test can't vacuously pass.
- `Unscoped_audit_read_does_not_set_diagnostic_property` — `GET /api/v1/audit` (no `entityId`), 200; same "event exists, property absent" shape.
- `Zero_match_entity_id_still_sets_diagnostic_property_KNOWN_LIMITATION` — documents the accepted limitation above (named explicitly so a future reader sees it as a pinned wart, not a spec): `GET /api/v1/audit?entityId=<well-formed, no matching rows>` returns 200 with an empty array; asserts the property IS set anyway.

**Frontend** (`AuditPage.test.tsx`, extend):
- Renders a scoped heading naming the entity type when `entityId` is present and rows have loaded.
- Falls back to the generic heading when `entityId` is present but zero rows return.
- **Falls back to the generic heading (not the previous entity's type) while a reload is in flight** — the stale-heading case: render already-scoped with rows loaded, trigger an action-filter change backed by a manually-controlled (deferred) promise — not one that resolves synchronously/same-tick, which would make `reloading`'s true window unobservable to the test — assert the heading is generic while that promise is unresolved, then resolves to the new entity's type once it settles. This is the test that would have caught `scopedEntityType`'s original bug.
- Calls `listAuditEvents` with `entityId` when present in the URL.
- Treats a malformed `entityId` as absent: does not call `listAuditEvents` with it, renders the unscoped view.
- Hides the entity column when `entityId` is present; shows it when absent (existing global behavior unchanged).
- **The empty-scoped-state message differs from the global empty message** — when `entityId` is present and the response is `[]`, the table area shows `scopedEmptyMessage` ("No audit events for this record yet."), not the generic `emptyMessage` ("No audit events yet.") that would misleadingly imply the whole log is empty.
- **The actual URL round-trip, scoped case**: render inside `renderWithProviders` (already wraps `MemoryRouter`, confirmed — no new test infrastructure needed) with `initialEntries=["/audit?entityId=<id>"]`, change the action filter, and assert the resulting URL still contains both `entityId` and the new `action` — proving `updateActionFilter`'s merge logic actually works, not just that `listAuditEvents` was called with the right args (which would pass even with a broken merge, since the mock doesn't care what produced the args).
- **The actual URL round-trip, unscoped case**: same test shape starting from `initialEntries=["/audit"]` (no `entityId`), change the action filter, assert the resulting URL carries only `action` — the merge logic's other branch, where there's no `entityId` to preserve. Catches a helper that assumes `entityId` is always present.

**Frontend** (one test each in `FlocksPage.test.tsx`, `GradesPage.test.tsx`, `SalesPage.test.tsx`, `ExpensesPage.test.tsx`, `HistoryPage.test.tsx`):
- Renders an "Audit history" link per row pointing to `/audit?entityId=<row id>`.

**Frontend** (`StockPage.test.tsx`):
- Renders an "Audit history" link per lot row, distinct from the existing movement-ledger "History" toggle, and asserts they do different things (the link navigates; the toggle expands the movement ledger in place) — not just that both elements are present.

**i18n**: existing namespace-coverage tests (per the repo's translate-now policy) should catch any new key missing from `es.ts`/`tl.ts` without a dedicated new test.

## Least confident decisions

1. **`entityType` for the heading comes from the loaded rows, not a URL param** — confirmed correct given `scopedEntityType`'s `reloading` gate above, but it does mean the heading is unavailable until the *first* successful page for a given entity lands; there is a brief "generic heading" flash on every fresh navigation into a scoped view, not just on errors. Accepted as the cost of not threading an unvalidated `entityType` through the URL.
2. **`isLikelyGuid`'s canonical-only strictness silently unscopes a valid-but-noncanonical hand-typed GUID** (braced/no-hyphen forms the backend's binder would actually accept) — see the note under its signature. Accepted as a low-probability edge case since every real link is server-issued and always canonical; not engineered around with fuller parsing.

Resolved during this review round (were open questions, now decided, not carried forward):
- ~~Shared `common.recordHistory` namespace, per-page import unverified~~ → checked all six pages: every one already calls `useTranslation("common")`. Confirmed safe, not open.
- ~~Clearing the action filter: delete key vs `action=`~~ → `listAuditEvents` drops a falsy `action` from the outgoing call either way (`if (params?.action) q.set(...)`); the choice only affects the address bar, not behavior. Settled, not actually ambiguous.
- ~~Exact `StatusCode == 200` vs range check~~ → range check, see Types & signatures.
- ~~`isLikelyGuid` strictness (whether to guard at all)~~ → narrow regex confirmed as a correctness requirement, not a style choice.
- ~~Whether `RequestLoggingTests.cs` is the right test home~~ → confirmed: it's the only harness with a live tap on completion `LogEvent`s, matches exactly.
- ~~Whether `StockPage.test.tsx` needs new router test infrastructure for the "link navigates vs. toggle expands" test~~ → checked `renderWithProviders.tsx`: already wraps `MemoryRouter`. No new infrastructure.
