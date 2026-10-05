# Compose every module through its contract, its fixture port and its own registration file (#858)

> **Rule** — the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md);
> this file records the end state of epic #514 Track C and the limits of the
> guards that hold it.

**Status:** accepted
**Date:** 2026-10-04
**Mechanism note (2026-10-04, #859):** the ledger rows now live in the `RealModuleLedger.*.cs` files in `tests/Cluckwork.Application.Tests/Architecture`, not in a JSON file. #859 step 1 moved the remaining compatibility exception unchanged; #859's last slice deleted it, so `CompatibilityExceptions` is empty (see [859](859-typed-rule-registries.md#the-last-compatibility-exception)).

## What happened

No incident. #858 was the Platform composition slice of epic #514. It started with 35
`compatibilityExceptions` rows naming it, and #857 E2 (PR #1060) added five Access rows.
The rows let jobs, CLI verbs and both seeders read module tables directly. The feature
registration file mixed every owner in 156 statements sorted by kind. It shipped in
eight PRs, each with base/head parity and mutation evidence:

| PR | Change |
|---|---|
| P1 #1040 | Insights' contract: `IInsightsModule`, `EntityProvenance`, `ExportDataset` |
| P2 #1042 | Deleted `IRepository<T,TId>` and the repository members nothing called |
| P3 #1043 | Farm's `IFarmDirectory` for the three cross-farm `Accounts` reads |
| P4 #1062 | Finance and General Inventory fixture ports (10 rows) |
| P5 #1063 | Commerce, Egg Operations, Flock, Farm and Access fixture ports (19 rows), including the step-up-free flock assignment |
| P6 #1064 | Demo seeder fixture ports (8 rows) and the first test of its partial-seed cleanup |
| P7 #1065 | One registration file per owner under `Cluckwork.Api/Hosting/Modules/` |
| P8 | This record. Deleted `AccountSlugLookup.ResolveAsync`; `Loosenable` is empty |

`IUnitOfWork` stays. It is the Platform transaction port that 49 handlers and
`ExportEndpoints` use (approved scope amendment).

## The rule

Code outside a module reaches its tables only through types on the module's ledger
contract. Adapters, jobs, CLI verbs and seeders are all held to this. Seeder-only reads
and writes go through a fixture port, `I<Module>Fixture` (Access's read port is
`IAccessSeedLookup`). The port lives in the module's Application namespace and is listed
on the module's `contract`. Its implementation goes on the module's `implementations` only
when it sits outside the module's namespaces, as the fixtures in
`Cluckwork.Infrastructure.Repositories` do. An implementation inside them, such as Access's
`AccessFixture` and `AccessSeedLookup` in `Cluckwork.Infrastructure.Identity`, already has
its module's own-table allowance, and listing it fails `CompatibilityExceptionRealTreeTests`.
The port is registered only inside `AddCluckworkPersistence`'s `!IsProduction()` block,
and each member returns ids, counts, records or a `Result`, never an entity or a query. Purges join the
caller's transaction and never commit. Services register in their owner's file under
`Cluckwork.Api/Hosting/Modules/`, listed once by `AddCluckworkModules`; Access registers in
`AddCluckworkIdentity`. Break the gate and a cross-farm read or the step-up-free assignment
becomes injectable in Production. Skip the contract and the ledger guards fail.

## End state

- **`compatibilityExceptions`:** one row at #858's end, and none named #858.
  `UserRoleAssignmentRepository.ListByNameByUserAsync` left-joined the filtered `Flocks` set
  in one statement, so the flock-scope filter decided which names a caller saw (#613).
  #859 replaced it with `IFlockLookup.GetDisplayNamesAsync`, accepting the extra round trip,
  and deleted the row.
- **Contracts:** every seeded module's contract lists its fixture port. These are Access
  (`IAccessSeedLookup`, `IAccessFixture`), Farm, Flock Management, Egg Operations,
  Commerce, General Inventory and Finance. Farm also lists `IFarmDirectory`. Insights
  has no fixture port, because no seeder reads it.
- **Adapters:** CLI verbs, jobs and seeders reach contracted modules only through
  contract types; 140 adapter rows declare it, and `Loosenable` is empty. Some operations
  stay direct Platform operations and reach no module: `migrate` and `healthcheck`
  (#263, #266), the `SimulationSeedStates` anchor, the idempotency purge and the durable
  worker (#271).
- **`persistenceForbiddenNamespaces`:** unchanged, still `Cluckwork.Api.Endpoints`.
  `migrate`, the worker and the seeders hold `AppDbContext` for Platform tables only.
- **Generated graph:** [`coupling-matrix.md`](../../tests/Cluckwork.Application.Tests/Architecture/Data/coupling-matrix.md) is the final graph. Regenerate it; never redraw it.

## Why not the obvious alternatives

- **A blanket read allowance for the two seeder types.** It is one PR, but it loses
  per-member pinning: a future direct read in a seeder would pass without review.
- **Seeder reads on the normal `I<Module>Module` facade.** Counts, existence probes and
  account purges would become callable from request code in Production.
- **Putting the seeders' cross-farm `Accounts` reads on `IFarmDirectory`.** Production
  registers that port, so seeder-only reads would widen a production surface.
  `IFarmFixture` holds them instead.
- **A golden DI test.** It would change with every new handler. P7's parity was a scratch
  dump of the real container per service type, for serving and one-shot processes in
  Production, Development and Testing. It was identical at base and head.

## How it is enforced

| Invariant | Guard |
|---|---|
| Adapters reach contracted modules only through contract types | `AdapterReachRealTreeTests` (#846, #849) |
| Peers reach each other only through contracts or seams | `PeerContractRealTreeTests` (#1023) |
| No other reads of a module's tables, and no stale rows | `CompatibilityExceptionRealTreeTests` (#850) |
| No fixture port resolves in Production, in either process role or with no environment set | `FixturePortRegistrationTests`. It runs the real `Cluckwork.Api.dll` in a child process per case and discovers `I*Fixture` by name, with a floor of 7 |
| The flock assignment refuses another farm's user, flock or tenant; keeps the Owner actor and flock-name audit; saves once | `AccessFixtureTests`, `AccessSeedAssignmentAtomicityTests` |
| Demo cleanup empties the farm, rolls back on a failed delete, and spares other farms | `DemoSeedCleanupTests` |
| `IFarmDirectory` is named only by operator verbs, jobs, and its own and registration files | `FarmDirectoryCallerTests` |
| Every `Ensure*` boot guard under `Cluckwork.Api.Hosting`, including `Modules`, has a row | `ServingGuardCoverageTests` |
| Seed parity | Manifest counts and fingerprint, rerun and persona tests in `SimulationSeederTests`, `DemoSeedTests` |

## What this does NOT cover

- **Contract types are injectable by any adapter.** Only the registration gate keeps
  fixture ports out of Production. In Development, Testing and Staging, review alone
  keeps request code from injecting one.
- **`FixturePortRegistrationTests` finds ports by name.** A seeder port not named
  `I<Module>Fixture` escapes it unless it is added beside `IAccessSeedLookup`.
- **`FarmDirectoryCallerTests` does not follow calls.** A forwarder in an allowed folder
  would hand the directory onward unseen. That is why P8 deleted
  `AccountSlugLookup.ResolveAsync` and its adapter row. Restored, the forwarder now fails
  `AdapterReachRealTreeTests` as an undeclared reach, but only until someone adds the row
  back.
- **Two orders are kept, not enforced.**
  - The first two `ICurrencyBoundRowSource` registrations can swap with every test
    green: a payment exists only with its sales order (#854).
  - Demo cleanup deletes flocks last. Only bird movements are constrained
    (`FK_BirdMovements_DailyEntries_DailyEntryId` and `FK_BirdMovements_Flocks_FlockId`
    are `RESTRICT`), and no foreign key ties daily entries or egg lots to flocks.
- **`IFlockFixture.DepleteAsync`'s own save is not observable.** The next handler's
  `SaveChanges` on the same scoped context flushes the tracked depletion. `main` had the
  same blind spot before the move.
- **`Loosenable` is reported, not enforced** (#846). It is empty at this record's date,
  but a future unused reach stays green.
- **No assembly boundary.** Everything here is a ledger walk over one assembly per layer;
  #859 owns any split.
