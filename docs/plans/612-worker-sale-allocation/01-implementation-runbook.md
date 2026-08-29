# #612 Worker Sale-Allocation Policy — Implementation Runbook

> **For the Claude Sonnet implementer:** Use test-driven development and execute
> these tasks in order. Work alone; do not spawn additional agents. Do not
> commit or push unless the owner asks.

**Goal:** Add an Account setting that makes restricted Worker sale confirmation
use assigned flocks by default, with an explicit farm-wide opt-in.

**Architecture:** Persist one enum on Account. Confirmation locks Account →
SalesOrder → EggLots, reads live role/assignments, plans the whole order without
mutation, then applies one successful plan. Existing authorization and FIFO SQL
remain the source of truth.

**Tech stack:** .NET 10, EF Core/PostgreSQL, xUnit/Testcontainers, React 19,
Vite/Vitest.

**Design:** `docs/designs/612-worker-sales-allocation-policy.md`

## Global constraints

- `AssignedFlocksOnly` is the database/domain default.
- One new migration; `InitialCreate` stays frozen; regenerate `docs/schema/`.
- Every Account, SalesOrder, and EggLot mutation bumps `Version` through its
  aggregate method.
- Writes keep FluentValidation, authorization, idempotency, audit, and tenant
  checks.
- Do not add a flock predicate or second call to the farm-wide FIFO lock query.
- Do not expose unassigned-flock names, quantities, or derived shortfalls.
- Do not add assignment locks, packages, or unrelated refactors.

---

### Task 1: Persist and expose the setting

**Files**

- Create `src/Cluckwork.Domain/Accounts/WorkerSaleAllocationPolicy.cs`
- Modify `src/Cluckwork.Domain/Accounts/Account.cs`
- Modify `src/Cluckwork.Application/Features/Accounts/UpdateFarmSettings/*`
- Modify `src/Cluckwork.Api/Endpoints/Accounts/AccountEndpoints.cs`
- Modify Account EF configuration and add migration `AddWorkerSaleAllocationPolicy`
- Test in existing Account/domain, validator, and `FarmSettingsTests.cs` suites

**Interface**

```csharp
public enum WorkerSaleAllocationPolicy
{
    AssignedFlocksOnly,
    AllFarmFlocks,
}
```

- [ ] Add failing tests for Account create default, settings serialization,
      required named-enum validation, audit snapshot, and `Version++`.
- [ ] Run the focused Domain/Application/FarmSettings tests and confirm the new
      assertions fail for the missing property.
- [ ] Add the enum/property, thread it through `UpdateSettings`, command,
      validator, snapshots, and settings DTOs.
- [ ] In `UpdateFarmSettingsHandler`, treat an actual policy change like a
      currency change: enter the existing transaction and take Account
      `FOR UPDATE`. A same-value request may keep the optimistic path.
- [ ] Add `showFarmWideSaleAllocationNotice` to `/account`; it is true only for
      a restricted plain Worker under `AllFarmFlocks`.
- [ ] Generate the migration with default/backfill `AssignedFlocksOnly`; add a
      downgrade test; regenerate schema docs.
- [ ] Run:

```bash
dotnet test tests/Cluckwork.Domain.Tests/Cluckwork.Domain.Tests.csproj --filter "Account" --verbosity normal
dotnet test tests/Cluckwork.Application.Tests/Cluckwork.Application.Tests.csproj --filter "UpdateFarmSettings" --verbosity normal
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj --filter "FarmSettings" --verbosity normal
tools/schema-docs/generate.sh --check
```

Expected: all pass; `InitialCreate` is unchanged.

---

### Task 2: Make assignments plain-Worker-only

**Files**

- Modify `src/Cluckwork.Domain/Accounts/Roles.cs`
- Modify `src/Cluckwork.Application/Common/IIdentityProvider.cs`
- Modify `src/Cluckwork.Infrastructure/Identity/IdentityProvider.cs`
- Modify `src/Cluckwork.Api/Middleware/FlockScopeResolutionMiddleware.cs`
- Modify `src/Cluckwork.Application/Common/IFlockScopeGuard.cs` and its implementation
- Modify `src/Cluckwork.Application/Features/Users/AssignFlock/AssignFlockHandler.cs`
- Test `RoleMatrixTests.cs`, `FlockScopeMiddlewareTests.cs`, `FlockScopeTests.cs`,
  and assignment handler/integration suites

**Interface**

```csharp
public enum EffectiveAccountRole { Worker, ReadOnly, Sales, Manager, Owner, Denied }

public static EffectiveAccountRole ResolveEffective(IEnumerable<string> roles);

Task<EffectiveAccountRole?> GetEffectiveRoleAsync(
    Guid accountId, Guid userId, CancellationToken ct = default);
```

- [ ] Add failing role-precedence tests: empty = Worker; known roles use
      Owner > Manager > Sales > ReadOnly; unknown-only = Denied.
- [ ] Add failing tests proving Sales/ReadOnly retained rows do not restrict
      request reads or the direct production guard, while plain Worker rows do.
