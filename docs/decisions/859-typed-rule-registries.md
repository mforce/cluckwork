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
  `deleteWhen: "#859"`, moves byte-identical. The last slice removes it; see
  [The last compatibility exception](#the-last-compatibility-exception).
- No analyzer, no assembly split and no policy change. The analyzer came later; see
  [The module-edge analyzer](#the-module-edge-analyzer). Known ledger gaps (`FarmModule` reading
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

## The last compatibility exception

`UserRoleAssignmentRepository.ListByNameByUserAsync` (Access) left-joined a user's assignments to Flock
Management's filtered `Flocks` set, so the flock-scope filter (#613) decided which flock names a caller
saw. The owner approved the replacement on 2026-10-04: `AccessModule.ListFlockAssignmentsAsync` reads
the assignments through `IUserRoleAssignmentRepository.ListByUserAsync`, then names their flocks with
one `IFlockLookup.GetDisplayNamesAsync` call bounded to the list's distinct flock ids. The repository
method, its query tag and the exception row are deleted, `CompatibilityExceptions` is empty, and the
Access to Flock Management edge names `AccessModule`. The owner declined a name cache: names are
per farm and per viewer, a rename must show at once, and #271 allows one serving instance with no
shared cache to invalidate.

`GetDisplayNamesAsync` is ordinary LINQ over the filtered `Flocks` set, as the join was, so both reads
apply the same `AccountId AND flock-scope` predicate:

| | SQL |
|---|---|
| Before | one statement: `UserRoleAssignments` `LEFT JOIN (SELECT Id, Name FROM Flocks WHERE AccountId = @tenant AND (@unrestricted OR Id = ANY(@assigned)))`, ordered by assignment id |
| After | `SELECT * FROM UserRoleAssignments WHERE AccountId = @tenant AND UserId = @userId ORDER BY Id`, then `SELECT Id, Name, Status FROM Flocks WHERE AccountId = @tenant AND (@unrestricted OR Id = ANY(@assigned)) AND Id = ANY(@ids)` |

The differences are the second round trip, which the owner accepted; the `Id = ANY(@ids)` bound to the
list's flock ids; full assignment rows instead of two columns; and no flock read when no assignment
names a flock. The two statements do not share one snapshot, so a flock renamed between them shows its
newer name.

`NamedRowProjectionTests.FlockAssignmentList_IsIdenticalForEveryViewer` was written first and passed
against the join. It pins the exact rows, in assignment-id order, for an Owner, a Worker scoped to one
flock, another farm and a user with no assignments, over farm-wide, Active, Archived, Depleted, missing
and other-farm flock rows. A row keeps its flock id whether or not the viewer may see the name.
`AssignmentNames_AreOneBoundedFlockReferenceRead` replaces the single-`LEFT JOIN` shape guard.

| Mutation | Red test | Message |
|---|---|---|
| The lookup drops the flock-scope filter but keeps the tenant | `FlockAssignmentList_IsIdenticalForEveryViewer`, `AssignmentProjection_RespectsFlockScopeForAWorker` | the scoped Worker sees the Depleted and Active names |
| One flock id left out of the lookup | `AssignmentNames_AreOneBoundedFlockReferenceRead` and two assignment-name tests | expected 3 bound ids, actual 2 |
| One lookup per row | `AssignmentNames_AreOneBoundedFlockReferenceRead` | three tagged flock reads where one is expected |
| The exception row restored | `CompatibilityExceptionRealTreeTests` | "stale compatibility exception … (its trigger was #859)" |
| The repository joins `Flocks` again | `CompatibilityExceptionRealTreeTests` | "undeclared compatibility exception …ListByNameByUserAsync -> FlockManagement" |

## Allow-list rows carry a token hash (#1072)

This section records a policy change made after #859; the moves above changed no policy. Each
`BypassAllowList` row was keyed by file and symbol, so an edit to an excused query, including one that
widened its predicate, kept CI green. Each row now also carries `Hash`, and a row excuses a bypass only
when file, symbol and hash all match.

`Hash` is `GuardScanner.TokenHash`, the #632 hasher behind the filter-free-set signatures: 8 hex
characters of SHA-256 over the Roslyn tokens, joined by single spaces, with literals kept exactly.
Comments and whitespace are trivia, so they change nothing. Only the scope differs. A filter-free-set
site hashes its innermost statement, because its key names one query. An allow-list row excuses every
bypass in its member, so its hash covers the method its symbol names, otherwise the property accessor
or property, otherwise the whole file for top-level statements and field initializers. A row for a
local function (`Method.Local(Query)`) hashes the whole method around it, because the function can
capture that method's locals. Passed as a method group, as in `ExecuteAsync(Query)`, it has no
forwarding call site, so hashing only the function would leave a captured `Expression` predicate
outside the fingerprint (security review of #1077). The function's name is hashed before the method's
tokens, so its row stays distinct from the row of a method that calls it directly; without the name,
both rows took one hash and the uniqueness check refused a valid pair (re-review of #1077). A statement scope would have missed
`ExecuteLineageFenceAsync`, whose SQL is a `const string sql` declared in another statement, and any
`Where` applied to an unfiltered query in a later statement. Each member has one hash, so no ordinal is
needed. No attribute exclusion exists in the #632 hasher, so attributes on the member count as tokens.

An edit to the member un-excuses its bypasses. The failure prints the row to paste, with the new hash,
and marks the old row stale with both hashes. Two rows sharing a hash are a registry error.

| Mutation | Base | Head |
|---|---|---|
| `FlockRepository.GetByIdForFlockScopedWriteAsync`: `f.AccountId == accountId` becomes `f.AccountId == f.AccountId` | green | red: stale `cfe394af → 4c0a1894`, unexcused with the row |
| `AccountRepository.GetCurrentLockedAsync`: SQL literal `"Id" = {tenant.AccountId}` gains `OR TRUE` | green | red: stale, unexcused `IgnoreQueryFilters` and `RawSql` |
| `AccountProvisioner.ProvisionAsync`: `account.Slug == input.Slug` becomes `== "default-farm"` | green | red |
| `ExecuteLineageFenceAsync`: the `const sql` gets `"RevokedAt" IS NOT NULL` | green | red |
| Comment lines added inside the `FlockRepository` query | green | green |
| The same query reflowed onto one line | green | green |
| Two rows given one hash | not applicable | red: `duplicate allow-list hash cfe394af` |
| Uniqueness check removed from `GuardScanner.Scan` | not applicable | `TwoRowsSharingAHash_FailClosed` red |
| Hash dropped from excuse matching | not applicable | `EditingAnAllowListedMember_UnexcusesItUnlessOnlyTriviaChanged` red on its three token edits |
| `GetByIdForFlockScopedWriteAsync` refactored into a local `Query()` passed to `ExecuteAsync(Query)`, its `.Local(Query)` row pinned, then only the captured `Expression` predicate edited | not applicable | red: stale `.Local(Query)` row (green when the hash covered only the local function) |
| Hash scope put back to the innermost local function | not applicable | `EditingAPredicateCapturedByALocalBypass_UnexcusesIt` red |
| The same method calls `Query()` directly; its row and the `.Local(Query)` row both pinned | not applicable | green, hashes `cf7a373c` and `18d622e7`; then the captured predicate edited: both rows stale |
| The local's name dropped from its hash | not applicable | `ALocalBypassAndItsDirectCaller_CanBothBeApproved` red (duplicate hash) |

The hash proves only that a reviewer saw these tokens. It does not see inputs outside the member, such
as a class-level constant, a helper the member calls or a wrapper in another file. Forwarding wrappers
keep their own rows, each with its own hash.

## The module-edge analyzer

On 2026-10-05 the owner added a Roslyn analyzer for compile-time and editor feedback on module edges and
namespace ownership only. Every architecture test stays as it was and remains the CI authority; the tests
never call the analyzer. The spike that preceded this (base `17e37869`) and its review measured the design
below. No incident.

**One copy of the rows.** The analyzer cannot read the test project, so the owner and edge rows moved from
`RealModuleLedger.Owners.cs` and `RealModuleLedger.Edges.cs` to assembly attributes in
`src/Cluckwork.Domain/Common/Architecture`: `ModuleOwners.cs` (whole owner rows, contract, seam and
implementation lists included) and `ModuleEdges.cs`. Domain is the one assembly every module compilation
references. The analyzer reads the attributes from source while compiling Domain and from metadata
elsewhere, so an edited row takes effect in the editor without rebuilding the analyzer.
`RealModuleLedger.Owners` and `Edges` read the same attributes by reflection, so every test keeps its logic
and assertions and only its data source moved. A parity test compared the reflected rows with the C# rows,
in order, by `System.Text.Json` serialisation, and passed before the old files were deleted; a
one-character reason edit turned it red. It ran locally, not in CI. [Module rules beside the code](#module-rules-beside-the-code) later split both files. The adapter, table, tier and
compatibility rows stay in `RealModuleLedger.*.cs` because no analyzer reads them.

Rejected shapes: a data file passed as `AdditionalFiles` brings back the file format this record removed and
needs a parser on both sides; a C# file compiled into both the analyzer and the tests bakes the rows into
the analyzer, so an editor shows stale diagnostics until it reloads the analyzer.

**What it reports.** `src/Cluckwork.Analyzers` ports `ModuleLedgerScanner`'s attribution and #1071's
semantic walk: CW1001 an undeclared edge, with the row to paste; CW1002 a stale symbol; CW1003 an unowned
namespace; CW1000 a compilation that reads no map. A symbol that exists in no assembly is reported only by
`Cluckwork.Api`, the compilation that references every module. The registry validation, the global-using
rule and the file floor stay test-only. The spike's suppression barriers and its measurement toggle were
not shipped: the tests are the authority, so silencing a CW id loses only the early warning.

**Roslyn.** A compiler refuses an analyzer built against a newer Roslyn than itself (CS9057). The central
`Microsoft.CodeAnalysis.CSharp` pin dropped from 5.9.0 to 5.0.0, the compiler in SDK 10.0.1xx; nothing in
the tests needed 5.9. `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing` asks for Workspaces 1.0.1, a .NET
Framework build, so `Microsoft.CodeAnalysis.CSharp.Workspaces` is pinned to 5.0.0 beside it.

| Check | Result |
|---|---|
| Parity on the real tree | the analyzer's realised edges equal `ModuleLedgerRealTreeTests`' `LiveEdges`: 73 and 73, `diff` identical |
| SDK 10.0.112, compiler `5.0.0-2.26422.108` | clean build green; Finance mutation red with CW1001 |
| SDK 10.0.401, compiler `5.9.0-1.26423.113` | same |
| Docker `build` stage, pinned SDK 10.0.401 | clean exit 0; Finance mutation exit 1 with CW1001 |
| `dotnet format analyzers Cluckwork.sln --verify-no-changes --diagnostics CW1001 CW1002 CW1003 --severity error` | clean exit 0; mutated exit 2 with CW1001. Pointed at a single project it skips referenced projects and reports nothing |
| Full rebuild, `dotnet build src/Cluckwork.Api --no-incremental`, 6 alternating pairs after one warm-up pair | median 3.87 s with, 3.79 s without (+0.08 s, 2 %); analyzer CPU about 1.6 s per build across the four compilations, mostly in parallel |
| `Cluckwork.Domain.dll`, Release | 125,952 to 156,160 bytes, the rows' strings |

| Mutation | `dotnet build src/Cluckwork.Api` | Existing test, built with `-p:RunAnalyzers=false` |
|---|---|---|
| Finance `CreateExpenseHandler` gets `typeof(Domain.Sales.DiscountCeiling)` | CW1001 Finance -> Commerce, with the row to paste | `ModuleLedgerRealTreeTests`: undeclared cross-owner edge |
| `EggGradeFloorPolicy` removed from its EggOperations -> Farm row while in use | CW1001, naming the row to extend | same test: undeclared edge |
| `UpdateEggGradeHandler`, which does not reference Farm, added to that row | CW1002 from Application | same test: stale ledger row |
| `GoneHandler`, which exists nowhere, added to that row | CW1002 from Api | same test: stale ledger row |
| New type in `Cluckwork.Application.Features.Nowhere` | CW1003 | same test: unowned namespace |

The analyzer's own tests run the analyzer over a two-project fixture whose Domain compiles the real
`ModuleMapAttributes.cs`: undeclared edge, declared edge, member-access-only reference, stale row, unowned
namespace, and an unused `using` plus `using static` that stays clean. Each has a mutation of the analyzer
that turns it red.

What this does not cover: live squiggles in a GUI IDE were not observed; CW1002 is a compilation-end
diagnostic, so an IDE shows it only with full-solution analysis on. A new module project must add the
analyzer's `ProjectReference` itself.

## Module rules beside the code

On 2026-10-05 the owner decided that a module rule lives next to the code it describes, and stays central
only when it describes a relationship. The two row files above were split on that line. Behaviour did not
change. No incident.

**Contract types carry their own mark.** Each of the 160 contract types declares `[ModuleContract("<Owner>")]`
in its own file, so the owner rows lost their `Contract` lists. `RealModuleLedger` collects the marked types
of Domain, Application and Infrastructure by reflection and lists each owner's contract in ordinal order.
The hand-written order was not kept; no test reads it. The owner name stays explicit rather than derived
from the namespace, so the existing "not in a namespace the owner owns" check still means something. A
mark naming an owner with no `[ModuleOwner]` row is a registry error. `Inherited = false` keeps a marked
record's nested subclasses out of the contract. The analyzer reads no contract today, before or after this
change, so the contract rules are still test-only.

**One rules class per owner.** `src/Cluckwork.Domain/Common/Architecture/Modules/<Owner>.cs` holds one
`internal static class <Owner>ModuleRules { }` carrying that owner's `[ModuleOwner]` row (namespaces,
implementations, seam, claimed types) and its outgoing `[ModuleEdge]` cells. An edge is filed with its
`from` owner, because the code that realises it is in that owner's namespaces. A cell on another owner's
class is a registry error. The suffix keeps clear of the existing `<Owner>Module` facade classes such as
`AccessModule` and `FarmModule`. The classes sit in `Cluckwork.Domain.Common.Architecture`, so no new
namespace segment is added (#985). Domain and Application gained a global using of that namespace,
which is Platform, for the marks.

**Order.** Reflection does not promise declaration order, so `RealModuleLedger` sets it. It orders owners
by design 3.4, which the coupling matrix's rows and columns follow, and orders edges by `from`, then `to`.
That was already the order of the old rows. The analyzer finds the rules classes by walking Domain's types
from source or metadata, so an edited row still takes effect without rebuilding it. The CW1001 and CW1002
messages and the test's undeclared-edge message now name `Modules/<from>.cs` and print `[ModuleEdge(...)]`.

| Check | Result |
|---|---|
| Owners, namespaces, implementations, seams, claimed types and contract sets | identical to the base, compared as JSON with a temporary dump test run on both trees |
| Edge cells | identical to the base, all 22, in order |
| Analyzer edges vs `ModuleLedgerRealTreeTests`' `LiveEdges` | 73 and 73, identical |
| Coupling matrix regenerated | no content change |

| Mutation | `dotnet build src/Cluckwork.Api` | Existing test, built with `-p:RunAnalyzers=false` |
|---|---|---|
| `[ModuleContract]` removed from `IFlockLookup` | green, as before this change | `PeerContractRealTreeTests` and `AdapterReachRealTreeTests`: contract bypass |
| `[ModuleContract("Farm")]` on `IFlockRepository` | green, as before | `AdapterReachRealTreeTests`: not in a namespace 'Farm' owns |
| `[ModuleContract("Fram")]`, no such owner | green | `ModuleLedgerRealTreeTests`: names owner 'Fram', which has no ModuleOwner row |
| FlockManagement -> Farm cell deleted | CW1001, naming `Modules/FlockManagement.cs` | `ModuleLedgerRealTreeTests`: undeclared edge |
| `UpdateEggGradeHandler` added to EggOperations -> Farm | CW1002, naming `Modules/EggOperations.cs` | same test: stale ledger row |
| FlockManagement -> Farm cell moved onto `FarmModuleRules` | green | `ModuleLedgerRealTreeTests`: cell sits on the wrong class |
