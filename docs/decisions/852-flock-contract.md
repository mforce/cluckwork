# Put flock lifecycle behind a Flock Management contract with two peer ports (#852)

> **Rule** — the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md),
> beside the #849 contract rule; this file records why Flock Management has three
> contract interfaces and what the contract leaves alone.

**Status:** accepted
**Date:** 2026-10-02

## What happened

No incident. This is epic #514, Track C slice 10, the third module behind a
contract after Finance (#849) and Farm (#851). Flock Management is the first
contracted module that peer modules call in production code: Access, Finance,
General Inventory and Egg Operations all read flocks, and Egg Operations writes
the bird ledger.

Before this change, adapters reached Flock Management through 34 parameter
crossings of 15 non-contract types across 23 adapter rows. The count comes from
running the #849 adapter check against `main` at 4c429f08 with a placeholder
Flock contract. After it, the count is 0. Peer modules injected
`IFlockRepository` or `IBirdMovementRepository` 13 times across 10 handlers.
After it, they inject neither.

## The rule

The contract lives in `Cluckwork.Application.Features.Flocks` and has three
interfaces:

- `IFlockModule` is the adapter surface. It carries the six lifecycle commands
  (create, update, deplete, archive, reactivate, record a movement), the picker
  search, birds-removed counts and the movement ledger page. Endpoints and
  seeders call it.
- `IFlockLookup` is the read port. It returns a `FlockDetails` copy of one flock,
  with `CanRecordProductionOn(date)` on the copy, the page display names, and
  name resolution. Peer modules and adapters call it.
- `IMortalityLedger` is the write port. `AppendAsync` adds one daily-entry
  movement to the caller's unit of work and never saves. A positive count records
  Mortality and a negative count records an Adjustment, the rule Submit, Adjust
  and Void already used. Egg Operations calls it.

`owners.FlockManagement.contract` lists the three interfaces, the records and
commands they carry, and the `FlockStatus` and `BirdMovementType` enums.
`FlockRepository` and `BirdMovementRepository` are its listed implementations
under #850.

Each port method runs the query its caller ran before, so the SQL and the
filters do not change. In particular:

- The flock-scope read filter (#613) composes in every lookup, and
  `GetForFlockScopedWriteAsync` still reinstates `AccountId` itself (#388).
- The resolved-actor requirement for flock-scoped writes (#787) stays in
  `FlockScopeGuard`, which the handlers still call first.
- `RecordFeedUsageHandler` still locks the item, then reads eligibility, then
  reads lots, inside one transaction. The flock row stays unlocked.
- The lookup's two flock reads are untracked (#1022). `RecordFeedUsageHandler`
  reads the same flock before and inside its transaction. While the first read
  was tracked, EF handed the second read that same instance with its earlier
  state, so a flock archived while the request waited on the item lock went
  unseen. The tracked repository reads stay for the lifecycle handlers that
  mutate the flock.

## Why three interfaces, not one

#849 made a contract one `I<Module>Module` interface. Flock Management departs
from that for its peers. `FlockModule` takes the six lifecycle handlers, and DI
builds all six, with their own dependencies, every time something resolves it.
A feed-usage or daily-entry request needs one flock read or one ledger append, so
injecting `IFlockModule` there would construct six handlers per request to use
none of them. #851 left `FarmClock` on `IAccountRepository` for the same reason.

The ports also keep each peer's reach visible in its constructor. A handler that
takes `IMortalityLedger` can append a movement and do nothing else in Flock
Management.

A later slice should split the same way when a peer module, not an adapter, needs
a small part of its module: a read or one write that runs inside the peer's own
transaction. Keep one `I<Module>Module` when only adapters call the module, as
with Finance.

## Name resolution, and the one behaviour change

`IFlockLookup.ResolveByNameAsync` returns `Found`, `NotFound` or `Ambiguous` with
every candidate ordered by id. It matches the name exactly, as the seeder did,
across every status, through the filtered set. Duplicate names are legal, so a
caller must handle `Ambiguous`. #809's MCP tool needs this contract.

`SimulationDataSeeder.EnsureFlockAsync` is its only caller. Before, it took
whichever row the database returned first, and a duplicated operational flock
name failed the re-run later at the manifest count check. Now the re-run stops at
the lookup with a message naming both flock ids. Nothing else changes behaviour.

## Flock Management to Egg Operations is a schema-only foreign key

`BirdMovement.DailyEntryId` is a bare `Guid?`, so the code walk sees no reference
from Flock Management to Egg Operations. The database still carries
`FK_BirdMovements_DailyEntries_DailyEntryId`, which the ledger declares under
`foreignKeys`. An assembly split (#859) or a move to separate databases has to
break that foreign key. The one-way code dependency (egg code depends on flocks)
holds for code only.

## What this does NOT cover

- **Peer modules were not checked here.** The #849 adapter check covers adapters
  only. A peer check needed a per-owner opt-in in the ledger, because Farm keeps
  `IAccountRepository` outside its contract on purpose (#851), so it was deferred.
  #1023 added it as Farm's `seam`: a peer handler that injects `IFlockRepository`
  again now fails `PeerContractRealTreeTests`
  ([`1023-peer-contract-guard.md`](1023-peer-contract-guard.md)).
- Method bodies. Handlers still write `Error.NotFound(nameof(Flock), ...)`, so
  they keep their `Domain.Flocks` import. `IFlockModule.FlockAuditEntityType` and
  `IFlockModule.FlockNotFoundCode` exist for the adapters' bodies.
- The seeders' own `db.Flocks` and `db.BirdMovements` reads, and
  `UserRoleAssignmentRepository.ListByNameByUserAsync`'s join to `Flocks`. They
  are #850 compatibility exceptions that #858 and #857 delete.
- `ReportQueries` and `ExportQueries` read flock tables through the declared
  Insights to Flock Management edge.
- **The contract walk checks declared and statically discoverable types only.** It
  sees a signature's declared types and every public subclass of a walked class in
  the searched assemblies. A generic constructed at runtime, such as
  `Result<Flock>` returned through a non-generic `Result` signature, never appears
  in any declaration the walk can enumerate, and neither does a subclass in an
  assembly outside the search, such as Api. Only review catches those.

## How it is enforced

- `AdapterReachRealTreeTests` reports a contract bypass for any adapter that takes
  a non-contract Flock Management type.
- `ModuleContractRealAssemblyTests` walks the contract types. This slice extends
  `SeamSurfaceScanner.ScanContracts` to also walk every public type deriving from
  a walked class, because `FlockNameResolution`'s cases are separate types. It
  searches the contract types' own assemblies, the base type's assembly and the
  assemblies the caller names, because a case can sit in another assembly than its
  base: an Application subclass of Domain's `Result` that carries a `Flock` is
  returned as a plain `Result`. The real test names Application, Domain and
  Infrastructure, which implements Application's ports and can hand such a subclass
  back through one. Api is not named, because the test project does not reference
  it.
  Before the extension, adding a `Flock` property to
  `FlockNameResolution.NotFound` passed, and so did returning that `Result`
  subclass from `FlockModule.DepleteAsync`. Both now fail, as does a
  `Task<Flock?>` method added to `IFlockLookup`. The fixture tests are
  `SeamSurfaceTests.Contract_AggregateInADerivedCase_IsAViolation` and
  `Contract_AggregateInADerivedCaseFromAnotherAssembly_IsAViolation`.
- `CompatibilityExceptionRealTreeTests` registers the six reads named above.
- `FlockLookupTests` pins the copy field by field. Swapping `DepletedOn` and
  `ArchivedOn` turns it red. `MortalityLedgerTests` pins the sign rule and the
  note truncation.
- `MortalityLedgerAtomicityTests` faults the audit write after the append, outside
  HTTP. `Submit_FailureAfterMortalityAppend_PersistsNothing` turns red when the
  port calls `SaveChangesAsync`. `Void_FailureAfterMortalityReversal_PersistsNothing`
  stays green under that mutation, because Void runs inside
  `ExecuteInTransactionAsync`; it turns red when that transaction is removed too.
- `FlockNameResolutionTests` checks the tenant and flock-scope filters and the id
  order. `IgnoreQueryFilters()` in `ListByNameAsync` turns all three red.
- `SimulationDuplicateFlockNameTests` turns red when the seeder takes the first
  match again.
