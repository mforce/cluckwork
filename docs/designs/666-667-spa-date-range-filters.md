# 666 / 667 — date-range filters on Audit and Expenses

Status: **approved by the owner, 2026-09-03** (Stock added to scope in the same approval)
Base: `965c73745fc2784bb155cfdf8ff399870ed0c196`
Issues: [#666](https://github.com/mforce/cluckwork/issues/666), [#667](https://github.com/mforce/cluckwork/issues/667)
Predecessor: #653 (introduced the bounded `.toolbar` these two screens plug into)

## What both issues were actually asking

Both were filed as open questions with an API-side unknown. Both unknowns are answered, and the
answer is the same one:

**The API already supports a date range on both screens.**

- `src/Cluckwork.Api/Endpoints/Audit/AuditEndpoints.cs:29-30` — `ListAuditEvents` takes
  `DateOnly? from` / `DateOnly? to`; `IAuditEventRepository.ListAsync` carries them through.
  `web/src/routes/AuditPage.tsx:132` simply never sends them.
- `src/Cluckwork.Api/Endpoints/Expenses/ExpenseEndpoints.cs:122-134` — `ListExpenses` takes
  `from`/`to`, and `IExpenseRepository.ListAsync`/`SumAsync` both filter on them. `ExpensesPage.tsx:145`
  already *converts its month into exactly that pair* (`monthRange`) before calling the API.

So neither slice needs an endpoint, a handler, a validator or a migration. Both are SPA-only.

## Decisions taken (owner, 2026-09-03)

| Question | Decision |
|---|---|
| #667: is the month granularity deliberate? | **No — convert to a from/to pair** matching Feed, Water, History and Reports. |
| #666: how far does the audit slice go? | **SPA only.** No migration, no schema-doc regeneration, #505 left as written — see below: the supporting index already exists, so this is not a deferral. |
| Slicing | **One PR, all three screens.** Same shape, same pattern, adjacent files. |
| Stock (found by the pre-review) | **Folded into this slice** rather than filed separately (owner, 2026-09-03). It is a one-line wrapper change; see Slice C. |

### Why no index — and the supporting index already exists

**The index this filter wants is already there.** `AuditEventConfiguration.cs:24` declares
`HasIndex(e => new { e.AccountId, e.OccurredAtUtc })`, beside `(AccountId, EntityId)` on line 25. A
date-narrowed audit read under a resolved tenant is an index lookup today, before this slice changes
anything.

[#505](https://github.com/mforce/cluckwork/issues/505) is about **partitioning, not indexing**, and the two must not be conflated: it argues that
range-partitioning by month is the wrong *axis*, because pruning only helps a query filtering on the
partition key and the dominant read (`AccountId`+`EntityId`) does not — turning one lookup on
`IX_AuditEvents_AccountId_EntityId` into one per partition
([`505-audit-events-no-time-partition.md`](../decisions/505-audit-events-no-time-partition.md), lines
14-20). Nothing in that record says the table has no date index, and #666's worry that "a date-filtered
audit query is exactly the read that decision says the table is not optimised for" rests on that
conflation.

So this slice adds **no migration and no index**, not as a deferral to be measured later, but because the
schema already supports the read. #505 needs no amendment.

## Selector call-site count (#662 rule)

`grep -rn "toolbar" web/src --include='*.tsx'` → **5 files**, all live:

| File | Line | Shape |
|---|---|---|
| `ExpensesPage.tsx` | 490 | month picker (this slice replaces its contents) |
| `HistoryPage.tsx` | 557 | from/to pair |
| `WaterPage.tsx` | 465 | from/to pair |
| `FeedPage.tsx` | 356 | from/to pair |
| `ReportsPage.tsx` | 85 | from/to pair, whole bar |

`AuditPage.tsx` has **zero** `.toolbar` call sites and no date input — it has a `.filters` div only
(`AuditPage.tsx:215`). This slice adds its first. Nothing is being styled blind: the class exists, is
used by four screens, and is not being changed.

## The canonical pattern being copied

`FeedPage.tsx:354-363` is the reference — a `.toolbar` div holding two `<label>`-wrapped
`<input type="date">` controls, sitting *inside* the surrounding `.filters` div, with non-date filters
left outside it. `fromLabel` / `toLabel` already exist in the `feed`, `water`, `history` and `reports`
namespaces in all three locales.

## Slice A — Audit (#666)

### The delicate part

`AuditPage` is not Feed. Its own header comment (`AuditPage.tsx:55-60`) states the rule:

> The URL is the single source of truth for BOTH `action` and `entityId`: react-router's
> `setSearchParams` REPLACES the whole query string rather than merging, so every write here goes
> through `updateActionFilter`, which builds a full copy from the CURRENT params.

The new `from`/`to` therefore live in `searchParams`, **not** in `useState` as Feed has them. Feed's
local-state form would put two filters in a place the rest of this screen's machinery does not read, and
`fetchPage`'s identity — which is what `usePagedList` and `isFetchStale` key the whole stale-window
discipline on (`AuditPage.tsx:130-197`) — must move when the range moves.

### Behaviour

1. `from` and `to` are read from `searchParams`, exactly as `action` is, and validated: a value that is
   not an ISO `YYYY-MM-DD` date is **ignored** (treated as absent), the same fail-soft the `entityType`
   and `action` params already get for out-of-range values. A hand-edited or shared URL must not be able
   to put the visible control and the query out of sync — that is the #521 finding, applied to a new
   param.
2. A single `updateDateFilter(field, value)` builds a full copy of the current params (never a partial
   object) and writes back with **`{ replace: true }`**. A `<input type="date">` fires `onChange` per
   keystroke on some browsers; without `replace` each partial date would push its own history entry, and
   Back would walk the user through half-typed dates.
3. `fetchPage` gains `from`/`to` in both its arguments and its dependency array. Everything downstream —
   the reload, the blanked table, `isFetchStale`, `committedFetchPage` — then works unchanged, because
   the screen already expresses "the filter changed" as "the fetcher's identity changed".
4. Empty result with a range set gets its own message. Today `AuditPage.tsx:246-248` renders
   `emptyMessage` ("No audit events yet.") or `scopedEmptyMessage`. Under a date filter that sentence is
   **false** — the log is not empty, this window is. A third message is required, not optional: this is
   the exact class of wrong-statement the sibling screens use `noRecordsMatch` for. Scope and range are
   two independent narrowings, so a scoped view with a range active needs a **fourth** message
   (`scopedFilteredEmptyMessage`) rather than either branch alone — checking `entityId` first and stopping
   there silently drops the range fact, and is false the moment that record has events outside the window
   (CodeRabbit, round 1).

   Round 2 found a third narrowing axis on this same screen: `actionFilter` predates this slice and can
   empty the view on its own, which the two-axis enumeration above missed. The block was reshaped rather
   than given a fifth state — it now keys on `isNarrowed` ("is anything narrowing the view at all", across
   scope, action, and range) with four sentences over three axes, and the filtered wording matches the
   sibling screens' `noRecordsMatch` rather than naming the date range specifically, since the range is no
   longer the only thing that can produce it.

### Files

- `web/src/routes/AuditPage.tsx`
- `web/src/routes/AuditPage.test.tsx`
- `web/src/i18n/{en,es,tl}.ts` — `audit.fromLabel`, `audit.toLabel`, `audit.filteredEmptyMessage`,
  `audit.scopedFilteredEmptyMessage` (added in the round-1 fix; both filtered keys were reworded in the
  round-2 reshape to the sibling-consistent "match these filters" form)

`web/src/api/cluckwork.ts` needs **no change**: `listAuditEvents` at line 918-930 already accepts and
serialises `from`/`to`. The client, the endpoint and the repository are all ready; only the screen is not.

## Slice B — Expenses (#667)

### Behaviour

1. `month` state is replaced by `from`/`to` state and the `monthRange` helper
   (`ExpensesPage.tsx:145-149`) is deleted — `fetchPage` sends `from`/`to` directly.
2. **Default range = the current farm month.** This is a deliberate divergence from the siblings, which
   open blank. Expenses opens on the current month today, and its period total (`monthTotalLabel`,
   `ExpensesPage.tsx:521`) is a money figure whose meaning comes from that window. Opening blank would
   silently change the screen's default from "this month's spend" to "every expense ever recorded, and a
   total to match", which is a bigger behaviour change than either issue asked for and lands on a money
   screen. The user can widen or clear it; the default preserves what ships today.
3. Neither range-filter input carries `max={today}`. It was carried over from the month picker this
   replaces, where `max` was the current MONTH — a granularity that contains its own month-end default. On
   a day-granularity control it does not: the default `to` is month-end, so the cap made the input render a
   value it forbade (constraint-invalid from first paint) and made that default unreachable once the user
   changed it (round 5 finding, confirmed against the #662 capture). Removed on both bounds; a future window
   is still empty by construction, which is what the sibling range filters (Feed, Water, History) already
   rely on uncapped.
4. `monthTotalLabel` becomes a period-total label in all three locales — the figure now describes a
   range, and a string reading "Month total" over a two-week window is a wrong statement about money.
   Same for `noExpensesMessage` ("No expenses for this month."), and its `EmptyState` comment at
   `ExpensesPage.tsx:743-745` ("The month picker is always set (never cleared)") which stops being true:
   a cleared range needs the `noRecordsMatch` + clear-filters treatment Feed uses at
   `FeedPage.tsx:374-379`.
5. **A consequence of (2) worth stating: with a default range always set, the truly-empty state is only
   reachable after Clear filters.** A farm with no expenses at all opens the screen and sees "No expenses
   match these filters." rather than "No expenses recorded yet." That is correct — the default window is a
   filter and it is visible in the two controls, so claiming the farm has recorded nothing would be a
   statement the screen cannot support (INV-4 again). Clearing the filters reaches the accurate sentence,
   and the filtered branch offers exactly that action. Found by the pre-dispatch runbook review, which
   caught an existing test asserting a branch the new defaults make unreachable on first render.
6. Everything the existing comments protect stays protected: the total is still withheld while a reload
   is in flight and still withheld when `meta` is null (`ExpensesPage.tsx:508-520`). Those two guards
   exist because a figure from the previous window under the new window's control is the defect this
   screen was fixed for. **Do not simplify them.**
7. **`from`/`to` live in `useState`, not the URL — a deliberate choice nobody had stated.** This mirrors
   Feed, Water and History, which all hold their filters the same way; Audit is the outlier, and it is an
   outlier for a reason already on record — its own header comment made the URL the single source of truth
   for `action` and `entityId` before this PR touched it, so `from`/`to` joining that same source keeps one
   screen's existing contract rather than establishing a new one. The consequence is real and user-visible:
   an Audit window survives a refresh, Back, and a shared link; an Expenses window survives none of them.
   Recorded here so the next reader does not rediscover the asymmetry as a bug.

### Files

- `web/src/routes/ExpensesPage.tsx`
- `web/src/i18n/{en,es,tl}.ts` — `expenses.fromLabel`, `expenses.toLabel`, `expenses.periodTotalLabel`
  (replacing `monthTotalLabel`), a filtered-empty message, a retitled truly-empty message;
  `expenses.monthLabel` retired
- `web/src/routes/ExpensesPage.test.tsx`
- `web/src/routes/emptyStates.guard.test.ts`, `web/src/styles.css`, `web/src/styles.toolbar.test.ts` —
  see "Guards and registries" below

## Slice C — Stock (found by the pre-review, folded in by the owner)

`StockPage.tsx:533` wraps the egg-lot production-date filter in `<div className="filters">`. The width
cap at `styles.css:1083-1085` is scoped to `.toolbar input[type="date"]`, so it never applies here and
those two inputs render at the row's full width. Full-width date inputs are the defect
[#653](https://github.com/mforce/cluckwork/issues/653) was filed to fix, and it fixed them everywhere it
looked — but its file list never included `StockPage.tsx`, so this instance survived. Same defect, a screen
that issue did not reach.

**Everything else on this screen is already right.** The from/to pair works, hits a server-side window
(`#465`), and already has the two-variant empty state with a clear-filters action (`noLotsMatch` /
`noLotsMessage`, `StockPage.tsx:542-549`). `stock.fromLabel` and `stock.toLabel` already exist in `en`,
`es` and `tl` (`en.ts:1171-1172`).

**So the change is one word:** `filters` → `toolbar` on line 533. It follows `ReportsPage.tsx:85`'s form
rather than Feed's — the whole bar *is* the toolbar, because this section has no non-date filter to keep
outside it.

**No i18n change, no new key, no empty-state change, no registry touched.** `StockPage.tsx` already
appears in `emptyStates.guard.test.ts`'s `EMPTY_STATE_SITES` **and** `TWO_VARIANT_FILES`, and this change
alters neither the message keys nor the `EmptyState` call shape those two regexes match — verified by
reading both registries, not by assuming.

### Files

- `web/src/routes/StockPage.tsx` (one line)
- `web/src/routes/StockPage.test.tsx` — a test asserting the pair renders inside `.toolbar`. This is the
  one screen in the slice whose defect is *purely* visual, so the guard has to pin the structural fact
  (the wrapper class), since jsdom does not compute the width the bug is actually about.

### Why this earns the before/after screenshot

Slice C makes the PR's purpose partly visual, so AGENTS.md's #662 rule binds: a 1:1 before/after
comparison captured from a stack **rebuilt at the head under review**. Two inputs shrinking from
full-width to 12rem is precisely the change a downscaled screenshot destroys, and #661 is the precedent
where that comparison caught the one defect four review seats and three CI runs all passed.

## Two things the controls look like they share and do not

Added after the contrarian pre-review (2026-09-03). Neither is a defect to fix; both are decisions that
have to be *stated*, because the two screens grow identical-looking controls over different semantics.

### The day boundary is UTC on Audit and farm-local on Expenses

`AuditEventRepository.cs:15-25` treats `from`/`to` as inclusive calendar days **over the UTC timestamp**
(`>= fromUtc && < toUtc`, where `toUtc` is `to + 1 day` at midnight UTC). `ExpenseRepositories.cs:51-53`
filters `e.Date >= from && e.Date <= to` on a `DateOnly` **farm-local business date**. An event at 23:30
farm-local on the `to` day is inside the Expenses window and may be outside the Audit one.

**This is correct and stays.** Audit's own column header already reads "When (UTC)" (`audit.whenHeader`)
— the trail records instants and says so — while an expense's date is a business fact with no time at
all. Making them agree would mean either giving audit a farm-local reinterpretation its column contradicts,
or giving expenses a timezone it does not have. The help text names the difference instead.

### An inverted range (`from` later than `to`) is allowed through

Neither repository validates it: both build a `WHERE` that simply matches nothing. No sibling screen
guards against it either — Feed and Water ship bare inputs with no `min`/`max` at all, and only Reports
clamps.

**Decision: no client-side validation, and no new error state.** An inverted range renders the
filtered-empty message this slice is already adding, which is a true statement about that window. Adding
a bespoke "your dates are backwards" path would be a fifth code path on two screens for a state the user
resolves by looking at the two controls they just set. What is *not* acceptable is the current Audit behaviour of
saying "No audit events yet." to a user who inverted a range — that is INV-4, and it is why the third
message is required rather than optional.

## Guards and registries this slice touches

Added after the design pre-review (2026-09-03) found two of these missing. Each was located by grepping
the registry's **readers**, per AGENTS.md's "Writing a guard" rule — not by recall.

### `web/src/styles.toolbar.test.ts:32` — the `input[type="month"]` selector goes with the control

`describe.each(['.toolbar input[type="date"]', '.toolbar input[type="month"]'])` pins `max-width: 12rem`
for both arms against the combined CSS rule at `styles.css:1083-1085`. Before Slice B, `ExpensesPage.tsx:492`
was the **last** production `type="month"` in `web/src` — `grep -rn 'type="month"' web/src` returned exactly
that line plus the guard itself. That grep is a **pre-change inventory**: Slice B deletes the input, so
afterwards no production `type="month"` call site remains and the selector's only reference is the guard
being retired with it.

**Decision: retire the selector with its last caller.** Drop the `input[type="month"]` arm from the CSS
rule and from the guard's `describe.each`. Leaving it would ship a styling rule with zero DOM call sites
and a guard asserting it — which reads as safety for a control that no longer exists, the precise failure
mode #662 records. This adds `web/src/styles.css` and `web/src/styles.toolbar.test.ts` to the file list.

### `web/src/routes/emptyStates.guard.test.ts` — two registries, one of them changing

- `EMPTY_STATE_SITES` (line 12) contains `{ file: "ExpensesPage.tsx", key: "noExpensesMessage" }` and
  regex-matches the literal `<EmptyState … message={t("noExpensesMessage")}…>` shape. Renaming that key
  or restructuring that call site fails this test.
- `TWO_VARIANT_FILES` (line 48) pins the filtered-vs-truly-empty two-message pattern
  (`icon={FilterX}` branch, then a differently-keyed truly-empty sibling) for six screens.

Slice B turns Expenses into exactly that two-variant shape, so **both** registries need updating:
`EMPTY_STATE_SITES` gains the new filtered-empty key beside the retitled truly-empty one, and
`ExpensesPage.tsx` joins `TWO_VARIANT_FILES`. This adds `emptyStates.guard.test.ts` to the file list.

**AuditPage deliberately does not join either registry.** Its empty state is a bare
`<p className="muted">` (`AuditPage.tsx:243-249`), and #655's classification pass did not list it among
the thirteen genuine `EmptyState` sites. Slice A adds a third *message* to that paragraph, not an
`EmptyState` component — keeping Audit out of a registry it was deliberately excluded from, and keeping
this slice off a shape decision #655 already made.

### `expenses.monthTotalLabel` — the key is renamed, not just reworded

Renamed to **`expenses.periodTotalLabel`**. A key named for a month, holding a string describing a range,
is the same wrong-statement class INV-4 covers, one layer down. This cascades into
`ExpensesPage.test.tsx:632`, which names the key literally in `withOverride("expenses",
"monthTotalLabel", …)` and asserts `queryByText(/Month total:/)` — both change with it. Same for
`expenses.monthLabel`, retired outright (single production call site, `ExpensesPage.tsx:491`, verified
repo-wide), and `expenses.noExpensesMessage`, whose string stops being true for a range.

### `ExpensesPage.test.tsx` drives the month picker by label, at seven call sites

`grep -n 'getByLabelText("Month")' web/src/routes/ExpensesPage.test.tsx` → lines 282, 759, 772, 777, 831,
902, 952. Every one of them selects the control this slice deletes, so all seven change to drive the
from/to pair. That is the bulk of Slice B's test diff and it is mechanical; it is recorded here so its
size is not mistaken for scope creep at review. (These are Vitest specs, where an English label literal is
fine — the never-hardcode-English rule governs `tools/simulation/ui/`, which this slice does not touch.)

### Coverage ratchet

`web/vite.config.ts:205-208` sets a global regression floor of `lines 92 / statements 89 / functions 85 /
branches 80`. This slice adds conditional branches (date validation, the filtered-vs-empty split, the
default-range logic) and the tests covering them. **Testing a screen can lower the global branch
percentage even while adding correct coverage** — if `npm run coverage` drops below a floor, the floor is
re-baselined on the merged state in the same PR, in whichever direction it actually moved. It is not
lowered to make a red run green without saying so.

## Documentation (mandatory, same PR)

- `web/src/i18n/{en,es,tl}.ts` `help` namespace (the object opens at `en.ts:2329`): the Expenses
  paragraph `expensesRecording` at `en.ts:2820-2821` says *"The month picker shows a running total"* —
  no longer true. The Audit paragraph `auditRecordTypeFilter` at `en.ts:2854` describes the Record-type
  dropdown and should name the new date range. Find both by their **key names**, not by line number, and
  update the `es` and `tl` counterparts in the same edit.
- `specs/product/GLOSSARY.md`: the **Audit log (#93)** entry (line 471) — check whether it describes the
  filters; update only if it does. No new concept is introduced by either change, so a new glossary term
  is not expected.

## Out of scope

- **Stock is now IN scope** — see Slice C. It was recorded here as out-of-scope in the first draft and
  the owner folded it in on 2026-09-03. The other `type="date"` hits — `FlocksPage`, `InventoryPage`,
  `SalesPage`, `DailyEntryPage` — are entry-form fields, not filters, and are correctly outside the
  toolbar; they stay out.
- Any migration, index, or schema-doc change (`docs/schema/` untouched).
- Any change to `AuditEndpoints`, `ExpenseEndpoints`, their handlers, validators or repositories.
- The audit `entityType` dropdown's existing client-side-only behaviour.
- Amending #505.

## Simplicity ceiling

Two React screens each gain a `.toolbar` holding two `<input type="date">`, wired into the fetcher's
identity, plus their i18n keys in three locales, their Vitest specs, and the two help paragraphs; a third
screen has one wrapper class corrected.
**Expected: 3 route files, 3 i18n files, 3 route test files, plus the three guard/registry files the
pre-review surfaced (`styles.css`, `styles.toolbar.test.ts`, `emptyStates.guard.test.ts`) and
`vite.config.ts` only if the coverage floor actually moves. GLOSSARY if it describes filters. No
API-client, endpoint, handler, repository, migration or schema-doc file is touched.**
Slice C is one line and one test — if it grows past that, it stops and reports, because a one-word
wrapper change that turns into a refactor is a different slice wearing this one's name. No new component, no new hook, no shared abstraction extracted across the two screens — they
are similar, not the same, and #662's lesson is that a shared presentational change reaches further than
it looks. An implementation that exceeds this stops and reports.

## Invariants this slice must not break

| ID | Invariant |
|---|---|
| INV-1 | `AuditPage`'s URL remains the single source of truth for every filter it has; no filter lives in `useState`. |
| INV-2 | Every `setSearchParams` write on `AuditPage` builds a full copy of the current params. A partial object drops the sibling filters. |
| INV-3 | `fetchPage`'s dependency array names every value the request body uses. A filter absent from it reloads stale rows under a new control. |
| INV-4 | No empty-state or total string states something the current window does not support — no "this month" over a range, no "no events yet" over a filtered-out window, no total from a period the rows below do not belong to. |
| INV-5 | The Expenses total stays withheld while a reload is in flight and when `meta` is null. |
| INV-6 | `en`, `es` and `tl` stay at key parity (`catalogParity.test.ts`), and new strings ship translated in the same PR. |
