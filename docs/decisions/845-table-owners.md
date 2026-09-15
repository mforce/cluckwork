# Walk table ownership from the EF model (#845)

> **Rule:** the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md).
> This record explains the enforcement boundary for epic #514, slice 3, Track B.

**Status:** accepted
**Date:** 2026-09-14

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

Declare every distinct EF table exactly once under an existing owner in
`Architecture/Data/module-ledger.json`. Use the relational table name, qualified
by schema when the schema is not `public`. Keep the table owner consistent with
the CLR namespace's longest matching owner claim, treating exact claims as
subtree claims for this check. Record any design exception in
`tableOwnerOverrides` with a reason. Declare each cross-owner foreign key that
does not touch Platform by its actual constraint name, source owner, destination
owner, and reason. Missing, duplicate, contradictory, malformed, or stale rows
fail the guard. A walk below 30 tables also fails, so a partial model cannot
quietly replace the production model.

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
Insights owns no tables and has no `tables` entry.

Platform tables' internals and FKs touching Platform remain untracked by the
cross-owner dependency guard. Platform's four mapped tables still have ownership
rows because completeness applies to every mapped table. EF history, raw-SQL
objects absent from the model, query behavior, transaction boundaries, and
permission to mutate another owner's data are outside this guard. A reasoned
FK row records a database constraint, not write authorization.

An override exempts that table's namespace consistency check. Its reason and
continued need require review. The scanner rejects blank, duplicate, and
unmapped override rows, but cannot prove the prose is correct.

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
