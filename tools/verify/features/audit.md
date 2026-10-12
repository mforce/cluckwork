# Audit

The read-only audit trail (#93): every corrective, destructive or configuration
change — who did it, when (UTC), and why. The screen has no mutation surface;
rows are written by the server inside the transactions they record.

## Sub-features

- Filter bar (`web/src/routes/AuditFilters.tsx`): record type, action, From/To.
  Every filter lives in the URL (`?entityType=`, `?action=`, `?from=`, `?to=`)
  — a filtered view is a shareable link, and each write replaces the whole
  query string (INV-2, `AuditPage.tsx`).
- Rows (`web/src/routes/AuditEventRows.tsx`): one `<details role="article">`
  per event, named "<timestamp> UTC · <action>" with the actor email as the
  summary's second line; expanding shows the entity, the payload summary
  (`audit:details*`, e.g. discount reasons #745/#756) or the raw reason.
- Entity-scoped mode: `?entityId=<guid>`, reached from a record's own screen
  via its "Audit history" link (`common:recordHistory.viewHistoryLink`,
  `web/src/components/ProvenanceCell.tsx` — the `common` namespace, dotted
  key). The heading is "<entityType> history" (`audit:scopedHeading`) only
  once the first scoped page has returned a row; while loading, and when the
  scoped record has no events, it is the fallback `audit:scopedHeadingFallback`
  ("Record history") — `AuditPage.tsx` (`scopedEntityType`,
  `t("scopedHeadingFallback")`).
- Connected-app filters (#800): "Only actions through connected apps"
  checkbox and the per-row "Show only {{app}}" pivot.
- 100 events per page (`PAGE` in `AuditPage.tsx`), then a button (styled as a
  link) labelled `audit:loadMoreButton` (`AuditPage.tsx` renders
  `<button className="link">`).

## How to get to it (user POV)

Nav: Setup, Audit (`web/src/routes/nav.tsx`, shown to the `isAdmin` tier —
Admin or Manager, `web/src/auth/claims.ts:currentUserIsAdmin`). Route
`/audit`. There is no SPA route gate by design: the API refuses other roles
server-side, and the screen renders its error paragraph with no data
(`tools/simulation/ui/specs/readonly.spec.ts`).

## Driving it with Playwright

```ts
import { test, expect } from "../src/fixtures";
import { owner } from "../src/cast";
import { tEn } from "../src/i18n";

await signIn(owner());
await page.goto("/audit");
await expect(page.getByRole("heading", { name: tEn("audit:heading") })).toBeVisible();
const panels = page.getByRole("article");
await expect(panels.first()).toBeVisible();
// Filter by action; option text comes from enums:auditAction.*, never English.
const filter = page.getByLabel(tEn("audit:actionFilterLabel"), { exact: true });
const option = filter.locator("option").nth(1);
const value = await option.getAttribute("value");
expect(value, "the option has a server value").toBeTruthy();
await filter.selectOption(value!);
// Deeper pages: the control is a BUTTON styled as a link, not role=link.
await page.getByRole("button", { name: tEn("audit:loadMoreButton") }).click();
```

Labels come through `tEn("audit:…")`; action and entity-type option text route
through `enums:auditAction.*` / `enums:entityType.*` (see the `audit` catalog
header in `web/src/i18n/en.ts`). Timestamps, actor emails and entity ids are
raw DATA, never routed through the catalog.

Proof: `owner.spec.ts` "audit browse lists real entries and filters by action"
asserts every summary line carries the chosen action, and "record type alone
narrows the audit rows" pins the `/api/v1/audit` response contract.
`readonly.spec.ts` proves a direct link to `/audit` renders refused with the
refusal status captured from the network.

## Gotchas

- Timestamps are UTC on purpose (`audit:utcTimestampsCaption`): a From/To
  window is inclusive UTC calendar days (`AuditPage.tsx` cites
  `AuditEventRepository.ListAsync`), not the farm-local business date Expenses
  filters on. Do not fill `farmToday` and expect a farm behind UTC to show
  today's events.
- Filter reloads are asynchronous: rows are blanked while a reload is in
  flight and on any scope change (`isFetchStale`, `AuditPage.tsx` #493) —
  poll, do not assert in the render after `selectOption`.
- A hand-edited noncanonical `entityId` GUID silently falls back to the
  unscoped view (`GUID_PATTERN`, `AuditPage.tsx` #493).
- The empty states are distinct on purpose (`audit:emptyMessage` vs the
  scoped/filtered variants, #493/#666) — assert the one your scenario
  actually produces, and expect `audit:scopedHeadingFallback`, not an entity
  name, when a scoped view loads or filters to zero rows.
- The fixture's seeding writes populate a non-empty audit log — flock
  creation, product creation and order actions run through audited handlers
  — but NOT every write: payments, for example, are recorded without an
  audit writer (`RecordPaymentHandler.cs` persists and logs, no
  `IAuditWriter`). Do not expect an audit event per fixture row.
