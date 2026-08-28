# Design — #388: Worker flock read-scoping (horizontal-authorization)

**Mode:** feature (horizontal-authorization)
**Issue:** [#388](https://github.com/mforce/cluckwork/issues/388)
**Owner decision (2026-08-25):** read scoping is **intended**. Assigning a Worker to a flock scopes what they may *see*, not only what they may *write*.
**Owner decision (2026-08-27, PR #611 review round 1):** #388 preserves `main`'s farm-wide FIFO allocation for a Worker's sale confirmation — sale confirmation is SalesFlow, not AdminOnly, and a plain Worker may confirm a sale against the farm's full FIFO stock. Issue #612 owns the future configurable policy (assigned-only vs. farm-wide allocation, its warning, and any migration/settings UI). This slice does not implement that flag.

## 1. Goal

Close the gap where a Worker scoped to one flock can enumerate and read unassigned flocks. The write path already enforces `IFlockScopeGuard` on 4 handlers; the read path consults it on zero. After this slice, a scoped Worker's reads are filtered to their assigned flocks (plus farm-wide rows), and `GET /flocks/{id}` for an unassigned flock returns 404.

## 2. Settled decisions (grill + Phase 4 review, 2026-08-26)

| # | Decision | Answer |
|---|---|---|
| 1 | **Nullable `FlockId`** (expenses, inventory usage): does a scoped Worker see a row with no flock? | **Yes.** A null `FlockId` is farm-wide; farm-wide rows are visible to all scoped Workers. |
| 2 | **List filtering vs. detail refusal**: must they agree? | **Yes, symmetric.** List returns only assigned flocks; detail on an unassigned flock returns **404** (not 403). 403 leaks existence; 404 makes the id indistinguishable from a non-existent flock. |
| 3 | **Chokepoint**: EF global query filter vs. manual guard per call site? | **EF global query filter on `FlockId`, parallel to the 27 existing `AccountId` filters.** Structural: a new flock-keyed read is scoped by construction. Reuses the repo's established pattern. |
| 4 | **P1-1: Farm-wide assignment rows** (`FlockId = null` = unrestricted in the write guard). The filter must match. | **Tri-state:** `FlockScope` is `Unrestricted` (Owner/Manager, 0 assignment rows, or any farm-wide row) or `RestrictedTo(Guid[] flockIds)`. A farm-wide row = Unrestricted, matching `FlockScopeGuard` line 84. |
| 5 | **P1-2: `Flock` has no `FlockId` column.** | The filter for `Flock` is `e.Id ∈ scope` (self-reference, like `Account.AccountId == Account.Id`). `Flock` is the only entity where the filter key ≠ the `FlockId` column. |
| 6 | **P1-3: Raw-SQL / `IgnoreQueryFilters` bypass.** `EggLotRepository` uses `FromSqlInterpolated` + `IgnoreQueryFilters()` — the filter does not apply. | **Corrected 2026-08-27 (owner decision, #611 round 1):** sale-confirmation FIFO allocation (`GetAvailableFifoLockedAsync`) stays farm-wide — SalesFlow, not scoped; #612 owns the future assigned-only/farm-wide setting. Only the two AdminOnly reconciliation locks, `GetByIdsLockedAsync` and `GetByDailyEntryLockedAsync`, receive the flock-scope `WHERE` predicate. `InventoryLotRepository` is deliberately excluded: `InventoryLots` has no `FlockId` column — feed's flock linkage lives on the already-filtered `FeedUsage`/`InventoryMovement` rows, so there is no predicate to add. |
| 7 | **P1-4: Sales/Profit reports have no `FlockId`.** | **No additional work.** `ReportEndpoints.Sales`/`Profit`/`Expenses` are already `AdminOnly` (Owner + Manager) — Workers get 403 before any data query. `ExpenseEndpoints` is also `AdminOnly` (`Program.cs:366`), and Owner/Manager are unrestricted by the `FlockScope` filter (INV-2) — so the `Expense` filter does not scope any Worker's reads today. It is structural defense-in-depth for a restricted/future repository caller, not a live Worker HTTP path. No schema change, no scope creep. |

## 3. Scope-ownership map (Phase 3)

| Sibling slice | Owns which surface | Lands first | Forward-compat change |
|---|---|---|---|
| **#388 (this slice)** | Read-side flock scoping: EF query filter on 8 entities with `FlockId`, the `FlockScope` resolution service, the 4 write handlers (already done), the 18 read endpoints (now scoped by construction), GLOSSARY + SPA Help sync | **This slice** | None — the filter is self-contained; the write handlers already exist |
| **#556** (leader lease, session-pinned endpoint) | Transaction-pooled deploy support for `DurableJobWorker` advisory lock | Later (open) | None — #556 touches job scheduling, not flock reads. No overlap. |
| **#530** (epic, substantially shipped) | Multi-farm tenancy: `TenantContext`, `TenantStampInterceptor`, 27 `AccountId` query filters, sign-in by farm code | Shipped (epic open for #357, #388, #537, #556) | #388 builds on #530's tenancy infrastructure (same `AppDbContext` filter pattern) but does not change it. The flock filter is a 28th filter, parallel to the 27 tenancy filters. |

**No overlap with in-flight siblings.** #556 is about job scheduling under a transaction pool; #530's remaining items (#357, #537) are unrelated to flock reads. #388 is self-contained.

## 4. Conflict table (Phase 3)

| User-facing option / state transition | What the issue/plan requires | What the repo's canonical rule says | Which wins |
|---|---|---|---|
| Scoped Worker reads `GET /flocks` (list) | Returns only assigned flocks + farm-wide | No existing rule for flock-scope reads; tenancy filter (#530) is a different axis | **Plan** — the issue is the owner decision; the tenancy filter is orthogonal |
| Scoped Worker reads `GET /flocks/{id}` (unassigned) | Returns 404 | `GetFlock` already returns 404 for not-found (`FlockEndpoints.cs:35`) | **Plan** — same 404 path, no new status code |
| Scoped Worker reads farm-wide expense (`FlockId = null`) | Visible | No existing rule | **Plan** — decision 1 (grill) |
| Owner/Manager reads any flock | Unrestricted | `FlockScopeGuard` lets Owner/Manager past (`UserRoleAssignmentRepository.cs:34`) | **Repo rule** — the filter must skip Owner/Manager |
| Worker with NO assignment rows reads any flock | Unrestricted (grandfathered #73) | `FlockScopeGuard` returns `Result.Success()` for 0 assignment rows (`UserRoleAssignmentRepository.cs:80`) | **Repo rule** — the filter must skip unassigned workers |
| Worker with a **farm-wide** assignment row (`FlockId = null`) reads any flock | Unrestricted | `FlockScopeGuard` returns `Result.Success()` for a farm-wide row (`UserRoleAssignmentRepository.cs:84`: `a.FlockId == null || a.FlockId == flockId`) | **Repo rule** — the filter must treat a farm-wide row as Unrestricted (P1-1) |
| Worker reads `GET /reports/sales` (or `/profit`, `/expenses`) | 403 (AdminOnly) | `AuthPolicies.AdminOnly` = Owner + Manager (`AuthPolicies.cs`) | **Repo rule** — the authorization policy blocks Workers before any data query; no flock scoping needed (P1-4) |
| Worker reads `GET /reports/production` | Scoped by filter | `Production` is open to all signed-in users; reads `Flocks` + `BirdMovements` (both filtered) | **Plan** — the filter scopes it by construction |
| New migration required? | No — EF global query filters are runtime metadata, not a schema change | #407: one migration per change; `InitialCreate` is frozen; #417: `docs/schema/` regenerated with every migration | **Repo rule, satisfied by construction** — no model-snapshot change, no `docs/schema/` regeneration, `InitialCreate` untouched (see §8) |

**No conflicts with the repo's canonical rules.** The plan is consistent with #530's tenancy pattern, #407's migration rule, #417's schema-docs rule, and the existing `FlockScopeGuard` semantics.

## 5. Invariants and enforcement sites (Phase 3)

### INV-1: A scoped Worker's read of a flock-keyed entity returns only rows for assigned flocks (or farm-wide rows where `FlockId` is nullable).

- **Establishes:** `FlockScope` resolution service (new) — resolves the current user's flock scope per request, parallel to `TenantContext`. Tri-state: `Unrestricted` or `RestrictedTo(Guid[] flockIds)`.
- **Checks:** EF global query filter on 8 entities (new) — `Flock`, `DailyEntry`, `EggLot`, `BirdMovement`, `InventoryMovement`, `FeedUsage`, `WaterUsage`, `Expense`.
- **Clears:** Nothing clears it; the filter is per-request, resolved from `UserRoleAssignment` rows (a DB read) by the middleware.
- **Enforcement sites (all mechanical, enumerated from the domain):**
  - `Flock` (**self-reference:** `e.Id ∈ scope` — `Flock` has no `FlockId` column; P1-2) — `AppDbContext.cs` (new filter)
  - `DailyEntry` (non-null `FlockId`) — `AppDbContext.cs` (new filter)
  - `EggLot` (non-null `FlockId`) — `AppDbContext.cs` (new filter)
  - `BirdMovement` (non-null `FlockId`) — `AppDbContext.cs` (new filter)
  - `InventoryMovement` (**nullable** `FlockId`) — `AppDbContext.cs` (new filter; null passes)
  - `FeedUsage` (non-null `FlockId`) — `AppDbContext.cs` (new filter)
  - `WaterUsage` (non-null `FlockId`) — `AppDbContext.cs` (new filter)
  - `Expense` (**nullable** `FlockId`) — `AppDbContext.cs` (new filter; null passes)
- **Raw-SQL enforcement sites (P1-3, Option A):** Walk every `FromSql*`/`IgnoreQueryFilters` call site on the 8 filtered entities:
  - `EggLotRepository.cs:38,66,85` (`FromSqlInterpolated` + `IgnoreQueryFilters`) — **corrected 2026-08-27 (owner decision, #611 round 1):** `:66` (`GetByIdsLockedAsync`) and `:85` (`GetByDailyEntryLockedAsync`), both AdminOnly reconciliation locks, carry the flock-scope `WHERE` predicate. Current production callers of both are AdminOnly (Owner/Manager, unrestricted by `FlockScope`), so the predicate scopes nobody today — it is defense-in-depth for a future non-elevated caller, not an active Worker restriction. `:38` (`GetAvailableFifoLockedAsync`) does **not** carry the predicate — sale confirmation is SalesFlow, and a plain Worker's FIFO allocation stays farm-wide to preserve `main`'s behavior; #612 owns the future assigned-only/farm-wide setting.
  - `EggLotRepository.cs:102` (`GetStockByGradeAsync`) — **LINQ, not raw SQL**; the query filter applies by construction. No change needed (codex v2 correction).
  - `InventoryLotRepository.cs` — **deliberately excluded, not a gap.** `InventoryLots` has no `FlockId` column; feed's flock linkage lives on the already-filtered `FeedUsage`/`InventoryMovement` rows, so there is no predicate to add here.
  - `SalesOrderRepository.cs:30` (`GetByIdLockedAsync`) — raw SQL on `SalesOrders`. `SalesOrder` has **no `FlockId`** (not in the 8-entity filter set). No flock predicate needed; document why.
  - `DailyEntryLockSweep.cs:33` (`IgnoreQueryFilters`) — background job, not a user read. Confirmed: it resolves `TenantContext` but not a user; document that it bypasses the flock filter by design (jobs are account-level).
- **Set difference (suspects):** The LINQ-path reads across 8 endpoint classes (`FlockEndpoints`, `DailyEntryEndpoints`, `WaterUsageEndpoints`, `InventoryEndpoints`, `ExpenseEndpoints`, `StockEndpoints`, `ReportEndpoints`, `ExportEndpoints`) are scoped **by construction** via the query filter. The `AdminOnly` reports (`Sales`, `Profit`, `Expenses`) are scoped by **authorization** (403 for Workers), not by the filter. The 4 write handlers already call `IFlockScopeGuard.CheckAsync` manually; the query filter is a second layer (defense in depth), not a replacement.
- **Mirror pass (who clears / who sets):** The `UserRoleAssignment` rows are set by `AssignFlockHandler` and `UserEndpoints` (managing assignments). No code clears them except deletion. The middleware resolves the scope once per request (a DB read); the filter reads the resolved scope (no per-query DB read).

### INV-2: Owner and Manager roles are unrestricted (read and write).

- **Establishes:** JWT `roles` claim (`ICurrentUser.Roles`).
- **Checks:** `FlockScopeGuard` (`UserRoleAssignmentRepository.cs:34`) — returns `Result.Success()` for Owner/Manager. The new query filter must replicate this: skip the filter when `user.Roles.Contains(Owner)` or `user.Roles.Contains(Manager)`.
- **Clears:** Role changes (via `UserEndpoints`).
- **Enforcement sites:** `FlockScopeGuard` (write), new `FlockScope` filter (read). Both must agree.
- **Set difference:** The 4 write handlers call `FlockScopeGuard` directly; the query filter is independent. A new write handler that forgets `FlockScopeGuard` is still scoped by the query filter (second layer). A new read endpoint is scoped by the query filter by construction.

### INV-3: A Worker with NO assignment rows OR a farm-wide assignment row is unrestricted (grandfathered #73 + P1-1).

- **Establishes:** `UserRoleAssignment` table (empty for the user, OR any row with `FlockId = null`).
- **Checks:** `FlockScopeGuard` — returns `Result.Success()` when `assignments.Count == 0` (line 80) OR when any assignment has `FlockId == null` (line 84: `a.FlockId == null || a.FlockId == flockId`). The new `FlockScope` must replicate this: `Unrestricted` when 0 rows OR any farm-wide row.
- **Clears:** First flock-specific assignment narrows them (existing behavior).
- **Enforcement sites:** `FlockScopeGuard` (write), new `FlockScope` filter (read). Both must agree.
- **Set difference:** Same as INV-2.

### INV-4: The filter is per-request, resolved by the middleware from a DB read, not cached per-query.

- **Establishes:** `FlockScopeResolutionMiddleware` (new) — resolves `FlockScope` per request from `UserRoleAssignment` rows (a DB read), parallel to `TenantResolutionMiddleware` (which resolves from a JWT claim — no I/O). The flock resolution is a DB read, not a claim parse; this is a different resolution contract than tenancy.
- **Checks:** `FlockScope` service (new) — single-assignment, like `TenantContext`. A differing re-resolve throws.
- **Clears:** End of request scope.
- **Enforcement sites:** `FlockScope` service (new), registered as scoped DI (parallel to `TenantContext`). `FlockScope` is a **constructor parameter of `AppDbContext`** (not a lazy scoped service) — the filter lambda closes over the constructor field, the same way the 27 tenancy filters close over `TenantContext`.
- **Set difference:** The query filter reads `FlockScope` (the constructor field), not a service resolved at query time. The middleware populates `FlockScope` once per request; the filter reads the populated value on every query (no per-query DB read).
- **Unresolved-user behavior:** `FlockScopeGuard` fails **open** for `!user.IsResolved` (line 70, #500 comment). The query filter must match: an unresolved user (seeders, one-shot verbs, background jobs) is Unrestricted. The seeders' existing `IgnoreQueryFilters()` calls stay — they resolve `TenantContext` but not `FlockScope`.

## 6. Seam and interface

**Seam:** `AppDbContext.OnModelCreating` — where the 27 existing `AccountId` query filters are registered. The 8 new flock-scope filters join them in the same `IModelBuilder` call. `FlockScope` is a **constructor parameter of `AppDbContext`** (like `TenantContext`), so the filter lambda closes over the constructor field.

**Interface:**
- `FlockScope` (new, `src/Cluckwork.Infrastructure/Persistence/FlockScope.cs`) — parallel to `TenantContext`. Tri-state: `IsUnrestricted: bool` + `AssignedFlockIds: IReadOnlyCollection<Guid>` (immutable defensive copy; empty when unrestricted). Single-assignment. Resolved by the middleware from `UserRoleAssignment` rows (a DB read).
- `FlockScopeResolutionMiddleware` (new, `src/Cluckwork.Api/Middleware/FlockScopeResolutionMiddleware.cs`) — resolves `FlockScope` per request from `UserRoleAssignment` rows, parallel to `TenantResolutionMiddleware`. Runs after `TenantResolutionMiddleware`. Skips the DB read for Owner/Manager (role check from `ICurrentUser`, no I/O).
- `IFlockScopeGuard` (existing, `src/Cluckwork.Application/Common/IFlockScopeGuard.cs`) — the write-side guard. **Distinct from `FlockScope`** (the read-side scope). The guard is async and per-flock; the scope is a per-request set-valued state. The filter must NOT reuse the guard.

**Fit with layering:** `FlockScope` lives in `Infrastructure/Persistence` (same as `TenantContext`); the middleware lives in `Api/Middleware` (same as `TenantResolutionMiddleware`). Dependencies point inward: Api → Infrastructure → Domain. No new port in `Application/Common` — the filter reads the concrete `FlockScope` type (same as how the tenancy filter reads the concrete `TenantContext` type).

## 7. File allow-list (derived from the enumeration above)

**New files:**
- `src/Cluckwork.Infrastructure/Persistence/FlockScope.cs`
- `src/Cluckwork.Api/Middleware/FlockScopeResolutionMiddleware.cs`

**Modified files:**
- `src/Cluckwork.Infrastructure/Persistence/AppDbContext.cs` (add `FlockScope` constructor parameter + 8 query filters)
- `src/Cluckwork.Infrastructure/Persistence/AppDbContextDesignTimeFactory.cs` (add `FlockScope` to manual `AppDbContext` construction at line 85)
- `src/Cluckwork.Api/Program.cs` (register `FlockScope` as scoped, add middleware)
- `src/Cluckwork.Api/Hosting/CluckworkFeatureServiceCollectionExtensions.cs` (register `FlockScope` as scoped)
- `src/Cluckwork.Infrastructure/Repositories/EggLotRepository.cs` (add flock-scope predicates to raw-SQL call sites at lines 66, 85 — P1-3; line 38 (`GetAvailableFifoLockedAsync`) stays farm-wide, corrected 2026-08-27 per #611 round 1 / #612)
- `src/Cluckwork.Infrastructure/Repositories/ExportQueries.cs` (add `FlockScope` to manual `AppDbContext` construction at line 76)
- `tests/**` (update direct `AppDbContext` constructions in test factories)
- `specs/product/GLOSSARY.md` (flock scoping definition)
- `web/src/...` (SPA Help page — flock scoping, all locales)
- `tools/simulation/ui/...` (#277 Playwright E2E spec — Worker persona read assertions)

**Do-not-touch:**
- `src/Cluckwork.Infrastructure/Repositories/InventoryLotRepository.cs` — deliberate exclusion, not an oversight: `InventoryLots` has no `FlockId` column; feed's flock linkage lives on the already-filtered `FeedUsage`/`InventoryMovement` rows, so no predicate exists to add.
- `src/Cluckwork.Infrastructure/Repositories/UserRoleAssignmentRepository.cs` (the existing `FlockScopeGuard` — the write path is already correct; the query filter is a second layer, not a replacement)
- `src/Cluckwork.Infrastructure/Persistence/TenantContext.cs` (tenancy is a different axis; do not conflate)
- `src/Cluckwork.Infrastructure/Persistence/Interceptors/TenantStampInterceptor.cs` (tenancy write guard; do not conflate)
- `src/Cluckwork.Domain/**` (no domain changes — the filter is a query-time concern, not a domain invariant)

## 8. Migration

**No migration needed.** EF global query filters are runtime metadata — they do not appear in the model snapshot and produce no relational schema change. The 27 existing `AccountId` filters have no migration; the 8 new flock-scope filters follow the same pattern. No `docs/schema/` regeneration needed (no schema change). Per #407, `InitialCreate` remains untouched.

## 9. Tests

- **Integration tests** (Testcontainers, real Postgres):
  - Scoped Worker reads `GET /flocks` → sees only assigned flocks (not unassigned).
  - Scoped Worker reads `GET /flocks/{unassigned-id}` → 404 (not 403, not 200).
  - Scoped Worker reads `GET /flocks/{assigned-id}` → 200 (positive control).
  - **Corrected — no dedicated shipped assertion for the `flockId` query-parameter path.** The shipped coverage is the Worker *list* filter: `FlockScopeTests.ScopedWorker_FlocksList_SeesOnlyAssignedFlock` and the child-row fact (§ below) prove `GET /daily-entries` returns only the assigned flock's rows by construction (the combined query filter, not a `flockId`-parameter-specific code path). No separate fact pins `GET /daily-entries?flockId={unassigned}` as its own query-parameter case.
  - Scoped Worker reads a farm-wide expense (`FlockId = null`) → visible (decision 1).
  - Worker with a **farm-wide assignment row** (`FlockId = null`) reads `GET /flocks` → sees all flocks (P1-1 tri-state).
  - Worker with **0 assignment rows** reads `GET /flocks` → sees all flocks (grandfathered #73).
  - Owner reads `GET /flocks` → sees all flocks (unrestricted).
  - Manager reads `GET /flocks` → sees all flocks (unrestricted).
  - Scoped Worker reads `GET /reports/sales` → 403 (AdminOnly; P1-4 — authorization, not filter).
  - Scoped Worker's AdminOnly reconciliation reads (`GetByIdsLockedAsync`/`GetByDailyEntryLockedAsync`) are flock-scoped — raw-SQL path, `EggLotRepository` lines 66/85. **Corrected 2026-08-27 (#611 round 1):** `GetAvailableFifoLockedAsync` (line 38, sale confirmation) stays farm-wide by owner decision (#612), not scoped.
  - Unresolved user (seeder/one-shot verb) reads a flock-keyed entity → sees all flocks (INV-4 — unrestricted).
- **Concurrency evidence (corrected 2026-08-27, PR #611 review round 2): no separate cross-transaction mid-request test ships.** What actually ships:
  - `Resolve_DifferingScope_ThrowsReassignmentException` pins single-assignment/no mid-request scope replacement (INV-4).
  - `RoleMatrixTests.FlockScope_CoversFeed_SubmitOfUnassignedDraft_AndMismatchedUnassign` removes an assignment and proves the *next* HTTP request observes the changed scope.
  - Do not claim a test that pins a scope read racing a concurrent assignment write mid-request; none ships.
- **Corrected — `RoleMatrixTests` did not gain a read row-count assertion.** The row-count coverage for scoped reads lives in `FlockScopeTests.ScopedWorker_FlocksList_SeesOnlyAssignedFlock` (Worker sees exactly the assigned flock, not the unassigned one) and the middleware resolution tests in `FlockScopeMiddlewareTests`. `RoleMatrixTests` was not the file that gained this assertion.
- **Mutation checks:**
  - Remove the `Flock` filter → the "scoped Worker reads `GET /flocks` → sees only assigned" test must RED (sees unassigned flocks).
  - Remove the `Expense` filter → `ExpenseFilter_FarmWideVisible_UnassignedExcluded` must RED (the unassigned-flock expense becomes visible).
  - Remove the raw-SQL flock predicate in `EggLotRepository` (line 66 or 85) → `EggLotRawSqlPaths_HonorTheirDistinctSalesAndAdminContracts` must RED on the corresponding AdminOnly assertion. Re-adding the predicate to line 38 (`GetAvailableFifoLockedAsync`) must also RED it — that path is farm-wide by owner decision (#612), not scoped.
  - Restore, rebuild, confirm green.

## 10. Documentation surfaces

- **GLOSSARY.md:** add "flock scoping" definition (a Worker's reads are limited to their assigned flocks + farm-wide rows; Owner/Manager unrestricted; 0-assignment Workers unrestricted).
- **SPA Help page:** add flock scoping to the in-app glossary (all locales).
- **PR description:** state the owner decision (read scoping is intended, #388, 2026-08-25) and the three settled decisions.
