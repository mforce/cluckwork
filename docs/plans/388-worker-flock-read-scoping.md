# #388 Worker Flock Read-Scoping — Implementation Plan

> **For agentic workers:** Execute this plan task-by-task. Steps use checkbox (`- [ ]`) syntax. Each task ends with an independently testable deliverable. **Transcribe code blocks verbatim.** Do not reformat, rename, or "improve" them.

**Goal:** Close the gap where a Worker scoped to one flock can enumerate and read unassigned flocks. Add an EF global query filter (parallel to the 27 tenancy filters) that scopes a Worker's reads to their assigned flocks + farm-wide rows.

**Architecture:** A new `FlockScope` scoped service (tri-state: `Unrestricted` / `RestrictedTo(Guid[])`) is resolved per-request by a new middleware from `UserRoleAssignment` rows. `FlockScope` is a constructor parameter of `AppDbContext` (like `TenantContext`), and 8 new query filters close over it. Raw-SQL call sites that bypass the filter get explicit flock-scope predicates where a flock column exists on the queried rows.

**Tech Stack:** .NET 10, EF Core 10, PostgreSQL, xUnit, Testcontainers.

**Design:** `docs/designs/388-worker-flock-read-scoping.md` (approved 2026-08-25). Invariant IDs INV-1..INV-4 are stable — never renumber.

