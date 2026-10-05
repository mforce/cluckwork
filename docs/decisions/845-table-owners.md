# Walk table ownership from the EF model (#845)

> **Rule:** the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md).
> This record explains the enforcement boundary for epic #514, slice 3, Track B.

**Status:** accepted
**Date:** 2026-09-14
**Mechanism note (2026-10-04, #859):** the ledger rows now live in the `RealModuleLedger.*.cs` files in `tests/Cluckwork.Application.Tests/Architecture`, not in a JSON file. The rule and its checks are unchanged.
**Amendment (2026-10-05, #1074):** table owners are now derived from the entities' namespaces, and the hand-written `tables` rows are deleted. See [Derived owners](#derived-owners-1074).

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
claims for this check. Tables are named by their relational name, qualified by
schema when the schema is not `public`. Record any design exception in
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
cross-owner dependency guard. Platform's four mapped tables still have an owner
because completeness applies to every mapped table. EF history, raw-SQL
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

The cost is that the ownership walk no longer notices an entity moving to another
owner's namespace, because the move is the new ownership. Other guards catch it:
moving `FarmLogo` into `Cluckwork.Domain.Sales` stayed green here and failed
`ModuleLedgerRealTreeTests` (undeclared Farm and Commerce edges) and
`CompatibilityExceptionRealTreeTests` (`FarmLogoRepository` reading a Commerce
table). Where the moved table has a cross-owner foreign key, this guard fails too
through the FK rows. An entity in a new, unclaimed `Cluckwork.Domain.*` namespace
resolves to Platform here through Platform's exact `Cluckwork.Domain` claim.
`ModuleLedgerRealTreeTests` fails that namespace as unowned. "No owner" now fires
for entities outside every claim, such as Identity's framework types without an
override.

An override that names its table's namespace owner fails as redundant, so every
remaining override records a real exception.