- [ ] Add a failing assignment test proving a live non-Worker target gets
      `Users.FlockAssignmentsWorkerOnly`; unassign remains allowed.
- [ ] Implement the shared effective-role resolver and focused live Identity
      read. Preserve the existing Users API raw role string.
- [ ] Update middleware/guard to consult assignments only for plain Worker.
- [ ] Add the live target-role check to assignment admission; retain rows across
      promotion/demotion without new locks.
- [ ] Run:

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj --filter "RoleMatrix|FlockScope|AssignFlock" --verbosity normal
dotnet test tests/Cluckwork.Application.Tests/Cluckwork.Application.Tests.csproj --filter "FlockScope|AssignFlock" --verbosity normal
```

Expected: all pass; route policies are unchanged.

---

### Task 3: Apply the policy during confirmation

**Files**

- Create `src/Cluckwork.Application/Features/Sales/ConfirmSale/SaleAllocationPlanner.cs`
- Modify `src/Cluckwork.Domain/Sales/SalesOrder.cs`
- Modify `src/Cluckwork.Application/Features/Sales/ConfirmSale/ConfirmSaleHandler.cs`
- Modify `src/Cluckwork.Api/Endpoints/Sales/SaleEndpoints.cs`
- Keep `src/Cluckwork.Infrastructure/Repositories/EggLotRepository.cs` SQL shape
- Add focused planner tests and integration/concurrency tests

**Interfaces**

```csharp
public sealed record PlannedEggLotDraw(
    Guid SalesOrderItemId, Guid EggLotId, int Quantity);

public Result CheckCanConfirm(); // Confirm delegates to this
```

- [ ] Write failing pure-planner tests for repeated grades, assigned success,
      assigned failure/farm success, farm failure, FIFO, and unchanged inputs.
- [ ] Write failing integration tests for default assigned-only, opt-in
      farm-wide, generic restricted-Worker errors, zero/null assignments, and
      elevated farm-wide behavior.
- [ ] Write deterministic barrier tests for confirm/void, same-order confirm,
      policy change, role change, and assignment add/remove. Use test-only DI
      barriers; never sleeps or production locks.
- [ ] Move the SalesOrder read inside the transaction and remove the old
      pre-transaction tracked read. Acquire Account `FOR SHARE`, then
      SalesOrder `FOR UPDATE`, then role/assignments, then one farm-wide FIFO
      EggLot query.
- [ ] Add `CheckCanConfirm`; return expected `NotDraft`/`NoItems` before stock.
      A later contradiction from `Confirm` or `EggLot.Allocate` is an invariant
      exception so the exception path clears tracked state.
- [ ] Implement whole-order planning with copied quantities. Under assigned-only
      filter the already-locked list in memory; do not change SQL or query twice.
- [ ] Map the post-lock `Auth.Forbidden` before the generic endpoint branch so
      it returns 403. Map the distinct stock code to 422.
- [ ] On every failure assert exact SalesOrder/EggLot quantities, versions, EF
      states, and absence of allocation/movement/audit rows before scope disposal.
- [ ] Run:

```bash
dotnet test tests/Cluckwork.Application.Tests/Cluckwork.Application.Tests.csproj --filter "SaleAllocationPlanner" --verbosity normal
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj --filter "SaleAllocation|EggLotConcurrency|ConfirmSale|VoidSale" --verbosity normal
```

Expected: all pass; an SQL-command/repository spy proves one FIFO query.

---

### Task 4: Finish UI, callers, docs, and verification

**Files**

- Modify `web/src/api/cluckwork.ts` and `web/src/test/fixtures.ts`
- Modify `SettingsPage`, `SalesPage`, and `UsersPage` plus focused tests
- Modify en/es/tl catalogs and `HelpPage.tsx`
- Modify `specs/product/GLOSSARY.md`
- Update `SimulationDataSeeder`, raw settings JSON in `FarmSettingsTests` and
  `SalesProductTests`, and simulation UI fixtures/specs

- [ ] Add failing UI tests for both settings choices, persistent generic Sales
      notice, localized distinct warning, and inactive/removable elevated rows.
- [ ] Update manual TypeScript types, pages, fixtures, and en/es/tl keys.
- [ ] Update Help guide, in-app glossary, and product glossary.
- [ ] Update every actual settings/account caller. k6 has no confirmation or
      settings payload and needs no fabricated change.
- [ ] Run focused web tests, typecheck, and simulation UI tests available
      without modifying external state.
- [ ] Run the final repository gates:

```bash
dotnet build Cluckwork.sln
dotnet test Cluckwork.sln
tools/schema-docs/generate.sh --check
cd web && npm run typecheck && npm run test:coverage && npm run build && npm run verify:sw
```

Expected: clean build, full test summary recorded verbatim, schema docs current,
and web gates pass.

## Handoff

After implementation, use one fresh reviewer for the final diff, limited to:
authorization/privacy, Account → SalesOrder → EggLots ordering, failure-state
tracking, migration/caller completeness, and unnecessary complexity. Fix only
confirmed findings, rerun the affected tests, then rerun the final gates. Do not
open additional review rounds unless the reviewer finds a new high-risk defect.
