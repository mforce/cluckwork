# Register every read of a contracted module's tables from outside it (#850)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md);
> this file records what counts as an exception and the limits of the guard.

**Status:** accepted
**Date:** 2026-10-02

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

## The rule

An exception is any member that obtains a `DbSet<T>` whose entity `T` belongs to a
module with a ledger `contract`, unless one of these holds:

- the member's top-level type belongs to that module;
- the type's owner has a ledger edge to the module and the edge's `symbols` names the type;
- the type, or a type containing it, implements an interface the module owns, as
  `ExpenseRepository` implements `IExpenseRepository`;
- the member is a `DbSet<T>` property declared on a `DbContext`.

Every other read needs a row under `compatibilityExceptions` in `module-ledger.json`.
A row is keyed by namespace, type and member, never by `file:line` (#632). It names
the module it `reaches`, an `owner` from the ledger's owners, a `reason`, and
`deleteWhen`, the slice issue that removes it. `deleteWhen` must match `^#[0-9]+$`, so
a date cannot stand in for an owner.

The guard finds exceptions by walking the code, not by reading a list. A new read
fails with the row to add. A row whose read has gone, or whose member now falls under
one of the allowances, fails as stale.

## Why not the obvious alternative

**A hand-kept list checked only for completeness of its fields.** That proves every
row is dated, but not that every exception has a row. The issue's own table had
drifted from the code before this slice.

**A syntax walk like the other ledger scanners.** A DbSet read is a property access
on a receiver. Syntax cannot tell `db.Expenses` from `counts.Expenses`, and the
seeder contains both. The guard therefore compiles `src/Cluckwork.Infrastructure` from
source against the test's references and binds every expression. A compile error
fails the guard, because a member that does not bind is a member the walk cannot see.

**Guard every module's tables.** Only a module with a contract claims "reach me only
through these types". Applying the rule to uncontracted modules would register every
repository in `Cluckwork.Infrastructure.Repositories` as an exception. The rule widens
as each contract slice declares `owners.<Module>.contract`.

**Widen `IFinanceModule` with count and existence reads for the seeder.** The #849
audit kept the contract narrow, and the seeder's conversion belongs to #858.

## What this does NOT cover

- `Cluckwork.Api` and `Cluckwork.AppHost` cannot be compiled from the test's
  references. Any project that references `Cluckwork.Infrastructure` is walked by
  name instead: a member access named after a guarded `DbSet` property, or
  `Set<T>()` of a guarded entity. That walk can report a property that only shares the
  name. It finds no reads today.
- Raw SQL (`FromSql`, `SqlQuery`, `ExecuteSql`) naming a contracted table is invisible.
  `TenantBypassRealTreeTests` already classifies every raw-SQL site.
- A read is attributed to the member that obtains the `DbSet`. A helper that returns
  `db.Expenses.AsQueryable()` to a caller registers the helper, not the caller.
- The `Insights -> Finance` edge allows `ReportQueries` and `ExportQueries` because the
  module ledger already lists them. The edge's `kind` is not checked against what the
  read does; review still owns `W` versus `R`.
- When the next module declares a contract, its Platform readers become exceptions at
  once. That slice registers them or moves them behind the contract.

## How it is enforced

- `CompatibilityExceptionRealTreeTests.RealSourceTree_EveryCompatibilityExceptionIsRegistered`
  runs the walk and fails on registry errors, compile errors, undeclared reads and
  stale rows. Its output lists every read with the allowance that admitted it.
- `RealSourceTree_CompilesTheWholeSemanticProject` fails if the compiled file count drops
  below 125 (the tree held 156 on 2026-10-02), or if the walk stops seeing the `DbSet`
  declarations and Finance's port implementations.
- `CompatibilityExceptionTests` covers each read shape, each allowance, each registry
  error and the name-based walk on fixtures.
- Against the committed tree, each of these turned the real-tree test red: deleting a
  row's `deleteWhen`, deleting its `owner`, deleting the probe's `db.Expenses` line
  (stale row), adding `db.Expenses.AnyAsync` to `PaymentRepository` (undeclared), and
  adding `db.Expenses.Count()` to `ListAccountsCliCommand` (undeclared, name walk).
