# Architecture: Entity-scoped audit history

> **Planning record — entity-scoped "View history" ([#493](https://github.com/mforce/cluckwork/issues/493)), August 2026.** What was *intended* at the time, not what shipped. The issue is closed; where this disagrees with the code, the code is right. See [`docs/plans/README.md`](../README.md).

## Fit

SPA-only, as the issue specifies — one backend-adjacent question flagged below for the metric only, not the feature itself.

Touches:
- `web/src/routes/AuditPage.tsx` — reads `entityId` from the URL, scopes the query, adjusts heading.
- Six row-level "View history" links, one per screen: `FlocksPage.tsx`, `GradesPage.tsx`, `SalesPage.tsx`, `ExpensesPage.tsx`, `HistoryPage.tsx` (Daily Entries), `StockPage.tsx` (Egg Lots).
- `web/src/i18n/{en,es,tl}.ts` — new copy for the link and the scoped heading.
- `specs/product/GLOSSARY.md`, `web/src/routes/HelpPage.tsx` — sync per AGENTS.md.
- **Decided at Gate 2**: `src/Cluckwork.Api/Program.cs` — one line in `UseSerilogRequestLogging`'s diagnostic context, added to make the success metric (server-side `entityId`-request counting) real. The one non-SPA line in the ticket; observability only, no change to the audit surface itself.

No domain, no migration, no new endpoint — matches the issue's explicit "out of scope."

## Endpoints

None new. Existing `GET /api/v1/audit` (`AuditEndpoints.cs`) already accepts `action`, `entityId`, `from`, `to`, `limit`, `offset` and is `AdminOnly`-gated — unchanged by this feature.

## Data

No new tables, no model change. Reads go through the existing `IAuditEventRepository.ListAsync(action, entityId, from, to, take, skip, ct)` → `AuditEventRepository`, already tenant-scoped. `entityId` alone is sufficient to filter correctly (per-aggregate GUID, never reused across entity types), so no `entityType` round-trip to the server is needed.

## Flow

Main path:

1. Admin/manager is on a list screen (Flocks, Grades, Sales, Expenses, Daily Entries/`HistoryPage`, or Egg Lots/`StockPage`) and clicks "**Audit history**" on a row. Deliberately not "History" — `StockPage` already has a "History"/"Hide history" toggle for the inventory movement ledger on the same row; the two must read as different things, so the new link's label is decided now, not left to Gate 3 copy-TBD.
2. That's a plain `<Link to={`/audit?entityId=${row.id}`}>` — same `react-router` `Link` component already used elsewhere on these pages (e.g. `HistoryPage.tsx`'s existing edit link), no new navigation mechanism.
3. `AuditPage` mounts (or re-renders if already on `/audit`) and reads `entityId` via `useSearchParams()` — precedent already in the codebase (`FeedPage.tsx`). **The action-type filter also moves into `useSearchParams`** (from its current local `useState`), so `entityId` and `action` are both URL-derived — one source of truth for what `usePagedList`'s fetcher depends on, no split between state and URL. (Pi review raised a stale-closure drift concern here; checked against `usePagedList.ts` — the fetcher's `useCallback` deps are re-read fresh every render regardless of source, so the specific bug described doesn't exist. Unifying into the URL anyway: it's cheap, and it makes the filtered view bookmarkable/shareable, which a split state+URL design wouldn't allow.)
4. When `entityId` is present: heading identifies the record using **`entityType` read from the first row of the response**, not from the URL — the API already returns `entityType` on every event, so there's no need to thread a second, unvalidated, display-only param through the URL (that was the Gate-2-draft's original half-wired plan; dropped). Before the first page loads, or if the record has zero audit events, the heading falls back to a generic "Record history" — deliberately not special-cased for "this ID doesn't exist" vs "exists but has no events" vs "was deleted": v1 doesn't distinguish these, and isn't trying to. Action-type filter **stays visible** (Gate 1 decision) as an additional narrowing control alongside the entity scope. Entity-type/id column is **hidden when scoped** (decided now — every row would otherwise repeat the same value up to 100 times, which is noise, not neutral).
5. **Malformed `entityId` guard**: a hand-edited URL can carry a non-GUID `entityId`, which the endpoint would 400 on. The SPA checks the param's shape client-side before calling the API; if it doesn't look like a GUID, treat it as absent and render the normal unscoped view rather than firing a request that will fail.
6. `usePagedList`'s fetcher (`useCallback` over `listAuditEvents({...})`) depends on both URL params now — changing either triggers the existing #469 "identity changed → reload from top" discipline, extended from one dependency to two, with no new mechanism.
7. `listAuditEvents({ action, entityId, limit, offset })` → `GET /api/v1/audit?entityId=<guid>&action=<optional>&...` → existing endpoint/repository/tenant filter, unchanged. This is the **first production caller** of the `entityId` filter — it has shipped unused until now. Gate 3's test plan should include a test that exercises this path end to end (SPA client → endpoint → repository), not just rely on the endpoint's existing isolated coverage.
8. **No link back to the originating record.** There's no detail page to link back to (Gate 1's "list-row links are a stopgap" stance) — v1 relies on the browser's Back button only. Stated explicitly here so it isn't rediscovered as a gap later.
9. **Metric path**: the `GET /api/v1/audit` request above is what the `Program.cs` diagnostic-context line observes. **Refined from the original one-line plan**: presence-of-`entityId` alone would also count requests that 400 on a malformed/empty value (pi review, verified against the endpoint's `Guid?` binding — an unparseable value fails binding before the handler runs). The enrichment condition is presence of the `entityId` query key **AND** a 200 response — still one added line, now actually measuring successful entity-scoped reads rather than raw attempts. Also renaming this internally from "adoption" to what it actually is: a count of successful `entityId`-scoped requests per week, not a distinct-user or distinct-session measure (the request logs carry no actor identity today, and adding one is out of scope for this ticket).

No write path — this feature adds zero mutations, matching `AuditPage`'s existing "deliberately no mutation surface" comment.

## External

None.
