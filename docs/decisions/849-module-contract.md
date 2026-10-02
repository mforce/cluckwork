# Reach a contracted module only through its contract (#849)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md);
> this file records the shape later contract slices copy and the limits of its guards.

**Status:** accepted
**Date:** 2026-10-01

## What happened

No incident. This is epic #514, Track C slice 7, the Finance pilot and the first
module behind a contract. The owner accepted the audit's narrowed criterion: the
contract stays inside the `Cluckwork.Application` assembly, and `ExpenseEndpoints`
keeps its Farm currency read, Flock name read and `IAuditEventRepository`.

Before this change, `ExpenseEndpoints` and `SimulationDataSeeder` reached Finance
through 12 parameter crossings of 8 types outside any contract: four handlers, two
repositories and the two aggregates. Neither existing guard could tell that state
from a contracted one. The adapter ratchet (#846) records owners, not types, so
every endpoint row still reaches Finance after the move and the matrix's Finance
adapter count stays at 9. The seam-surface guard (#847) allows concrete aggregates
on purpose and leaves "aggregate or snapshot" to a contract decision. This record is
that decision.

## The rule

A module contract is one `I<Module>Module` interface in the module's own
Application namespace. A public `<Module>Module` class implements it by delegating
to the module's existing handlers and repositories, and
`CluckworkFeatureServiceCollectionExtensions` registers it beside them. Contract types carry ids, values and versions: the existing commands,
`Result`/`Result<T>`, and `*Details` records. They never carry an entity or an
aggregate, at any depth. List every contract type under
`owners.<Module>.contract` in `module-ledger.json`. An adapter reaches a
contracted module only through those types. It still validates through
`IValidator<TCommand>`, because the commands are contract types. If an endpoint
injects a repository again, every owner-level guard stays green while the
endpoint couples to the module's internals once more.

## Why not the obvious alternative

**A contracts project per module (design §5).** The owner chose option 1: no new
assembly. Inside one assembly, `internal` hides nothing from a peer, so the ledger
list does the hiding instead.

**Make the handlers and repository interfaces internal.** Infrastructure
implements the repository interfaces, and `CurrencyLockRaceTests` resolves
`CreateExpenseHandler` from DI. Both must stay public.

**Declare a contract namespace instead of a type list.** The commands already live
in per-feature namespaces. Moving them into a contract namespace is a namespace
move of existing types, which this slice excludes.

**Validate inside the module.** Endpoints build `ValidationProblem` responses from a
FluentValidation result, and `ExceptionHandlerReExecutionTests` swaps validators in
DI. Moving validation behind the contract changes both.

## What this does NOT cover

- Only adapters are checked: the endpoint, CLI and job namespaces, the two seeder
  types and tier namespaces. A peer module that reaches Finance's internals shows
  up only as a new edge in the module ledger. Finance has no incoming edges today.
  The edge walk records namespaces, not types, so a contract check for peer modules
  has to arrive with the first contracted module that has incoming edges.
- The adapter walk reads parameter types and service resolutions, not method
  bodies. `nameof(Expense)` or an object creation inside a body is invisible, which
  is why `IFinanceModule.ExpenseAuditEntityType` exists for the provenance reads.
  Parameter types are read through aliases, tuple aliases included, and an aliased
  `ActivatorUtilities` receiver still counts as a service resolution.
- The contract walk finds a repository through its signatures. A repository that
  exposes an entity, as every `IRepository<T, TId>` does, fails. An interface that
  exposes only DTOs and values passes, because nothing structural marks it as a repository.
- `SimulationDataSeeder` now calls `IFinanceModule`, but its existence and count
  reads still query `db.Expenses` and `db.ExpenseCategories`. #846 does not count
  persistence types as seeder reach. Those reads, and `CurrencyBoundRowProbe`'s, are
  `compatibilityExceptions` rows guarded by #850. `ReportQueries` and `ExportQueries`
  moved to Insights in #856 and read Finance through a declared edge.
- Finance's own handlers still inject Farm's `IAccountRepository` and Flock
  Management's `IFlockRepository`. Those ports belong to the Farm and Flock
  Management contract slices.

## How it is enforced

- `AdapterReachRealTreeTests.RealSourceTree_EveryAdapterReachIsDeclared` fails on a
  contract bypass that `AdapterReachScanner` reports. It also fails when a contract
  type is not declared under `src/` or sits outside its owner's namespaces. The
  fixture tests are `AdapterReachTests.Contract*`. Run against `origin/main` at
  05d7e7a7, the check reports all 12 crossings above.
- `ModuleContractRealAssemblyTests` loads every contract type by reflection and
  walks it with `SeamSurfaceScanner.ScanContracts`, which adds the
  entity-or-aggregate rule to #847's persistence rules. It follows public
  properties and fields, static and inherited ones included, at any depth. For any
  interface a contract type exposes, it also walks the interface's method signatures,
  generic constraints included, and the interfaces it inherits. The field walk applies to #847's scan too. Adding
  `Task<Expense?> LeakAsync(Guid id)` to `IFinanceModule` turns it red. The fixture
  tests are `SeamSurfaceTests.Contract_*`.
- `FinanceModuleTests` pins the read paths' field-by-field copy against literals.
  Swapping `FarmId` and `ExpenseCategoryId` in the copy turns it red.
