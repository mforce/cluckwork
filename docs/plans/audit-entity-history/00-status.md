# Status: Entity-scoped audit history (#493)

- Gate 1 — Product: APPROVED 2026-08-11
- Gate 2 — Architecture: APPROVED 2026-08-11
- Gate 3 — Program Design: APPROVED 2026-08-11 (2 pi review rounds, both with real product-level yield)
- Gate 4 — Slice plan: APPROVED 2026-08-11 (1 pi review round, real yield — see below)

## Pi review of Gate 3, and what changed

Full critique: /tmp/claude-1000/-home-mforce-dev-cluckwork/5b576f36-2c12-40b8-b16b-bede9a48833b/scratchpad/pi-review-gate3.txt (scratchpad). This was the sharpest round yet — 4 real bugs in the design itself, not just missing edge cases.

Acted on (folded into 03-program-design.md):
- `updateActionFilter`'s signature couldn't work as written — react-router's `setSearchParams` replaces the query string, doesn't merge, and this repo has no working merge precedent (`FeedPage.tsx` only ever reads params). Fixed: takes `searchParams` too, builds a full replacement object.
- **Stale-heading bug**: heading read `rows[0]?.entityType` without checking `usePagedList`'s `reloading` flag — during a reload (action-filter change OR switching to a different record's history), the OLD entity's type would show while new data loads. Fixed: `scopedEntityType` now gates on `!reloading`, same as the table already does.
- Metric's `StatusCode == 200` swapped for a `>= 200 && < 300` range check — the exact literal was one unrelated endpoint change away from silently breaking the metric.
- All-zeros/non-matching-but-well-formed `entityId` returns 200 with zero rows and still counts as a "successful scoped read" — documented as an accepted limitation (fixing it needs response-body inspection, disproportionate for a one-line log enrichment) and pinned by a new test rather than left implicit.
- Test plan gaps: added a real router-driven test for the `setSearchParams` URL round-trip (the mocked-`listAuditEvents` tests would pass even with the broken merge above), and a test for the stale-heading fix.

Checked and pushed back on (not acted on, with evidence):
- "No completion log event for a binding failure, so the malformed-entityId test can't assert absence" — verified false against `Program.cs`'s own #398 comment: `BindingFailureResponse` does not rethrow, so `UseSerilogRequestLogging` still logs a completion event (400, exception null). Kept the test, added "assert the event exists first" as cheap hygiene rather than because the fear was real.
- "First production caller of `entityId`, Gate 3's test plan needs a new SPA→backend integration test" — checked `AuditTests.cs`: the filter, including tenant isolation, is already integration-tested end-to-end at the real HTTP layer. Corrected the test plan's framing rather than adding a redundant test — the real gap was the frontend round-trip test above, not backend coverage.

## Pi round 2 on Gate 3 (user asked for one more), and what changed

Full critique: /tmp/claude-1000/-home-mforce-dev-cluckwork/5b576f36-2c12-40b8-b16b-bede9a48833b/scratchpad/pi-review-gate3-round2.txt (scratchpad). This round was thinner than round 1 — pi retracted one of its own points mid-argument (§2), and several others resolved to checkable facts rather than judgment calls.

Acted on:
- `updateActionFilter` had no test for the unscoped-merge path (only the scoped case was tested) — added a second URL round-trip test starting from `/audit` with no `entityId`.
- Stale-heading test needed to specify a manually-controlled/deferred promise, not a same-tick-resolving one, or the `reloading=true` window is unobservable — specified.
- "Known limitation" only named the all-zeros case; `AuditTests.cs`'s own `Viewer_NeverCrossesTenants` proves a cross-tenant read is a second, distinct 200-with-zero-rows phantom — added.
- "No new backend test needed" overclaimed: `AuditTests.cs` covers Flock and Daily Entry, not Grades/Sales/Expenses/Egg Lots — softened to "low risk given the generic query shape," not "proven."
- `HelpPage.tsx`'s own `help` i18n namespace was never named in Files — added.
- Real, previously-undesigned UX gap: a scoped view with zero events showed the generic "No audit events yet." message, which reads as "the whole log is empty." Added a distinct `scopedEmptyMessage` and a test for it.
- Softened Flow C's "exactly as unscoped" to name the specific behaviors that match, not an implied byte-identical DOM claim.
- Added a one-line note on `isLikelyGuid`'s known UX trade-off (rejects some valid-but-noncanonical hand-typed GUIDs) as a least-confident decision.
- Renamed the phantom-read test to `..._KNOWN_LIMITATION` so it reads as a pinned wart, not a spec, to a future maintainer.

