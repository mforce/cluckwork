# Move the architecture and tenant-bypass rules from data files to typed C# registries (#859)

> **Rule** — the module-ledger, adapter, table-owner and tenant-bypass rules in
> [`AGENTS.md`](../../AGENTS.md) and [`src/AGENTS.md`](../../src/AGENTS.md) keep their meaning; this file
> records how their rows move from three data files into C# without changing behaviour.

**Status:** accepted
**Date:** 2026-10-04

## What happened

No incident. On 2026-10-04 the owner narrowed #859 from an assembly split to typed C# registries.
The evidence was that 64 failed PR runs since #842 included one boundary-guard failure, and that was an
unsupported handler shape (#874), not an accidental crossing. The rules stay decisions that tests check.
Only their storage changes. Before #859 they lived in a JSON module ledger, a tenant-bypass JSON
allow-list and a TSV of filter-free-set classifications; the rows become C# data in
`tests/Cluckwork.Application.Tests`.

The plan accepted on 2026-10-04 splits step 1 into three slices, one PR each:

| Slice | Change | Deletes |
|---|---|---|
| S1 | Loader seam: scanners take a `ModuleLedger` or an allow-list instead of a path; one accessor per registry; fixtures build records; `ModuleLedger.Validate` holds every value rule, `Parse` only the JSON shape | nothing |
| S2 | All nine ledger sections move to `RealModuleLedger.*.cs`, one commit per section, then the JSON ledger, `Parse` and the JSON-shape fixtures go | the JSON ledger |
| S3 | The allow-list and the filter-free-set classifications move to `BypassAllowList.cs` and `FilterFreeSetSites.cs` | both tenant files |

## The rule

Real rules are read only through `RealModuleLedger.Value`, `BypassAllowList.Entries` and
`FilterFreeSetSites.All`. A test that needs rules builds records and passes them through
`ModuleLedger.Validate`; it never writes a JSON file. A data move changes no rule, no #632 identity or
token hash, no reason text and no diagnostic. Each data PR first proves in CI that the C# rows equal the
file they replace, then switches the accessor and deletes the file in a later commit. If a rebase changes
the file's rows, the parity run is repeated in CI before the switch.

## Why not the obvious alternative

An attribute on each `src/` type would let a row disappear with its type, so a stale row could no longer
fail. That is a behaviour change. It would also touch about 250 `src/` files and need either Roslyn
attribute extraction or a reference to `Cluckwork.Api`, which the test project does not have. Tenant-bypass
approvals must stay apart from the code they excuse, as the allow-list rows in the test project are.

Owner names stay strings. `Validate` already rejects an unknown owner with a message; an enum would make
that message unreachable.

## What this does NOT cover

- The remaining compatibility exception, `UserRoleAssignmentRepository.ListByNameByUserAsync` with
  `deleteWhen: "#859"`, moves byte-identical. Removing it is still #859's job (see
  [858](858-platform-composition.md)).
- No analyzer, no assembly split and no policy change. Known ledger gaps (`FarmModule` reading
  `DiscountCeiling`, Access reaching Commerce through `IIdentityProvider`, seven import-scope rows) stay as
  they are.
- During S1 only, malformed JSON can produce slightly different registry errors, because `Validate` sees
  records, which cannot tell a missing value from an empty one. A missing `kind` reads `kind ''` instead of
  `kind '<missing>'`. A non-object owner, edge or compatibility row also reports the checks its blank
  placeholder fails. Shape errors are listed before value errors. The changed error sets occur only in
  synthetic malformed inputs; no real row and no test assertion produces one. The ordering change does
  reach two existing `TableOwnerTests.MalformedRows` fixtures, `"tables": {"Red": [null]}` and
  `"tables": {"Red": 5}`, which report the same two errors in reverse order; both assert with `Contains`.
  S2 deletes the JSON path.

## How it is enforced

