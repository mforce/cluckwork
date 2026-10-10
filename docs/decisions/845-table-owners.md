# Walk table ownership from the EF model (#845)

> **Rule:** no `AGENTS.md` restates this rule, because a guard, a how-to or the code already carries it.
> This record explains the enforcement boundary for epic #514, slice 3, Track B.

**Status:** accepted
**Date:** 2026-09-14
**Mechanism note (2026-10-04, #859):** the ledger rows now live in the `RealModuleLedger.*.cs` files in `tests/Cluckwork.Application.Tests/Architecture`, not in a JSON file. The rule and its checks are unchanged.
**Amendment (2026-10-05, #1074):** module table owners are now derived from the entities' namespaces, and the hand-written `tables` rows are deleted. Platform tables stay listed by hand. See [Derived owners](#derived-owners-1074).

## What happened

No incident. This is a forward-looking guard on the module ledger introduced
in #842. A namespace ledger alone cannot detect a newly mapped table with no
owner, or a database foreign key crossing an undeclared ownership boundary.

The model walk at base `eb60c89` found 37 distinct tables and 11 cross-owner
foreign keys that do not touch Platform. The committed schema census has 38
tables because it includes `__EFMigrationsHistory`, which `AppDbContext` does
not map. Listing that table in this ledger would create a stale row.

Design §3.3 assigns `UserRoleAssignments` to Access, although its CLR namespace,
`Cluckwork.Domain.Accounts`, resolves to Farm. The design wins through a reasoned
`tableOwnerOverrides` row. Five framework tables also need explicit overrides:
`AspNetRoleClaims`, `AspNetUserClaims`, `AspNetUserLogins`, `AspNetUserRoles`, and
`AspNetUserTokens`. Their CLR namespace is `Microsoft.AspNetCore.Identity`, which
has no owner in the Cluckwork namespace map. They belong to Access under §3.3.

## The rule

Every distinct EF table has exactly one owner: the owner of its entities' CLR
namespace, by the longest matching owner claim, treating exact claims as subtree
claims for this check. Only module owners derive: a table whose entity resolves
to a Platform namespace fails until an override assigns it. Tables are named by
their relational name, qualified by schema when the schema is not `public`.
Record every Platform table and any design exception in
`RealModuleLedger.TableOwnerOverrides` with its owner and a reason. Declare each
cross-owner foreign key that does not touch Platform by its actual constraint
name, source owner, destination owner, and reason. A table with no namespace
owner, entities resolving to different owners, and duplicate, contradictory,
malformed, redundant or stale rows fail the guard. A walk below 30 tables also
fails, so a partial model cannot quietly replace the production model.

## Why not the obvious alternative

A fixed list of CLR entity types misses the next entity someone adds. The EF
model discovers the tables instead. Owned values and shared join mappings do
not count their enclosing table again, but a distinct owned or shadow table
still needs an owner.

The design's illustrative FK list is not a schema census. For example, the model
has no FK from `EggLots` to `Flocks`, or from `UserRoleAssignments` to `Flocks`.
The scanner reads `GetConstraintName()` from actual model foreign keys and
records only constraints that exist. A scalar identifier alone is not a FK.

## What this does NOT cover

This slice makes no schema change and moves no configuration or production
code. It adds no migration, package, or CI change, and authorizes no module move.
Insights owns no tables because no entity sits in its namespaces.

Platform tables' internals and FKs touching Platform remain untracked by the
cross-owner dependency guard. Platform's four mapped tables still have an owner,
each listed by hand, because completeness applies to every mapped table. EF history, raw-SQL
objects absent from the model, query behavior, transaction boundaries, and
permission to mutate another owner's data are outside this guard. A reasoned
FK row records a database constraint, not write authorization.

An override replaces the namespace owner for that table. Its reason and
continued need require review. The scanner rejects blank, duplicate, unmapped,
unknown-owner and redundant override rows, but cannot prove the prose is correct.

## How it is enforced

`tests/Cluckwork.Application.Tests/Architecture/TableOwnerScanner.cs` walks
`IModel` and evaluates ownership and FK declarations. `ModuleLedger.cs` preserves
malformed input as registry errors. `TableOwnerTests.cs` exercises each failure
class against small Npgsql fixture models, including owned, shared, view-only,
and schema-qualified mappings.

`TableOwnerRealModelTests.cs` constructs `AppDbContext` inside each test with an
unreachable database configuration, disabled service-provider caching, a fresh
`TenantContext`, and a fresh `FlockScope`. Reading `context.Model` opens no
connection. Run the guard with:

```sh
dotnet test tests/Cluckwork.Application.Tests --filter FullyQualifiedName~TableOwner
```

The five real-ledger mutations were run red and reverted before the final green
run. Removing `Payments` and removing `FarmLogos` each reported no owner. Adding
`Expenses` under Farm reported both Farm and Finance as claimants. Removing
`FK_Expenses_Flocks_FlockId` reported an undeclared cross-owner FK. Moving
`Payments` to Farm reported disagreement with its CLR namespace owner, Commerce.
The implementation commit records the exact red diagnostics.

## Derived owners (#1074)

The 37 hand-written `tables` rows restated what the namespace map already
decided, because the guard required each row to match its namespace owner unless
an override said otherwise. Only the six override rows recorded decisions.
`TableOwnerScanner` now derives each table's owner from the model and the owner
namespaces, and `TableOwnerReport.Owners` carries the result. Each
`TableOwnerOverride` names its owner, and `CompatibilityExceptionScanner` reads
the derived owners instead of the deleted rows. Before deleting the rows, a
one-off comparison showed the derived owner equal to the hand-written owner for
all 37 tables, so the deletion changed no ownership and no coupling-matrix cell.

Deriving owners has a cost: a move is the new ownership, so the walk cannot
compare it with an earlier owner. What catches a move depends on where the entity
lands:

- **Into a Platform namespace.** This guard fails, because Platform never derives
  an owner. Platform is the free hub for the FK, module-edge and
  compatibility-exception guards, so without this rule nothing caught the move.
  Moving `ApplicationRole` into `Cluckwork.Infrastructure.Persistence` passed
  all 21 architecture test classes at `c7f33f2a`. It now fails with
  `table 'AspNetRoles' ... resolves to a Platform namespace`. A new entity in an
  unclaimed `Cluckwork.Domain.*` namespace lands here too, through Platform's
  exact `Cluckwork.Domain` claim, and fails the same way.
- **Into another module's namespace.** This guard changes the owner and fails
  only through a cross-owner FK row that no longer matches. Moving `FarmLogo`,
  which has no such FK, into `Cluckwork.Domain.Sales` stayed green here.
  `ModuleLedgerRealTreeTests` failed on the old module's references to it, and
  `CompatibilityExceptionRealTreeTests` failed on `FarmLogoRepository` reading a
  Commerce table. A move with no cross-owner FK, no reference left in the old
  module and no outside read of a contracted owner's table passes every guard.
  That is a deliberate module move, and the walk records it.
- **Anywhere, when the table has an override.** The override still decides the
  owner.

"No owner" fires for entities outside every claim, such as Identity's framework
types without an override.

The four Platform rows are hand-written on purpose. An override that names a
module table's namespace owner fails as redundant, so the remaining rows are the
Platform tables and real design exceptions.

