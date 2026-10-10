# Register every read of a contracted module's tables from outside it (#850)

> **Rule** — the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md);
> this file records what counts as an exception and the limits of the guard.

**Status:** accepted
**Date:** 2026-10-02
**Mechanism note (2026-10-04, #859):** the ledger rows now live in the `RealModuleLedger.*.cs` files in `tests/Cluckwork.Application.Tests/Architecture`, not in a JSON file. The rule and its checks are unchanged.

## What happened

No incident. This is epic #514, Track C slice 8. The Finance pilot (#849) put Finance
behind `IFinanceModule`, but code outside Finance still read its tables through
`db.Expenses` and `db.ExpenseCategories`. No guard saw those reads. The module ledger
treats Platform as a free hub, the adapter walk reads parameter types rather than
method bodies, and #846 does not count persistence types in seeders as reach. The
exceptions lived only in the #850 issue body, keyed by line numbers that went stale
twice.

The issue named five exceptions. At `origin/main` 7ce95cf4 they stand as follows:

| Caller | State |
|---|---|
| `ReportQueries` | Resolved by #856 (PR #1012): an Insights type reading through the declared `Insights -> Finance` `R` edge |
| `ExportQueries` | Resolved the same way |
| `BusinessRecordModel` | Resolved by #969 (PR #970): it lists one contribution per module, and Finance's `FinanceBusinessRecords` in `ExpenseConfiguration.cs` names `typeof(Expense)` |
| `SimulationDataSeeder` | Three rows, owner Platform, deleted by #858 |
| `CurrencyBoundRowProbe` | One row, owner Farm, deleted by #855 |

Farm declared its contract in #851 (PR #1015) while this slice was in review. Farm lists
`AccountRepository` and `FarmLogoRepository` as implementations, and eight readers of the
`Accounts` table are registered. The credential-epoch middleware's read belongs to the
Access slice (#857), owner Access. The lock sweep, both seeders and the two CLI readers
belong to the Platform composition slice (#858), owner Platform. Per #851, none moves
behind `IFarmModule`.

## The rule

A read is any member that obtains a `DbSet<T>`. The real EF model maps `T` to the
tables a query of the set touches: its own tables, the tables of owned values mapped
apart from it, and the tables of derived types. The ledger's `tables` section names
each table's owner. The reader is
classified by its namespace owner. These differ: `UserRoleAssignment` sits in Farm's
`Domain.Accounts` namespace, but Access owns the `UserRoleAssignments` table. A read of
a table whose owner has a ledger `contract` is an exception unless one of these holds:

- the member's top-level type belongs to the table's owner;
- the type's owner has a ledger edge to the table's owner, and the edge's `symbols`
  names the type;
- the member's own type is listed under `owners.<Module>.implementations`. Each entry
  must be declared in `Cluckwork.Infrastructure`, implement one of the module's
  interfaces, and read the module's tables. Nested types are not covered. Finance
  lists `ExpenseCategoryRepository` and `ExpenseRepository`;
- the member is a `DbSet<T>` property on a `DbContext` whose whole expression body is
  `Set<T>()`, as in `Expenses => Set<Expense>()`. Any other getter, including one that
  computes something before returning the set, is a read. When one member reads a table
  both ways, the stricter classification wins.

Every other read needs a row in `RealModuleLedger.CompatibilityExceptions`.
A row is keyed by namespace, type and member, never by `file:line` (#632). Type keys
keep their type parameters, so `ExpenseRepository<T>` is not `ExpenseRepository`. It names
the module it `reaches`, the `tables` the member reads, an `owner` from the ledger's
owners, a `reason`, and `deleteWhen`, the slice issue that removes it. `deleteWhen`
must match `^#[0-9]+$`, so a date cannot stand in for an owner.

The guard finds exceptions by walking the code, not by reading a list. A new read
fails with the row to add. A registered member that starts reading a table its row
does not name fails, so a row cannot silently grow. A row whose read has gone, a
table the member no longer reads, or a member now covered by an allowance fails as
stale. Overloads share one key, so they share one row and its table list.

A `DbSet<T>` of a type parameter fails closed: the walk cannot tell which table a
generic helper reads, so the helper must obtain the set with a concrete type.

## Why not the obvious alternative

**A hand-kept list checked only for completeness of its fields.** That proves every
row is dated, but not that every exception has a row. The issue's own table had
drifted from the code before this slice.

**A syntax walk like the other ledger scanners.** A DbSet read is a property access
on a receiver. Syntax cannot tell `db.Expenses` from `counts.Expenses`, and the
seeder contains both. Syntax also misses `Set<E>()` through a `using E = ...` alias.
The guard therefore compiles `src/Cluckwork.Infrastructure` from source against the
test's references and binds every expression. A compile error there fails the guard,
because a member that does not bind is a member the walk cannot see.

**Trust every implementer of a module interface.** Implementing one Finance interface
would then exempt every member of the type and its nested types, including reads
unrelated to the port. The ledger lists trusted implementations by name instead, so
adding one is a reviewed ledger change.

**Classify a read by the entity's namespace.** Once Farm declares a contract, that
would charge the seven `UserRoleAssignments` reads, an Access table, to Farm.

**Guard every module's tables.** Only a module with a contract claims "reach me only
through these types". Applying the rule to uncontracted modules would register every
repository in `Cluckwork.Infrastructure.Repositories` as an exception. The rule widens
as each contract slice declares `owners.<Module>.contract`.

**Widen `IFinanceModule` with count and existence reads for the seeder.** The #849
audit kept the contract narrow, and the seeder's conversion belongs to #858.

## What this does NOT cover

- Projects that reference `Cluckwork.Infrastructure`, `Cluckwork.Api` and
  `Cluckwork.AppHost` today, compile against the Infrastructure compilation with
  errors tolerated, because their packages are not on this test's path. Bound reads
  are classified like Infrastructure's. A member access named after a guarded `DbSet`
  property, or any `Set<...>` call, that does not bind fails closed as unresolved.
  An unbound expression that reaches a set under some other name is not seen.
- The scanner sees a `DbSet` receiver used with `FromSql*`, but it does not read the
  table names inside SQL text. `Database.SqlQuery` and `ExecuteSql` have no `DbSet`
  receiver at all. `TenantBypassRealTreeTests` classifies every raw-SQL site.
- A read is attributed to the member that obtains the `DbSet`. A helper that returns
  `db.Expenses.AsQueryable()` to a caller registers the helper, not the caller.
- Tables reached through navigations, by `Include` or a join in LINQ, are not added to
  a read's tables. Only owned values and derived types are.
- A type named by an edge, or listed as an implementation, is trusted for every
  member. A new read in `ReportQueries`, or a new method on `ExpenseRepository`, needs
  no ledger edit. The edge's `kind` is not checked against what the read does; review
  still owns `W` versus `R`.
- When the next module declares a contract, its Platform readers become exceptions at
  once. That slice lists its implementations and registers or moves the rest.

## How it is enforced

- `CompatibilityExceptionRealTreeTests.RealSourceTree_EveryCompatibilityExceptionIsRegistered`
  runs the walk and fails on registry errors, compile errors, unresolved reads,
  undeclared reads, tables a row does not name, and stale rows. Its output lists every
  read with the table and the allowance that admitted it.
- `RealSourceTree_CompilesTheWholeSemanticProject` fails if the compiled file count drops
  below 125 (the tree held 156 on 2026-10-02), or if the walk stops seeing the `DbSet`
  declarations and Finance's listed implementations.
- `CompatibilityExceptionTests` covers each read shape, each allowance and its limits,
  table ownership, each registry error, generic helpers and the referencing projects
  on fixtures.
- Against the committed tree, each of these turned the real-tree test red: deleting a
  row's `deleteWhen`, deleting its `owner`, deleting the probe's `db.Expenses` line
  (stale row), adding `db.Expenses.AnyAsync` to `PaymentRepository` (undeclared),
  adding `db.Expenses.Count()` to `ListAccountsCliCommand` (undeclared), an aliased
  `Set<E>()` in Api and in AppHost, a Platform `IExpenseRepository` implementer with an
  unrelated category read, an extra category read in `EnsureExpenseAsync`, a generic
  `Set<T>()` helper, a generic `ExpenseRepository<T>` beside the listed type, a
  `DbSet` getter that counts before returning the set, and `SalesOrder.TotalAmount`
  mapped to a Finance-owned table of its own.

## Amendment, 2026-10-06: no `Implementations` allowance (#1087)

Repositories moved into their modules (`Infrastructure/Modules/<Owner>/Repositories`, #1097), so they read their tables as the module's own code. The `Implementations` list and the "declared implementation" allowance are deleted (#1099). A read is allowed for the module's own types, a type its edge's `Symbols` names, and a `DbSet` property whose whole body is `Set<T>()`; every other read still needs a compatibility-exception row.