The existing guards: `ModuleLedgerRealTreeTests`, `AdapterReachRealTreeTests`, `PeerContractRealTreeTests`,
`AdapterTierRealTreeTests`, `CompatibilityExceptionRealTreeTests`, `TableOwnerRealModelTests`,
`CouplingMatrixRealTreeTests` and `TenantBypassRealTreeTests`. S1 was measured as follows:

- A copy of the old loader and `Validate(Parse(path))` read 95 inputs: every ledger file the 429-test
  suite writes, the real ledger and a missing path. They produced equal records and equal error sets for
  all 95.
- Every ledger the suite builds was serialised, records and sorted errors, at base and at head. All 92 base
  ledgers occur at head. The two extra head ledgers are the new blank-table-name and blank-FK-name fixtures.
- The mutation set below turns the same tests red with the same message at base and at head.

| Id | Section | Mutation | Red test | Message |
|---|---|---|---|---|
| M-a | `src/` | a Finance type references `Domain.Sales.DiscountCeiling` | `ModuleLedgerRealTreeTests` | "undeclared cross-owner edge Finance -> Commerce" |
| M-b | adapters | drop `ListAccountsCliCommand.RunAsync` | `AdapterReachRealTreeTests` | "undeclared adapter reach Cluckwork.Api.Cli.ListAccounts…" |
| M-c | `src/` | an unlisted `IgnoreQueryFilters` in Infrastructure | `TenantBypassRealTreeTests` (allow-list) | "unexcused bypass" |
| M-d | `src/` | reorder the operands of `AccountScopedUserValidator`'s classified `db.Users` predicate | `TenantBypassRealTreeTests` (filter-free) | "unclassified filter-free-set candidates" |
| M-e1 | allow-list | drop row 1 | `TenantBypassRealTreeTests` (allow-list) | "unexcused bypass" |
| M-e2 | filter-free sites | drop the validator's row | `TenantBypassRealTreeTests` (filter-free) | "unclassified filter-free-set candidates" |
| M-f1 | foreignKeys | drop row 1 | `TableOwnerRealModelTests` | "undeclared cross-owner foreign key FK_AspNetUsers_Accounts_AccountId" |
| M-f2 | compatibilityExceptions | drop the row | `CompatibilityExceptionRealTreeTests` | "undeclared compatibility exception" |
| M-f3 | adapterTiers | drop the MCP row | `AdapterTierRealTreeTests` (2 of 3 red) | "`McpTierRow_IsDeclared`" |
| M-g | tables | drop Finance `ExpenseCategories` | `TableOwnerRealModelTests` | "table 'ExpenseCategories' … has no owner" |
| M-h | tableOwnerOverrides | drop `AspNetRoleClaims` | `TableOwnerRealModelTests` | "disagrees with CLR namespace owner <unowned>" |
| M-i | owners | drop `IFinanceModule` from Finance's contract | `AdapterReachRealTreeTests` | "IFinanceModule at src/Cluckwork.Api/Endpoints/Expenses/ExpenseEndpoints.cs" |
| M-j | edges | drop the first symbol of edge cell 1 | `ModuleLedgerRealTreeTests` | "undeclared cross-owner edge Access -> Commerce" |
| M-k | adapterRoots | empty `persistenceForbiddenNamespaces` | `AdapterReachRealTreeTests` | "adapter registry error: adapterRoots declares no persistenceForbiddenNamespaces" |

S2 was measured as follows:

- Push 1 copied the rows into the `RealModuleLedger.*.cs` files one section per commit, with a parity test
  comparing the `System.Text.Json` serialisation of the file's sections and of the C# rows. It passed in CI
  at `ddfb37ea` (run 37237425922). One-character edits to three rows turned it red.
- Push 2 switched `RealModuleLedger.Value` to the rows and deleted the file, `Parse` and the parity test.
  The row mutations above (M-b, M-f1, M-f2, M-f3, M-g, M-h, M-i, M-j and M-k), applied to the C# rows, and
  the `src/` mutations M-a, M-c and M-d turn the same tests red with the same messages.
- The coupling matrix regenerates with only its first line changed.