Checked and pushed back on (not acted on, with evidence):
- "`common.recordHistory` per-page import still open, deferred to Gate 4 is a dodge" — checked all six pages directly: every one already calls `useTranslation("common")`. Not open; confirmed safe now rather than deferred.
- "StockPage test may need new router test infrastructure" — checked `renderWithProviders.tsx`: already wraps `MemoryRouter`. No new infrastructure.
- "The 'first production caller' correction is a rhetorical dodge — still need a genuine SPA→backend integration test" — checked this codebase's own convention: every page test (including `AuditPage.test.tsx`) mocks the API-client function at the module boundary; real browser-driven E2E lives separately in Playwright per AGENTS.md #277/#385, deliberately not in Vitest. A hybrid test would be a new pattern this repo doesn't use anywhere else — rejected as disproportionate, not silently dropped.
- "Range check should be tested against a hypothetical 201/202/204" — checked `AuditEndpoints.cs`: exactly one return path, `Results.Ok(...)`. Rejected testing a status this endpoint cannot structurally produce today.
- Gate 4 — Slice plan: in progress

## Slices
- [x] Slice 1 — tracer bullet: URL-as-source-of-truth mechanics + FlocksPage link (naive heading, no reloading gate yet) — DONE 2026-08-11. `AuditPage.tsx`, `FlocksPage.tsx`, i18n (en/es/tl), tests. 1745/1745 web tests pass, typecheck clean.
- [ ] Slice 2 — harden the scoped view: reloading gate on the heading, entity column hiding, scoped empty message
- [ ] Slice 3 — remaining links: Grades/Sales/Expenses/HistoryPage (mechanical) + StockPage (distinct label + behavior test)
- [ ] Slice 4 — Flow A′ test: switching to a different record's history while already on /audit
- [ ] Slice 5 — backend metric: Program.cs enrichment + RequestLoggingTests.cs
- [ ] Slice 6 — docs sync: GLOSSARY.md + HelpPage.tsx (es/tl translations land inline with each slice above, not here)

## Notes for a fresh session

Source issue: https://github.com/mforce/cluckwork/issues/493 — SPA-only, no domain/API/migration work (the `entityId` filter on `GET /api/v1/audit` and `listAuditEvents({ entityId })` already exist and are unused).

Related, already shipped: #494/PR #503 — added inline "created by/when, last changed by/when" `ProvenanceCell` columns to Flocks, Egg grades, Daily entries, Sales, Expenses list pages. Different feature (two-point summary, not full history), but touched the same 5 screens — reuse that row/entity-id wiring for this issue's "View history" links.

Grilled decisions locked at Gate 1 (see 01-product.md):
- Success metric: count of `GET /api/v1/audit` requests carrying `entityId` (known-zero baseline today), not self-report.
- Entity-scoped AuditPage view: action-type filter KEPT visible (reversed from an initial "disable" call after pi review — component already exists, stays useful within one record's history).
- Screen scope: 6 rows — Flocks, Daily Entries, Sales Orders, Expenses, Egg Grades, plus Egg Lots via `InventoryPage`'s `lotRows` (added after pi review flagged FIFO lot corrections as plausibly the highest-value audit target; not in #494's original 5).
- List-row links are documented as a stopgap for missing detail pages, not a final home.
- No Gate 1 mockups — reusing/extending the already-shipped AuditPage, not a new screen.

Pi (local vllm, deepseek-v4-flash) ran a contrarian review of the first Gate 1 draft — full critique at /tmp/claude-1000/-home-mforce-dev-cluckwork/5b576f36-2c12-40b8-b16b-bede9a48833b/scratchpad/pi-review-gate1.txt (scratchpad, not durable — the decisions it drove are captured above and in 01-product.md, which is what survives). It flagged the metric, the action-filter drop, the EggLot omission, and the unowned detail-page workaround; all four were acted on above. Its #71/Help-page caveat was checked and found stale — HelpPage.tsx already exists in this repo.

## Gate 2 corrections to Gate 1 (factual, from reading the actual code)

- "Daily Entries" screen is `HistoryPage.tsx` (route `/history`), not `DailyEntryPage.tsx`.
- "Egg Lots" screen is `StockPage.tsx` (route `/stock`, `lots` list with `l.id`), not `InventoryPage.tsx` — that page only has lot ids inside an adjustment dropdown, no rows to attach a link to.
- `StockPage.tsx` already has a per-lot "History" toggle for the **inventory movement ledger** (different from the audit trail) — the new link's label must not collide with it.
- The chosen success metric (entityId-request count) assumed zero-new-instrumentation; confirmed false — `UseSerilogRequestLogging` doesn't capture query strings today. **Flagged for Gate 2 approval**: add one line to `Program.cs` (tension with "SPA-only" scope), or switch the metric to a client-side click counter.

01-product.md and 02-architecture.md both updated to reflect these; 01-product.md's Screens/Metric sections carry inline "corrected during Gate 2" notes rather than pretending the first draft was already right.

## Pi review of Gate 2, and what changed

Full critique: /tmp/claude-1000/-home-mforce-dev-cluckwork/5b576f36-2c12-40b8-b16b-bede9a48833b/scratchpad/pi-review-gate2.txt (scratchpad).

