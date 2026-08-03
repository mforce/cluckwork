# Daily Entry Reconciliation and Saleable Quality Eggs Implementation Plan

Tracks #394 and #396. Implementation must follow the non-CI writer rule in
PR #395.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Require official Daily Entry reconciliation while allowing independently saleable Cracked and Dirty stock with farm-defined product prices.

**Architecture:** Store a stable Daily Entry kind on egg grades and snapshot the resolved Cracked/Dirty grade ids on the official Daily Entry. Manual grades always reconcile against the non-condition remainder; submission and adjustment add or reconcile all snapshot-backed lots atomically. Product prices remain product-owned and are never derived from another grade.

**Tech Stack:** .NET 10/C#, EF Core/Postgres migrations, React 19/Vite/TypeScript, FluentValidation, Vitest, xUnit/Testcontainers, Playwright simulation harness.

## Global Constraints

- Preserve `DailyEntry.Version++` on every mutation and add parallel-race coverage for new aggregate mutation paths.
- Drafts accept incomplete manual grade lines; submit and adjustment require exact reconciliation.
- `Discarded` stays non-saleable; Cracked and Dirty are saleable only through their snapshotted configured grade.
- **Add no second migration.** `Persistence/Migrations/` holds exactly one, `InitialCreate` (#245), and this application has never been deployed — a virgin database is the only starting state anywhere. Hand-carry the columns, the partial unique index, and the seed changes into `InitialCreate` itself, and hand-edit rather than regenerate: it carries four un-regenerable `lower("Name")` expression indexes and 21 rows of `WHERE NOT EXISTS`-guarded reference data that `dotnet ef migrations add` would silently drop. There is correspondingly **no existing farm whose saleability could be modified**, so the "leave existing farms untouched" half of this constraint describes data that cannot exist — dropping it avoids shipping the dead-backfill pattern the #245 squash was performed to remove.
- Update every non-CI writer and fixture per PR #395, including simulation manifest verification.
- Update `HelpPage.tsx` and `specs/product/GLOSSARY.md` with the behavior change.

---

### Task 1: Persist quality-condition identity and official-entry snapshots

**Files:**
- Modify: `src/Cluckwork.Domain/Eggs/EggGrade.cs`, `src/Cluckwork.Domain/Eggs/DailyEntry.cs`
- Modify: EF configurations and the hand-maintained `InitialCreate` (hand-edited, never regenerated) — **no second migration**, see Global Constraints
- Modify: base-reference seed SQL and migration security/base-data tests
- Modify: `src/Cluckwork.Infrastructure/Repositories/ExportQueries.cs` — add the two quality snapshot ids to the `daily-entries` projection and `DailyEntryKind` to the `egg-grades` projection, so a full account export can still reconstruct which grade a condition counter represented
- Test: Egg-grade and DailyEntry domain tests; migration tests; `ExportTests.cs` regression coverage for both changed projections

- [ ] Write failing tests for immutable `DailyEntryKind`, one Cracked/Dirty grade per farm, null/non-null quality snapshots on official entries, and an account export missing both additions.
- [ ] Run the focused tests and verify the model cannot yet distinguish a renamed Cracked/Dirty grade from any other quality grade, and that a full account export cannot yet reconstruct which grade a condition counter represented.
- [ ] Add the enum, partial uniqueness constraint, and official-entry snapshots, carried into `InitialCreate` by hand — columns, index, and the seed defaults together. Confirm afterwards that the four `lower("Name")` expression indexes and all 21 guarded reference-data rows survive, and that `MigrationSecurityReviewTests` and `BaseReferenceDataMigrationTests` still pass; treat a failure there as having destroyed something, not as a test to adjust.
- [ ] Add the quality snapshot ids to the `daily-entries` export projection and `DailyEntryKind` to the `egg-grades` export projection.
- [ ] Verify new-base defaults are saleable, existing official records remain non-reclassified, migration security tests still pass, and both export regression tests are green.

### Task 2: Enforce exact reconciliation and atomically produce quality lots

**Files:**
- Modify: `DailyEntry`, `RecordDailyEntryHandler`, `SubmitDailyEntryHandler`, `AdjustDailyEntryHandler`, grade repositories/commands as required
- Test: Domain, record, submit, adjustment, stock, report, and concurrency integration tests

- [ ] Write failing domain/API tests for ungraded and partially graded official entries, zero remainder, each saleable quality counter, each non-saleable quality counter, an **inactive-but-saleable** condition grade (must snapshot null — `EggGrade.Deactivate()` leaves `IsSaleable` set, so this is the case a saleability-only rule wrongly accepts), a zero-count day for an otherwise-saleable quality counter (snapshot recorded, no lot — `EggLot.Create` rejects zero), and an adjustment that preserves its original snapshot.
- [ ] Write failing API tests submitting a **condition-kind grade id as a manual line** — on both record and adjust — and assert it is refused. Excluding condition grades from the manual pane is a UI affordance, not the enforcement: `RecordDailyEntryHandler` gates manual-line eligibility on `IsSaleable` **alone**, with no `GradeType`/kind restriction, and this feature makes Cracked and Dirty saleable by default. So a direct or stale API client can name a Cracked id as a manual line, the exact-total check passes (it is only a sum), and submission then creates **both** that manual lot and the counter-backed quality lot for the same grade — double-counting the day's stock and breaking the one-lot-per-grade assumption reconciliation depends on. The rejection belongs at the handler/aggregate boundary where the UI cannot be bypassed.
- [ ] Run them red; assert status/version/lots remain unchanged on refusal.
- [ ] Implement `manualGrades == total - cracked - dirty - discarded` at the aggregate boundary, reject any manual line naming a condition-kind grade, resolve snapshots only on first official submission and only for a grade that is both `Active` and `IsSaleable`, and create/reconcile quality lots — skipping lot creation for a zero counter while still recording its snapshot — in the existing transactions.
- [ ] Update report and stock projections to count only snapshot-backed quality stock as marketable; verify no historical row changes its meaning.
- [ ] Run targeted integration suites plus parallel submit/adjust tests.

### Task 3: Extend the API contract and align Daily Entry and adjustment UI

**Files:**
- Modify: `src/Cluckwork.Api/Endpoints/DailyEntries/DailyEntryEndpoints.cs` — `DailyEntryResponse` gains the two quality snapshot ids
- Modify: `src/Cluckwork.Api/Endpoints/EggGrades/EggGradeEndpoints.cs` — `EggGradeResponse` gains `DailyEntryKind`
- Modify: `web/src/api/cluckwork.ts` — mirror both DTO changes in the `DailyEntry` and `EggGrade` TypeScript contracts
- Modify: `web/src/routes/DailyEntryPage.tsx`, `web/src/routes/HistoryPage.tsx`, shared dialog/styles if needed
- Modify: `web/src/i18n/en.ts`, `web/src/i18n/es.ts`, `web/src/i18n/tl.ts`
- Test: API integration coverage in `SubmitDailyEntryTests.cs`/`DailyEntryAdjustTests.cs` and `EggGradeManagementTests.cs` for the two new response fields; `DailyEntryPage.test.tsx`, `HistoryPage.test.tsx`, catalog parity/typecheck

- [ ] Write failing API integration tests asserting `DailyEntryResponse` carries both quality snapshot ids and `EggGradeResponse` carries `DailyEntryKind`, plus failing UI tests that exclude condition grades from manual entry, show quality counts in the marketable summary, disable official saves for an under/over grade total, and render the adjustment dialog as the two-pane Daily Entry layout.
- [ ] Run focused API and Vitest tests red.
- [ ] Add both response fields and their TypeScript contract mirrors, then implement shared calculations and the adjustment layout without changing draft-save behavior, conflict rebind, reason requirement, or accessible live feedback. The History UI must derive its reconciliation feedback from the entry's own snapshots, never the current egg-grade catalog, so a later saleability change cannot retroactively rewrite how a past entry reads.
- [ ] Run API integration tests, page tests, i18n catalog parity, and typecheck.

### Task 4: Preserve sales configuration and update non-CI writers

**Files:**
- Modify: product/grade UI guidance and relevant API tests
- Modify: `DemoDataSeeder`, `SimulationDataSeeder`, `tools/simulation/k6/bundles.js`, Manager/Worker Playwright specs, simulation manifest/verification scripts
- Test: seeder/manifest tests and simulation-harness self-check

- [ ] Write failing coverage proving a Cracked/Dirty product maps only to its saleable quality lot and can retain an independently configured default price.
- [ ] Update all seed and browser writers to satisfy exact reconciliation and include quality lots where intended.
- [ ] Recompute the simulation fixture manifest and add/update self-checks so a stale writer or fixture fails closed.
- [ ] Execute targeted seed, k6 bundle, Playwright, and harness verification commands documented by PR #395.

### Task 5: Document the operating rule

**Files:**
- Modify: `web/src/routes/HelpPage.tsx`, i18n help catalogs, `specs/product/GLOSSARY.md`, `specs/product/specs.md`
- Test: Help-page tests and catalog parity

- [ ] Explain that drafts are flexible but official saves reconcile exactly, and that Cracked/Dirty quality stock is farm-configurable and separately priced through products.
- [ ] Document the no-retroactive-reclassification rule.
- [ ] Run Help, i18n, backend, and SPA verification suites before review.