**Owner decision (2026-08-27, PR #611 review round 1):** #388 preserves `main`'s farm-wide FIFO allocation for a Worker's sale confirmation (SalesFlow, not AdminOnly). Issue #612 owns the future configurable assigned-only/farm-wide policy. Task 3 below is corrected in place to reflect this — see the "Superseded" note ahead of its scope-walk table.

## Global Constraints

- **No domain changes.** The filter is a query-time concern, not a domain invariant. `src/Cluckwork.Domain/**` is do-not-touch.
- **`FlockScope` is a constructor parameter of `AppDbContext`** (not a lazy scoped service). The filter lambda closes over the constructor field, the same way the 27 tenancy filters close over `TenantContext`.
- **`AssignedFlockIds` is `IReadOnlyCollection<Guid>`** (immutable defensive copy). Single-assignment: a differing re-resolve throws.
- **Tri-state:** `IsUnrestricted: bool` + `AssignedFlockIds: IReadOnlyCollection<Guid>`. Unrestricted when: Owner/Manager role, 0 assignment rows, OR any farm-wide row (`FlockId = null`). This matches `FlockScopeGuard` lines 80 and 84.
- **`Flock` filter is `e.Id ∈ scope`** (self-reference — `Flock` has no `FlockId` column). The other 7 entities use `e.FlockId ∈ scope` (nullable `FlockId` on `InventoryMovement`/`Expense` passes `null` — farm-wide).
- **No migration.** Query filters are runtime metadata, not in the model snapshot. No `docs/schema/` regeneration. `InitialCreate` untouched (#407).
- **No new dependency.** No package add → no lock-file churn (#146).
- **The pre-commit hook does NOT build this tree.** `.githooks/pre-commit` runs only `Domain.Tests` + `Application.Tests` (when `.cs` staged) and `npm run typecheck` (when `web/` staged). `Infrastructure`/`Api` compile only in the `dotnet build` step of each task. Build before every commit.
- **Full suite in the foreground.** Report the final summary line verbatim.
- **CI gates** (from `.github/workflows/ci.yml`): `dotnet build Cluckwork.sln --configuration Release --no-restore`, `dotnet test Cluckwork.sln --configuration Release --no-build --verbosity normal`, `tools/schema-docs/generate.sh --check`, `npm run test:coverage`, `npm run build`, `npm run verify:sw`.

**Read-only surfaces a scoped Worker gets 403 on (AdminOnly — no filter needed there, documented for the review loop):** Sales/Profit/Expense reports (`ReportEndpoints.cs:28,33,38`), **Expenses + ExpenseCategories** (`Program.cs:359-367`, both groups AdminOnly — money is admin end to end, #87), Catalog, EggGrades, Flocks create/deactivate/archive, DailyEntry adjust/void, WaterUsage update, sale void, payment void, Audit, Users, **Export** (`Program.cs:445-448`, AdminOnly group). **Correction (owner decision 2026-08-27, #611 round 1; authorization map corrected 2026-08-27, #611 round 2; corrected again #611 clean-round-1):** sale **confirm** is SalesFlow — a plain Worker may confirm a sale and allocate against farm-wide FIFO stock, not only assigned-flock stock (#612 owns the future assigned-only/farm-wide setting). Sale **void** is AdminOnly, distinct from confirm. Payment views (`GET .../payments`, `POST .../payments` to record one) are SalesAccess (Owner/Manager/Sales) and exclude plain Workers; payment void is AdminOnly. Stock summary/lots/movement-ledger reads (`StockEndpoints.cs`) are default-authenticated and Worker-reachable; only the stock movement write is AdminOnly. Inventory item/lot/movement/usage reads (`InventoryEndpoints.cs`) are default-authenticated and Worker-reachable; inventory configuration and corrections are AdminOnly. The Worker's reachable READ surface — and the surface the filters must protect for a Worker — is: Flocks list/detail (`/api/v1/flocks`, default policy), DailyEntries list/get, BirdMovements list (`/api/v1/flocks/{id}/movements`), WaterUsage list, FeedUsage list, Stock summary/lots/movement ledger, Inventory item/lot/movement/usage reads, and Sale *view* endpoints (SalesFlow — Payment views are SalesAccess and out of a plain Worker's reach). The Expense filter still ships (INV-1 says every scoped entity is filtered — it protects the Manager-less read paths and is the row the mutation table needs); there is simply no Worker-reachable HTTP surface to exercise it, so its test goes through the repository layer, not an endpoint.

---

### Task 1: `FlockScope` service + middleware (no filters yet)

**Files:**
- Create: `src/Cluckwork.Infrastructure/Persistence/FlockScope.cs`
- Create: `src/Cluckwork.Api/Middleware/FlockScopeResolutionMiddleware.cs`
- Modify: `src/Cluckwork.Api/Program.cs` (add middleware after `TenantResolutionMiddleware`)
- Modify: `src/Cluckwork.Api/Hosting/CluckworkFeatureServiceCollectionExtensions.cs` (register `FlockScope` as scoped)
- Modify: `tests/Cluckwork.Api.IntegrationTests/CredentialEpochMiddlewareOrderTests.cs` (pinned middleware sequence)
- Test: `tests/Cluckwork.Api.IntegrationTests/FlockScopeMiddlewareTests.cs` (new — middleware unit behavior only; **no scoping assertions — those need Task 2's filters and live in Task 2**)

**Interfaces:**
- Consumes: `ICurrentUser` (role + resolved state), `AppDbContext` (`UserRoleAssignment` rows)
- Produces: `FlockScope` with `bool IsUnrestricted`, `IReadOnlyCollection<Guid> AssignedFlockIds`, `bool IsResolved`, and `void Resolve(bool unrestricted, IReadOnlyCollection<Guid> flockIds)`. Single-assignment: a differing re-resolve throws `FlockScopeReassignmentException`.

**Independence note:** Task 1 ships NO scoping. Every test in this task asserts middleware behavior only (resolve outcomes via a probe endpoint-free path), so the task is independently green. The scoping tests (flock list/detail) are written in Task 2 together with the filters that make them pass — an increment that ships a failing suite is a red tree, and the pre-commit hook will not catch it (it does not build `Infrastructure`/`Api`).

- [ ] **Step 1: Write `FlockScope.cs`**

Create `src/Cluckwork.Infrastructure/Persistence/FlockScope.cs`:

```csharp
namespace Cluckwork.Infrastructure.Persistence;

using System.Collections.Generic;

// Per-request flock-scope resolution (#388). Parallel to TenantContext (#546).
// Tri-state: Unrestricted (Owner/Manager, 0 assignment rows, or any farm-wide row)
// or RestrictedTo(assigned flock ids). Single-assignment: a differing re-resolve
// throws FlockScopeReassignmentException.
//
// Resolved by FlockScopeResolutionMiddleware from UserRoleAssignment rows (a DB
// read), NOT from a JWT claim. This is a different resolution contract than
// TenantContext (which resolves from a claim, no I/O).
//
// The query filter reads this (a constructor field of AppDbContext), not a
// service resolved at query time. The middleware populates it once per request;
// the filter reads the populated value on every query (no per-query DB read).
public sealed class FlockScope
{
    // Unresolved contexts (design-time, seeders, one-shot verbs, hand-built
    // tests) retain the existing account-wide read behavior. HTTP middleware
    // explicitly resolves every request. Matches FlockScopeGuard line 70.
    public bool IsUnrestricted { get; private set; } = true;
    public IReadOnlyCollection<Guid> AssignedFlockIds { get; private set; } = [];
    public bool IsResolved { get; private set; }

    public void Resolve(bool unrestricted, IReadOnlyCollection<Guid> flockIds)
    {
        if (IsResolved)
        {
            // Same scope: a deliberate no-op, NOT an error (mirrors TenantContext).
            // (IReadOnlyCollection<Guid> has no SetEquals — compare as sets manually.)
            if (IsUnrestricted == unrestricted &&
                flockIds.Count == AssignedFlockIds.Count &&
                !flockIds.Except(AssignedFlockIds).Any())
                return;
            throw new FlockScopeReassignmentException(IsUnrestricted, AssignedFlockIds, unrestricted, flockIds);
        }

        IsUnrestricted = unrestricted;
        AssignedFlockIds = flockIds.ToList().AsReadOnly(); // defensive copy
        IsResolved = true;
    }
}

public sealed class FlockScopeReassignmentException(
    bool oldUnrestricted, IReadOnlyCollection<Guid> oldIds,
    bool newUnrestricted, IReadOnlyCollection<Guid> newIds)
    : Exception($"FlockScope reassignment: ({oldUnrestricted}, {oldIds.Count} ids) -> ({newUnrestricted}, {newIds.Count} ids)");
```

- [ ] **Step 2: Write `FlockScopeResolutionMiddleware.cs`**

Create `src/Cluckwork.Api/Middleware/FlockScopeResolutionMiddleware.cs`. Mirror the shape of `src/Cluckwork.Api/Middleware/TenantResolutionMiddleware.cs`:

```csharp
namespace Cluckwork.Api.Middleware;

using System.Security.Claims;
using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// #388 — resolves FlockScope per request from UserRoleAssignment rows.
// Runs AFTER TenantResolutionMiddleware (which resolves AccountId from the JWT)
// and BEFORE CredentialEpochMiddleware — it touches no credential state, so its
// position is pinned by CredentialEpochMiddlewareOrderTests.
// Skips the DB read for Owner/Manager (role check from ICurrentUser, no I/O).
// An unresolved user (seeders, one-shot verbs, background jobs) is Unrestricted
// (matches FlockScopeGuard line 70 fail-open behavior).
public sealed class FlockScopeResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, FlockScope scope, ICurrentUser user, AppDbContext db)
    {
        if (!user.IsResolved)
        {
            // Unresolved user (seeders, one-shot verbs, background jobs): Unrestricted.
            // Matches FlockScopeGuard line 70 fail-open behavior.
            scope.Resolve(true, []);
            await next(context);
            return;
        }

        if (user.Roles.Contains(Roles.Owner) || user.Roles.Contains(Roles.Manager))
        {
            // Owner/Manager: Unrestricted, no DB read.
            scope.Resolve(true, []);
            await next(context);
            return;
        }

        var assignments = await db.UserRoleAssignments.AsNoTracking()
            .Where(a => a.UserId == user.UserId)
            .ToListAsync(context.RequestAborted);

        if (assignments.Count == 0)
        {
            // 0 rows: unscoped worker (grandfathered #73). Unrestricted.
            scope.Resolve(true, []);
            await next(context);
            return;
        }

        if (assignments.Any(a => a.FlockId == null))
        {
            // Farm-wide row: grants everything (matches FlockScopeGuard line 84). Unrestricted.
            scope.Resolve(true, []);
            await next(context);
            return;
        }

        var flockIds = assignments.Where(a => a.FlockId != null).Select(a => a.FlockId!.Value).ToList();
        scope.Resolve(false, flockIds);
        await next(context);
    }
}
```

- [ ] **Step 3: Register the service and the middleware**

In `CluckworkFeatureServiceCollectionExtensions.cs`, add `services.AddScoped<FlockScope>();` (parallel to `services.AddScoped<TenantContext>()` in `CluckworkPersistenceServiceCollectionExtensions.cs`).

In `Program.cs`, add `app.UseMiddleware<FlockScopeResolutionMiddleware>();` on the line immediately AFTER `app.UseMiddleware<TenantResolutionMiddleware>();` (before `CredentialEpochMiddleware`).

In `CredentialEpochMiddlewareOrderTests.cs`, the pinned `expectedOrder` array asserts the COMPLETE contiguous sequence from `UseAuthentication()` to `IdempotencyMiddleware` — insert `"app.UseMiddleware<FlockScopeResolutionMiddleware>();",` between the `TenantResolutionMiddleware` and `CredentialEpochMiddleware` entries or that test fails. This is the registry-reader rule: the fence test walks the sequence, so a new middleware in the window must be added to it.

- [ ] **Step 4: Write the middleware behavior tests**

Create `tests/Cluckwork.Api.IntegrationTests/FlockScopeMiddlewareTests.cs`. Mirror the shape of `RoleMatrixTests.cs` (same `CluckworkWebApplicationFactory`, same `[Collection(IntegrationCollection.Name)]`). These assert the RESOLUTION OUTCOME, not scoping — the factory's DI exposes the scoped `FlockScope`, so a request by a given persona, followed by a `scope.IsResolved/IsUnrestricted/AssignedFlockIds` check on that request's scope instance, is the assertion path (resolve the scope from the factory's `Services` after the request — or use a probe endpoint if the factory's scope instance is not request-scoped; pick the mechanism that works against the existing factory and note it in the test's comment).

The sketch below is illustrative only; the shipped fact names differ (`Worker_ZeroAssignments_Request_Succeeds`, `Worker_FarmWideRow_Request_Succeeds`, `Owner_Request_Completes_ScopeUnrestricted`, `Manager_Request_Completes_ScopeUnrestricted`) and, as of #611 round 1, `Owner_Request_Completes_ScopeUnrestricted`/`Manager_Request_Completes_ScopeUnrestricted` also assert that an elevated role's own flock assignment never narrows its reads (`AssertElevatedUserWithAssignmentIsUnrestricted`), which this sketch predates.

```csharp
[Collection(IntegrationCollection.Name)]
public sealed class FlockScopeMiddlewareTests(CluckworkWebApplicationFactory factory)
{
    // Fixture: one farm, two flocks (A, B), an Owner, a Manager, a Worker with
    // 0 assignments, a Worker with one flock-A assignment row, a Worker with a
    // farm-wide (FlockId=null) row, and an unresolved (no-auth) request.

    [Fact]
    public async Task Owner_Resolved_Unrestricted() { /* assert scope.Unrestricted, no flock ids */ }

    [Fact]
    public async Task Manager_Resolved_Unrestricted() { /* INV-2 */ }

    [Fact]
    public async Task Worker_ZeroAssignments_Request_Succeeds() { /* INV-3 */ }

    [Fact]
    public async Task Worker_FarmWideRow_Request_Succeeds() { /* INV-3 */ }

    [Fact]
    public async Task Worker_SingleFlockRow_Resolved_RestrictedToThatFlock() { /* RestrictedTo([A]) */ }

    [Fact]
    public async Task UnauthenticatedRequest_Resolved_Unrestricted() { /* unresolved user fail-open, INV-4 */ }

    [Fact]
    public void Resolve_SameScopeTwice_IsNoOp() { /* new FlockScope(); Resolve(true, []); Resolve(true, []); assert no throw */ }

    [Fact]
    public void Resolve_DifferingScope_ThrowsReassignmentException() { /* Resolve(false, [A]); Resolve(false, [B]) → FlockScopeReassignmentException */ }
}
```

- [ ] **Step 5: Build and run tests**

Run: `dotnet build Cluckwork.sln --configuration Release --no-restore`
Expected: clean (0 errors, 0 warnings).

Run: `dotnet test Cluckwork.sln --configuration Release --no-build --filter "FullyQualifiedName~FlockScopeMiddlewareTests|FullyQualifiedName~CredentialEpochMiddlewareOrderTests" --verbosity normal`
Expected: PASS (resolution works; the middleware order fence includes the new middleware).

- [ ] **Step 6: Commit**

```bash
git add src/Cluckwork.Infrastructure/Persistence/FlockScope.cs \
        src/Cluckwork.Api/Middleware/FlockScopeResolutionMiddleware.cs \
        src/Cluckwork.Api/Program.cs \
        src/Cluckwork.Api/Hosting/CluckworkFeatureServiceCollectionExtensions.cs \
        tests/Cluckwork.Api.IntegrationTests/CredentialEpochMiddlewareOrderTests.cs \
        tests/Cluckwork.Api.IntegrationTests/FlockScopeMiddlewareTests.cs
git commit -m "feat: add FlockScope service and resolution middleware (#388)"
```

---

### Task 2: 8 query filters on `AppDbContext`

**Files:**
- Modify: `src/Cluckwork.Infrastructure/Persistence/AppDbContext.cs` (add `FlockScope` constructor parameter + 8 query filters)
- Modify: `src/Cluckwork.Infrastructure/Persistence/AppDbContextDesignTimeFactory.cs` (line 85)
- Modify: `src/Cluckwork.Infrastructure/Repositories/ExportQueries.cs` (line 76 — manual `new AppDbContext(` with the request-scoped `tenant`; pass the request-scoped `FlockScope`, NOT a fresh one)
- Modify: the enumerated `new AppDbContext(` sites in `tests/` (Step 4 list)
- Test: `tests/Cluckwork.Api.IntegrationTests/FlockScopeTests.cs` (new — the scoping tests; written here, not in Task 1, because they require the filters to pass)

**Interfaces:**
- Consumes: `FlockScope` (Task 1)
- Produces: 8 query filters on `Flock`, `DailyEntry`, `EggLot`, `BirdMovement`, `InventoryMovement`, `FeedUsage`, `WaterUsage`, `Expense`.

- [ ] **Step 1: Add `FlockScope` to the `AppDbContext` constructor**

In `AppDbContext.cs`, change the constructor from:
```csharp
public class AppDbContext(DbContextOptions<AppDbContext> options, TenantContext tenant)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
```
to:
```csharp
public class AppDbContext(DbContextOptions<AppDbContext> options, TenantContext tenant, FlockScope flockScope)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
```
(Primary-constructor parameter; the filters below close over `flockScope` exactly as the tenancy filters close over `tenant`.)

- [ ] **Step 2: Combine flock scope into 8 existing tenancy filters**

Do **not** append a second unnamed `HasQueryFilter`: EF documents that a simple second call overwrites the first, which would delete tenant isolation on all 8 tables. Replace each entity's existing one-line tenancy filter with ONE combined predicate: `e.AccountId == tenant.AccountId && (flockScope.IsUnrestricted || assigned-match)`. For `Flock`, assigned-match uses `e.Id`; for the other entities it uses `e.FlockId`; `InventoryMovement`/`Expense` also pass `e.FlockId == null` (farm-wide). The correctness-critical exact blocks live in `docs/plans/388-runbook.md` Increment 2b. Afterward there remains exactly one `HasQueryFilter` call per entity and every one retains `AccountId` as its first conjunct.

- [ ] **Step 3: Update every manual `new AppDbContext(` site**

`AppDbContext`'s constructor is now 3-arg. Every manual construction must pass a `FlockScope`:

- `src/Cluckwork.Infrastructure/Persistence/AppDbContextDesignTimeFactory.cs:85` — `new AppDbContext(options.Options, new TenantContext(), new FlockScope())` (fresh, unresolved → Unrestricted; design-time reads are unrestricted, matching how `new TenantContext()` behaves there).
- `src/Cluckwork.Infrastructure/Repositories/ExportQueries.cs:76` — `BeginConsistentReadAsync` builds a SEPARATE snapshot context. It must take the request-scoped `FlockScope` as a constructor parameter (add `FlockScope flockScope` to `ExportQueries(AppDbContext db, TenantContext tenant, FlockScope flockScope)` and pass it to the snapshot `new AppDbContext(..., flockScope)`) so an export run by a scoped caller is scoped the same as any other read. The DI registration in `CluckworkFeatureServiceCollectionExtensions.cs:100-101` resolves it from the request scope automatically — no registration change, constructor only. (Export endpoints are AdminOnly, so this is defense-in-depth, but the snapshot must not silently be UNRESTRICTED for a scoped caller — an unrestricted snapshot context is a wider read than the request-scoped `db` the same class uses elsewhere.)
- Test sites — add `new FlockScope()` as the third argument (fresh → Unrestricted; test contexts today assume an unrestricted tenant, and `FlockScope` must not change what they see):
  - `tests/Cluckwork.Application.Tests/TenantBypass/TenantBypassRealTreeTests.cs:62`
  - `tests/Cluckwork.Application.Tests/TenantBypass/TenantBypassDiscoveryTests.cs:36,96`
  - `tests/Cluckwork.Api.IntegrationTests/StepUpAuthTests.cs:832,965`
  - `tests/Cluckwork.Api.IntegrationTests/DisableUserRaceTests.cs:114,439,484,568,615`
  - `tests/Cluckwork.Api.IntegrationTests/ChangeUserRoleRaceTests.cs:172,375`
  - `tests/Cluckwork.Api.IntegrationTests/ChangeUserEmailRaceTests.cs:146,274`
  - `tests/Cluckwork.Api.IntegrationTests/SchemaDocsTests.cs:65`
  - `tests/Cluckwork.Api.IntegrationTests/BootstrapAdminCommandTests.cs:256,287`
  - `tests/Cluckwork.Api.IntegrationTests/BaseReferenceDataMigrationTests.cs:50`
  - `tests/Cluckwork.Api.IntegrationTests/SecurityEventLoggingTests.cs:309,368`
  - `tests/Cluckwork.Api.IntegrationTests/FirstRunLoginNoticeTests.cs:241`
  - `tests/Cluckwork.Api.IntegrationTests/ApplicationUserIndexModelTests.cs:28`
  - `tests/Cluckwork.Api.IntegrationTests/AdminRecoveryServiceTests.cs:210,228,243`
  - `tests/Cluckwork.Api.IntegrationTests/AccountScopedIdentityMigrationTests.cs:40`
  - `tests/Cluckwork.Api.IntegrationTests/IdempotencyRecordPurgeSweepTests.cs:140`
  - `tests/Cluckwork.Api.IntegrationTests/ReportQueryBoundingTests.cs:34`
  - `tests/Cluckwork.Api.IntegrationTests/CurrencyLockSerializationTests.cs:83,99,170`
  - `tests/Cluckwork.Api.IntegrationTests/CurrencyLockRaceTests.cs:86,156,230`
  - `tests/Cluckwork.Api.IntegrationTests/AccountSlugMigrationTests.cs:40`
  - `tests/Cluckwork.Api.IntegrationTests/StepUpGrantRegistryTests.cs:72,102`
  - `tests/Cluckwork.Api.IntegrationTests/StepUpGrantRegistrySharedStoreTests.cs:73,162`
  - `tests/Cluckwork.Api.IntegrationTests/FarmBannerMigrationDowngradeTests.cs:34`
  - `tests/Cluckwork.Api.IntegrationTests/TenantScopedLockTests.cs:47,92`

  (Line numbers are as of base commit `170e9d52`; if any is off by a few lines, the grep pattern `new AppDbContext(` in `tests/` is the authority — every hit must become 3-arg.)

- [ ] **Step 4: Write the scoping tests**

Create `tests/Cluckwork.Api.IntegrationTests/FlockScopeTests.cs`. Mirror the shape of `RoleMatrixTests.cs` (same `CluckworkWebApplicationFactory`, same `[Collection(IntegrationCollection.Name)]`). Fixture: one farm, two flocks (A, B) each with a daily entry + an egg lot, a farm-wide expense and a flock-A expense, an Owner, a Manager, and a Worker assigned to flock A only.

```csharp
[Collection(IntegrationCollection.Name)]
public sealed class FlockScopeTests(CluckworkWebApplicationFactory factory)
{
    // INV-1 — the core bug from #388's title.
    [Fact]
    public async Task ScopedWorker_FlocksList_SeesOnlyAssignedFlock()
    {
        // Worker (flock A) GET /api/v1/flocks → 200, body contains flock A only.
    }

    // Symmetric filtering: unassigned flock detail = 404, not 403 (settled decision 2).
    [Fact]
    public async Task ScopedWorker_UnassignedFlockDetail_Returns404()
    {
        // Worker (flock A) GET /api/v1/flocks/{flockB} → 404.
    }

    // Positive control (INV-1 inverse): the assigned flock is still readable.
    [Fact]
    public async Task ScopedWorker_AssignedFlockDetail_Returns200()
    {
        // Worker (flock A) GET /api/v1/flocks/{flockA} → 200.
    }

    // INV-2 — elevated roles are unrestricted (list shows both flocks).
    [Fact]
    public async Task Owner_FlocksList_SeesAllFlocks()
    {
        // Owner GET /api/v1/flocks → 200, both A and B.
    }

    [Fact]
    public async Task Manager_FlocksList_SeesAllFlocks()
    {
        // Manager GET /api/v1/flocks → 200, both A and B.
    }

    // Child-row scoping (DailyEntry/BirdMovement/WaterUsage filters) through
    // Worker-reachable reads.
    [Fact]
    public async Task ScopedWorker_ChildRows_AreScoped()
    {
        // Worker (flock A):
        //   GET /api/v1/daily-entries          → only flock A's entries
        //   GET /api/v1/flocks/{flockA}/movements → flock A's movements (200)
        //   GET /api/v1/flocks/{flockB}/movements → 404 (flock B not visible)
        //   GET /api/v1/water-usage            → only flock A's water rows
    }

    // The Expense filter (INV-1 covers every scoped entity) has no Worker-reachable
    // HTTP surface — /api/v1/expenses is AdminOnly (#87, money admin end to end) —
    // so it is exercised at the repository layer: resolve the scoped service
    // provider the same way the middleware does (scoped FlockScope + a db
    // constructed with it) and assert db.Expenses returns the FlockId=null row
    // (farm-wide, settled decision 1) but NOT the flock-B row.
    [Fact]
    public async Task ExpenseFilter_FarmWideVisible_UnassignedExcluded()
    {
        // Restricted scope (flock A): the farm-wide expense IS present,
        // the flock-B expense is NOT, the flock-A expense IS present.
    }
}
```

(Do NOT assert expense reads through an HTTP endpoint for a Worker — the group is AdminOnly and a Worker gets 403 before any filter runs. The repository-layer assertion above is the mechanism; the mutation row M2 names it.)

**Shipped test inventory addendum (#611 clean-round-1):** `EggInventoryMovement` has no `FlockId` column, so its Worker-reachable movement-ledger endpoint (`GET /api/v1/stock/lots/{id}/movements`) cannot be protected by a query filter on the movement table itself — it is protected by the filtered `EggLot` parent lookup (`StockEndpoints.ListLotMovements` 404s when `eggLots.GetByIdAsync(id, ct)` returns null through the flock-scoped `EggLot` filter). `FlockScopeTests.ScopedWorker_LotMovementLedger_UsesFilteredParentGate` pins this: an assigned lot's movements return 200 and contain the seeded row; an unassigned lot's movements return 404. Mutation `MP-PARENT` (deleting the parent-gate `if` in `ListLotMovements`) turns the unassigned case into 200 — confirmed RED, then restored green.

- [ ] **Step 5: Build and run tests**

Run: `dotnet build Cluckwork.sln --configuration Release --no-restore`
Expected: clean.

Run: `dotnet test Cluckwork.sln --configuration Release --no-build --filter "FullyQualifiedName~FlockScopeTests" --verbosity normal`
Expected: PASS (filters are in place — the scoping tests now pass).

- [ ] **Step 6: Run the full suite**

Run: `dotnet test Cluckwork.sln --configuration Release --no-build --verbosity normal`
Expected: clean (or the same failures as the baseline — block on anything new or changed).

- [ ] **Step 7: Commit**

```bash
git add src/Cluckwork.Infrastructure/Persistence/AppDbContext.cs \
        src/Cluckwork.Infrastructure/Persistence/AppDbContextDesignTimeFactory.cs \
        src/Cluckwork.Infrastructure/Repositories/ExportQueries.cs \
        tests/
git commit -m "feat: add 8 flock-scope query filters to AppDbContext (#388)"
```

---

### Task 3: Raw-SQL flock-scope predicates

**Files:**
- Modify: `src/Cluckwork.Infrastructure/Repositories/EggLotRepository.cs` (3 raw-SQL sites)
- Test: `tests/Cluckwork.Api.IntegrationTests/FlockScopeTests.cs` (raw-SQL path test)
- DO-NOT-TOUCH: `InventoryLotRepository.cs`, `SalesOrderRepository.cs`, `AccountRepository.cs`, `AuditEventRepository.cs`, `InventoryItemRepository.cs` (documented below)

**Interfaces:**
- Consumes: `FlockScope` (Task 1, via the `AppDbContext` constructor parameter — the repositories already hold `db`)
- Produces: raw-SQL queries with flock-scope `WHERE` predicates where the queried table has a flock column.

**Superseded (owner decision 2026-08-27, #611 round 1):** this task originally added the flock-scope predicate to all three `EggLotRepository` raw-SQL sites, including `GetAvailableFifoLockedAsync`. That broke sale confirmation for a scoped Worker — SalesFlow lets a plain Worker confirm a sale and allocate against the farm's FIFO stock, not only assigned-flock stock (current `main` behavior, preserved). The round-1 fix **removed** the predicate from `GetAvailableFifoLockedAsync` only; `GetByIdsLockedAsync` and `GetByDailyEntryLockedAsync` (both AdminOnly reconciliation paths) keep it. #612 owns the future assigned-only/farm-wide setting. The table below is historical (as originally planned); the current contract is the corrected row after it.

**Scope walk (why the file list is exactly this):** every `IgnoreQueryFilters`/`FromSqlInterpolated` site in `src/Cluckwork.Infrastructure` was walked; the ones over the 8 filtered entities are:

| Site | Table | Flock column? | Action |
|---|---|---|---|
| `EggLotRepository.cs:38` `GetAvailableFifoLockedAsync` | `"EggLots"` | `"FlockId"` (NOT NULL) | ~~add predicate (this task)~~ — **superseded:** stays farm-wide (#612), no predicate |
| `EggLotRepository.cs:66` `GetByIdsLockedAsync` | `"EggLots"` | `"FlockId"` (NOT NULL) | add predicate (this task) — needs no JOIN: the table itself carries the column |
| `EggLotRepository.cs:85` `GetByDailyEntryLockedAsync` | `"EggLots"` | `"FlockId"` (NOT NULL) | add predicate (this task) |
| `InventoryLotRepository.cs:19,39` | `"InventoryLots"` | **no `FlockId` column** — feed stock is farm-wide | NO predicate possible; see below |
| `SalesOrderRepository.cs:30` | `"SalesOrders"` | **no `FlockId` column** | NO predicate possible; sale writes are SalesFlow-gated (Worker-reachable), not AdminOnly — documented no-op |
| `AccountRepository` / `AuditEventRepository` / `InventoryItemRepository` | Account / AuditEvent / InventoryItem | not in the 8-entity filter set | untouched by design |

**`InventoryLot` / `InventoryItem` (no flock column):** feed stock is farm-wide by model (an `InventoryLot` has no `FlockId`), and `InventoryItem` is a catalog row with no flock at all. Item and lot reads (`InventoryEndpoints` catalog/stock) are Worker-reachable but inherently farm-wide — there is no `FlockId` to scope by, and none is invented (no predicate, no JOIN). Inventory **movement** and **usage** reads (`InventoryMovement`, `FeedUsage`) ARE Worker-reachable and ARE scoped — Task 2's combined EF filters cover both (`InventoryMovement`'s nullable `FlockId` also passes farm-wide rows). Inventory configuration and corrections are AdminOnly, unaffected either way. Do not claim item/lot reads are AdminOnly (they are not) or that movement/usage reads have no scoped read to protect (they do — Task 2 protects them).

- [ ] **Step 1 (superseded): Add the flock predicate to the 3 `EggLotRepository` raw-SQL sites**

As originally planned, each of the three `FromSqlInterpolated` queries got one fixed predicate: `AND ({scope.IsUnrestricted} OR "FlockId" = ANY({scope.AssignedFlockIds.ToArray()}))`. The #611 round-1 fix removed that predicate from `GetAvailableFifoLockedAsync` only (owner decision, preserves `main`'s farm-wide sale allocation for a Worker); `GetByIdsLockedAsync` and `GetByDailyEntryLockedAsync` keep it, exactly as originally written here. `EggLots.FlockId` is NOT NULL, so no `IS NULL` branch; `= ANY(...)` is the existing Npgsql array-parameter pattern; do not interpolate a prebuilt SQL-clause string.

- [ ] **Step 2 (superseded): Add a raw-SQL-path integration test**

The shipped fact is `FlockScopeTests.EggLotRawSqlPaths_HonorTheirDistinctSalesAndAdminContracts` — one fact covering all three raw-SQL sites, asserting the FIFO path stays farm-wide (SalesFlow, #612) while `GetByIdsLockedAsync`/`GetByDailyEntryLockedAsync` stay flock-scoped (AdminOnly reconciliation). It replaces the originally planned `ScopedWorker_EggLotRawSqlPath_IsScoped`, which assumed all three sites were scoped identically.

- [ ] **Step 3: Build and run tests**

Run: `dotnet build Cluckwork.sln --configuration Release --no-restore`
Expected: clean.

Run: `dotnet test Cluckwork.sln --configuration Release --no-build --filter "FullyQualifiedName~FlockScopeTests" --verbosity normal`
Expected: PASS.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test Cluckwork.sln --configuration Release --no-build --verbosity normal`
Expected: clean.

- [ ] **Step 5: Commit**

```bash
git add src/Cluckwork.Infrastructure/Repositories/EggLotRepository.cs \
        tests/Cluckwork.Api.IntegrationTests/FlockScopeTests.cs
git commit -m "feat: add flock-scope predicates to raw-SQL egg-lot lock paths (#388)"
```

---

### Task 4: Docs + E2E

**Files:**
- Modify: `specs/product/GLOSSARY.md` (add "flock scoping" definition)
- Modify: `web/src/i18n/en.ts`, `web/src/i18n/es.ts`, `web/src/i18n/tl.ts` (SPA Help glossary entries — every locale, same key names; `i18n.test.ts` / `translations-status.ts` enforce parity)
- Modify: `web/src/routes/HelpPage.tsx` (the glossary table enumerates rows explicitly; add the new rendered row)
- Modify: `web/src/routes/HelpPage.test.tsx` (glossary-rendering guard for the new row, same shape as the FIFO/step-up/Disabled-user guards)
- Modify: `tools/simulation/ui/specs/worker.spec.ts` (#277 Playwright E2E — Worker persona read assertions + preserve server write-guard coverage via a captured-id `page.route` POST-body rewrite, because the restricted picker now hides the unassigned flock)

**Interfaces:**
- Consumes: Tasks 1-3 (the feature is complete)
- Produces: documentation surfaces rendered at runtime, in all three locales.

- [ ] **Step 1: GLOSSARY.md**

Add a "flock scoping" entry to `specs/product/GLOSSARY.md`: "A Worker's reads are limited to their assigned flocks + farm-wide rows. Owner/Manager unrestricted. Workers with 0 assignment rows or a farm-wide row unrestricted. Unassigned flocks return 404 (not 403)."

- [ ] **Step 2: SPA Help glossary (all three locales)**

Add a `glossaryFlockScopingTerm` / `glossaryFlockScopingDef` pair (naming follows the existing `glossary*Term`/`glossary*Def` convention in `web/src/i18n/en.ts`) to **all three** locale files — `en.ts`, `es.ts`, `tl.ts` — with native-language definitions. `web/src/i18n/i18n.test.ts` and `translations-status.ts` enforce key parity across locales; a missing key in any locale fails `npm run test:coverage`. Add the corresponding explicit `<tr>` to `web/src/routes/HelpPage.tsx`; the page does not auto-list keys. Evidence: `HelpPage.test.tsx`'s `"renders the flock-scoping glossary term and definition from the catalog (#388)"` fact (same `withOverride` shape as the FIFO/step-up/Disabled-user guards) asserts the row reads from the catalog, not a hardcoded literal, and that the catalog's real English term is absent when overridden.

- [ ] **Step 3: #277 Playwright E2E spec**

Update `tools/simulation/ui/specs/worker.spec.ts` to assert read scoping for the scoped Worker persona: the flock picker shows the assigned flock and hides the unassigned flock (`is read-scoped to its assigned flock on the daily-entry picker (#388)`). The SPA has no flock-detail route (only the `/flocks` list), so the unassigned-detail 404 contract is pinned by `FlockScopeTests.ScopedWorker_UnassignedFlockDetail_Returns404` in the API integration suite, not by this spec.

Preserve the server-side 422 write-guard coverage: the restricted picker no longer offers the unassigned flock, so the existing test's `selectOptionContaining(..., UNASSIGNED_FLOCK)` under the restricted persona breaks. The shipped mechanism does **not** inject a DOM option: sign in as the **unrestricted** worker first and capture both the assigned and unassigned flock's real ids from its (unfiltered) picker via `selectOptionContaining`; sign out; sign in as the **restricted** worker and select the real assigned option (the only one it has); register a `page.route("**/api/v1/daily-entries", ...)` handler that, on the POST only, asserts the outgoing body's `flockId` is not already the unassigned id, then rewrites it to the captured unassigned id via `route.fallback({ postData: ... })` and calls `route.fallback()` unmodified for every other method. Submit through the normal SPA path (the module-held access token travels with the real request) and assert the `not assigned to this flock` refusal text plus `rewrotePost === true`. Never use `page.request.post` (it lacks the SPA's module-held token) and never hardcode a flock id or credential. There is no private runbook step for this — the procedure above is the whole mechanism, already shipped in `worker.spec.ts`.

- [ ] **Step 4: Build web and run web tests**

Run: `cd web && npm run build && npm run test:coverage`
Expected: clean.

- [ ] **Step 5: Commit**

```bash
git add specs/product/GLOSSARY.md web/src/i18n/ web/src/routes/HelpPage.tsx tools/simulation/ui/specs/worker.spec.ts
git commit -m "docs: add flock scoping to glossary, help, and E2E (#388)"
```

---

### Mutation checks (run after all tasks, on the final commit)

Each row: the mutation is causal to the named test — removing the code the row names is the thing that test exists to catch.

| # | Kind | Mutate | Expected test | Expected result |
|---|---|---|---|---|
| M0 | control | `FlockScope.cs`: change a comment string (no test reads it) | *(none)* | GREEN (control — proves the harness itself is sound) |
| M1 | guard | `AppDbContext.cs`: remove the `Flock` query filter | `ScopedWorker_FlocksList_SeesOnlyAssignedFlock` | RED (sees unassigned flocks) |
| M2 | guard | `AppDbContext.cs`: remove the `Expense` query filter | `ExpenseFilter_FarmWideVisible_UnassignedExcluded` (the flock-B expense must stay ABSENT) | RED (flock B's expense appears) |
| M4 | guard | `FlockScopeResolutionMiddleware.cs`: make the 0-assignment branch resolve `Restricted` (empty) instead of `Unrestricted` | `Worker_ZeroAssignments_Request_Succeeds` (corrected name, #611 round 1) | RED |
| M5 | guard | `FlockScopeResolutionMiddleware.cs`: delete the farm-wide-row early return (the `assignments.Any(a => a.FlockId == null)` branch) so a farm-wide row falls through to `Resolve(false, [])` | `Worker_FarmWideRow_Request_Succeeds` (corrected name, #611 round 1) | RED |
| MR-FIFO | guard, #611 round 1 | `EggLotRepository.cs` `GetAvailableFifoLockedAsync`: re-add the removed scope predicate | `EggLotRawSqlPaths_HonorTheirDistinctSalesAndAdminContracts` | RED at `Assert.Contains(fifo, l => l.Id == lotBId)` (farm-wide contract broken) |
| MR-BYIDS | guard, #611 round 1 | `EggLotRepository.cs` `GetByIdsLockedAsync`: remove the predicate | `EggLotRawSqlPaths_HonorTheirDistinctSalesAndAdminContracts` | RED at `Assert.DoesNotContain(byIds, l => l.Id == lotBId)` |
| MR-BYENTRY | guard, #611 round 1 | `EggLotRepository.cs` `GetByDailyEntryLockedAsync`: remove the predicate | `EggLotRawSqlPaths_HonorTheirDistinctSalesAndAdminContracts` | RED at `Assert.Empty(byUnassignedEntry)` |
| MR-ELEVATED | guard, #611 round 1 | `FlockScopeResolutionMiddleware.cs`: delete the Owner/Manager early-return block | `Owner_Request_Completes_ScopeUnrestricted` and `Manager_Request_Completes_ScopeUnrestricted` (mutation only; not a shipped middleware edit) | RED (only the assigned flock remains visible) |
| MP-PARENT | guard, #611 clean-round-1 | `StockEndpoints.cs` `ListLotMovements`: delete the parent-gate `if (await eggLots.GetByIdAsync(id, ct) is null) return Results.NotFound();` | `FlockScopeTests.ScopedWorker_LotMovementLedger_UsesFilteredParentGate` | RED — an unassigned lot's movements return 200 instead of 404 (`EggInventoryMovement` has no `FlockId` column, so this parent gate is the only scoping `ListLotMovements` has) |
| MF-FLOCK | guard, #611 round 3 | `AppDbContext.cs`: remove only the flock conjunct from the `Flock` filter (keep `AccountId`) | `AllEightCombinedFilters_AssignedPresent_UnassignedAbsent_FarmWideVisible` | RED at `Assert.DoesNotContain(fix.FlockB, flockIds)` |
| MF-DAILYENTRY | guard, #611 round 3 | `AppDbContext.cs`: remove only the flock conjunct from the `DailyEntry` filter | same fact | RED at `Assert.DoesNotContain(dailyBId, dailyIds)` |
| MF-EGGLOT | guard, #611 round 3 | `AppDbContext.cs`: remove only the flock conjunct from the `EggLot` filter | same fact | RED at `Assert.DoesNotContain(lotBId, lotIds)` |
| MF-BIRDMOVEMENT | guard, #611 round 3 | `AppDbContext.cs`: remove only the flock conjunct from the `BirdMovement` filter | same fact | RED at `Assert.DoesNotContain(birdBId, birdIds)` |
| MF-INVENTORYMOVEMENT | guard, #611 round 3 | `AppDbContext.cs`: remove only the flock conjunct from the `InventoryMovement` filter | same fact | RED at `Assert.DoesNotContain(inventoryBId, inventoryIds)` |
| MF-FEEDUSAGE | guard, #611 round 3 | `AppDbContext.cs`: remove only the flock conjunct from the `FeedUsage` filter | same fact | RED at `Assert.DoesNotContain(feedBId, feedIds)` |
| MF-WATERUSAGE | guard, #611 round 3 | `AppDbContext.cs`: remove only the flock conjunct from the `WaterUsage` filter | same fact | RED at `Assert.DoesNotContain(waterBId, waterIds)` |
| MF-EXPENSE | guard, #611 round 3 | `AppDbContext.cs`: remove only the flock conjunct from the `Expense` filter | same fact | RED at `Assert.DoesNotContain(expenseBId, expenseIds)` |
| M-SUBMIT | guard, #611 round 3 | `DailyEntryRepository.cs` `GetByIdForFlockScopedWriteAsync`: drop `IgnoreQueryFilters()`, so the write lookup runs through the combined query filter again | `RoleMatrixTests.FlockScope_CoversFeed_SubmitOfUnassignedDraft_AndMismatchedUnassign` | RED — an own-account unassigned draft now reads as null before `FlockScopeGuard` runs; the submit returns 404 instead of `422 FlockScope.NotAssigned`, failing `Assert.Contains("FlockScope.NotAssigned", ...)` |
| M-SUBMIT-TENANT | guard, #611 round 3 | `DailyEntryRepository.cs` `GetByIdForFlockScopedWriteAsync`: drop the explicit `.Where(e => e.AccountId == accountId)` reinstatement (keep `IgnoreQueryFilters()`) | `Submit_ForeignEntry_Returns404` | RED — a foreign-account entry becomes visible to the write lookup; the Owner test actor loads it, `FlockScopeGuard` does not reject it (Owner is unrestricted, not not-assigned), and the request reaches later domain validation, which currently returns `422 UnprocessableEntity` rather than the asserted `404`. The stable mutation claim is 404 changed to non-404 — do NOT label the result `FlockScope.NotAssigned`, which this mutation does not produce. |
| MT-BIRDMOVEMENT | guard, #611 round 2 | `AppDbContext.cs`: remove only the tenant conjunct (`e.AccountId == tenant.AccountId &&`) from the `BirdMovement` filter | `FiveRewrittenFilters_TenantConjunctExcludesForeignRows_WhenFlockScopeUnrestricted` | RED at `Assert.DoesNotContain(foreignBirdId, birdIds)` |
| MT-INVENTORYMOVEMENT | guard, #611 round 2 | `AppDbContext.cs`: remove only the tenant conjunct from the `InventoryMovement` filter | same fact | RED at `Assert.DoesNotContain(foreignInventoryId, inventoryIds)` |
| MT-FEEDUSAGE | guard, #611 round 2 | `AppDbContext.cs`: remove only the tenant conjunct from the `FeedUsage` filter | same fact | RED at `Assert.DoesNotContain(foreignFeedId, feedIds)` |
| MT-WATERUSAGE | guard, #611 round 2 | `AppDbContext.cs`: remove only the tenant conjunct from the `WaterUsage` filter | same fact | RED at `Assert.DoesNotContain(foreignWaterId, waterIds)` |
| MT-EXPENSE | guard, #611 round 2 | `AppDbContext.cs`: remove only the tenant conjunct from the `Expense` filter | same fact | RED at `Assert.DoesNotContain(foreignExpenseId, expenseIds)` |
| MG-WORKER-CREATE | guard, #611 attached-fix | `FlockEndpoints.cs`: revert `CreateFlock`'s authorization back to `AuthPolicies.ProductionWrite` | `AdminGatingTests.Worker_RunsTheFullDailyLoop` | RED — the Worker flock-create assertion expects `Forbidden`, actual `Created` |
| MG-MANAGER-CREATE | guard, #611 attached-fix | `FlockEndpoints.cs`: narrow `CreateFlock`'s authorization to `AuthPolicies.OwnerOnly` | `RoleMatrixTests.Manager_HasCorrectiveTier_ButNoUserManagement` | RED — the Manager flock-create assertion expects `Created`, actual `Forbidden` |
| MG-FLOCKS-BUTTON | guard, #611 attached-fix | `FlocksPage.tsx`: remove the `isAdmin` gate around the New flock button | `FlocksPage.test.tsx` "hides flock creation and every lifecycle action from a worker, but leaves the ledger read open" | RED — the button renders for a Worker |
| MG-FLOCKS-DIALOG | guard, #611 attached-fix | `FlocksPage.tsx`: change the create dialog's `open` condition back to `creating` alone | `FlocksPage.test.tsx` "closes an open create dialog the instant the role demotes away from admin" | RED — the dialog stays open after the controlled-context rerender demotes the role |
| MG-DAILY-BUTTON | guard, #611 attached-fix | `DailyEntryPage.tsx`: remove the `isAdmin` gate around the new-flock trigger | `DailyEntryPage.test.tsx` "hides the new-flock trigger from a worker" | RED — the trigger renders for a Worker |
| MG-DAILY-DIALOG | guard, #611 attached-fix | `DailyEntryPage.tsx`: change the new-flock dialog's `open` condition back to `showNewFlock` alone | `DailyEntryPage.test.tsx` "closes an open new-flock dialog the instant the role demotes away from admin" | RED — the dialog stays open after the rerender demotes the role |
| MU-UNRESOLVED | guard, #611 attached-fix | `FlockScopeResolutionMiddleware.cs`: delete the `if (!user.IsResolved) { ... return; }` block | `FlockScopeMiddlewareTests.UnresolvedLivenessRequest_ResolvesUnrestricted_WithoutDatabaseAccess` | RED — the unresolved branch now attempts the unreachable Npgsql connection and throws before `next` runs |

For each: apply → run the named test → confirm RED → restore → **rebuild** → confirm the full `FlockScopeTests` + `FlockScopeMiddlewareTests` are green again (the two `MG-*` and `MU-UNRESOLVED` SPA/API pairs are verified the same way, against their own test files). Mark each mutant with `// MUTANT M<n>: <what this breaks>` and delete the marker on restore. `grep -rn MUTANT src/ tests/ web/src` must return nothing at the end.

**Filtered-invocation coverage rule (#611 round 3):** every filtered invocation above discovered at least one test going RED — a mutation applied and re-run with zero test failures is not evidence the guard exists, it is evidence nothing exercises it. Every row in this table was confirmed causal against a green baseline before being recorded here.

**Ledger total (#611 attached-fix):** **32 rows — 1 GREEN control (M0) and 31 RED guard mutations.** M0 is the control and is never causal/RED by design; every other row above is a confirmed-RED guard mutation. The earlier "All 25 rows" count is stale: M3 was a literal duplicate of MR-BYIDS (same site, same test) and was removed rather than kept as a second row for one guard; MP-PARENT (previously documented only in the Task 4 addendum prose, not in this table) and the seven `MG-*`/`MU-UNRESOLVED` rows above were added, for a net 25 − 1 + 1 + 7 = 32.