Acted on (folded into 02-architecture.md and 01-product.md):
- Metric miscounted malformed/empty `entityId` (presence-only check would count 400s) — enrichment now also requires a 200 response.
- `entityType` needed for display was left half-wired via an unvalidated URL param — now read from the first response row instead; no `entityType` in the URL at all.
- Entity column redundant when scoped, StockPage "History" label collision, malformed-URL client guard, and "no back-link to the record" were all Gate-3-deferred or unstated — all decided now (see Flow steps 1, 4, 5, 8 in 02-architecture.md).
- Metric reworded from "adoption" to "successful entityId-scoped requests" — it measures requests, not distinct users, and the doc was overclaiming.
- Flagged for Gate 3: a test exercising the `entityId` path end-to-end (SPA → endpoint), since this is the filter's first production caller.

Checked and pushed back on (not acted on):
- GUID-reuse-after-delete risk to `entityId` scoping — verified against `Entity.cs`/`Flock.Create`: random `Guid.NewGuid()` PKs throughout, no ID recycling anywhere in this codebase. Not a real risk here.
- Stale-closure/state-URL-drift bug in `usePagedList` — verified against `usePagedList.ts`: the fetcher's `useCallback` deps are read fresh every render regardless of source; the specific mechanism pi described doesn't exist. (Moved `action` into the URL anyway, for bookmarkability, not because the bug was real.)

## Pi review of Gate 4 slice plan, and what changed

Full critique: /tmp/claude-1000/-home-mforce-dev-cluckwork/5b576f36-2c12-40b8-b16b-bede9a48833b/scratchpad/pi-review-gate4.txt (scratchpad).

Acted on (04-slices.md rewritten):
- **Biggest finding**: Flow A′ — clicking a different record's link while already on `/audit` — had no test anywhere in Gate 3's plan. The primary record-to-record browsing flow was untested. Added as its own slice (4).
- Original Slice 1 bundled every mechanic (URL-as-source-of-truth, the `reloading` gate, the empty-message distinction, column-hiding, the link) into one "tracer bullet" that wasn't minimal — a failure in any one piece would be indistinguishable from a failure in another. Split: Slice 1 is now the minimal wiring (accepting a known-temporary stale heading), Slice 2 hardens it (`reloading` gate, column-hiding, empty message) as its own reviewable step.
- `StockPage` was described as "mechanical repetition" alongside four genuinely-identical pages, undermining its own already-documented complexity (label collision with the existing movement-ledger toggle). Called out separately in Slice 3.
- The original plan made i18n its own trailing slice with the stated reason "depends on English being settled" — checked against this project's own standing i18n policy (translate-now, every batch ships es/tl inline) and found the reasoning was wrong: translations belong inline with each slice's English keys, not deferred. Corrected; only `GLOSSARY.md`/`HelpPage.tsx` doc-sync remains a genuine last step.
- Slice 1's "provable end to end in the real app" overstated what's actually being proven (a manual browser click-through, since the plan deliberately excludes an automated SPA→backend test) — reworded to say so plainly.
- Added an open question this doc surfaced but can't resolve: who actually consumes the `EntityScopedAuditRequest` metric — flagged for the product owner rather than silently assumed.

Checked and pushed back on (not acted on, with evidence):
- "Slice 3 (the metric) should move after slices 1-2" — checked the original numbering: it already was, at position 3 of 4, strictly after both frontend slices. Genuine misread on pi's part; clarified the "independent" wording so a future reader doesn't hit the same misread.
- "`rows[0]` might mix entities across a scoped response" — checked against Gate 2: the repository's `entityId` filter is an exact-match `WHERE EntityId = @id`; a scoped response is homogeneous by construction, not an assumption.
- "StockPage's visual-distinctness requirement needs Playwright coverage" — real limitation (Vitest can't assert visual design), but adding new browser-driven E2E infrastructure for one label is disproportionate to this ticket's SPA-only, Vitest-level scope. Accepted as a documented limitation, not built.

## Slice 1 code review (extended pi-review-every-gate to slices, per user instruction)

Round 1 (`pi-review-slice1-round1.txt`, scratchpad): 7 claims. 5 verified false or already-settled and rejected with evidence — including the "headline" one (FlocksPage test fixture `f1` "proves the link is dead in production": false, `isLikelyGuid` runs in `AuditPage` parsing the URL, not in `FlocksPage` generating it; production `Flock.Id` is always a real canonical `Guid.NewGuid()`, confirmed back in Gate 2) and `entityTypeLabel` supposedly throwing on an unknown type (false, checked `enums.ts` — falls back to `String(value)`). 2 real, both test-quality not shipped-code defects: acted on — the malformed-`entityId` test's `objectContaining({ entityId: undefined })` couldn't distinguish "explicitly undefined" from "key absent" (now reads `mock.calls` directly), and the three new i18n keys had no marker test matching this file's own established `withOverride` convention (added 3: `audit:scopedHeading`, `audit:scopedHeadingFallback`, `common:recordHistory.viewHistoryLink`).

Round 2: attempted twice, both times the remote pi endpoint (vllm) was unresponsive — even a trivial "PONG" ping timed out at 60s. Infra failure, not a completed zero-finding review, so it does **not** count toward the 2-consecutive-zero-yield stop rule. Round 2 will be retried at a later slice boundary if the service recovers; not blocking Slice 2.

Verification after round 1 fixes: 1748/1748 web tests pass, typecheck clean.
