# Cross-module references are declared in the module ledger (#514, #842)

> **Rule** — the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md);
> this file is the relocated rationale (what shipped, why the short version was
> insufficient, what not to break).

**Status:** accepted
**Date:** 2026-09-14
**Mechanism note (2026-10-04, #859):** the ledger rows now live in the `RealModuleLedger.*.cs` files in `tests/Cluckwork.Application.Tests/Architecture`, not in a JSON file. The rule and its checks are unchanged.
**Mechanism note (2026-10-05, #859):** the owner and edge rows moved again, to assembly attributes in `src/Cluckwork.Domain/Common/Architecture`, so the module-edge analyzer can read them; the tests read them by reflection. See [the module-edge analyzer](859-typed-rule-registries.md#the-module-edge-analyzer).

## What happened

No incident. This is the first slice of the modular-monolith epic (#514) and it is
forward-looking on purpose: it writes down who owns what before anything moves, so
that every later move is checkable.

What did happen is that the epic's own hand-written inventory rotted in five weeks.
The 2026-08 design named four compatibility exceptions; by 2026-09 there were five
(`BusinessRecordModel` arrived with #819). Its `file:line` cites for the seeder's
expense injection had all moved. Its §3.4 coupling matrix marked five cells as
"no coupling" that a source walk shows are real: Farm → Commerce, Access → Commerce,
Commerce → Access (all three introduced by #727's discount ceiling and effective-role
work), GeneralInventory → EggOperations (feed and water usage inject
`IDailyEntryRepository` for provenance) and Access → EggOperations (provisioning
seeds default egg grades). A list maintained by recall goes stale; a walk does not.

## The rule

Every reference from one business module's namespaces to another's is declared in
`src/Cluckwork.Domain/Common/Architecture/ModuleEdges.cs`, as a cell
(`From`, `To`, `Kind`, `Reason`) that lists the fully-qualified top-level types
realising it. `ModuleLedgerRealTreeTests` walks every `.cs` under `src/` with Roslyn
and fails on an undeclared edge, a stale row (a listed type that no longer references
the other owner), a namespace no owner claims, a parse error, a compile error in a
module-owned file, or a file count below the floor. Add a cross-module dependency and the build tells you which cell to extend
and prints the row to paste; remove one and the build tells you which row to delete.
Break the guard by widening a cell's reason instead of reading the code, and the
ledger stops being a document anyone reads.

## Why not the obvious alternative

**A namespace-keyed ratchet.** The design counted references between
`Application.Features.*` folders by namespace. Two of the five "new" edges that
method reports are artefacts of where a file sits: `Customers → Sales` because
`Customer.cs` lives in `src/Cluckwork.Domain/Sales/`, and `Accounts → Media` because
`Domain.Media` is three entity-free image helpers only the farm logo and banner use.
A namespace-keyed ledger demands a justification for both forever, and the owner
learns to write "not really an edge", which is how a registry stops being read.
Keying on owner makes them vanish: both ends of each are the same owner.

**Rows keyed by `file:line`.** #632 measured what that costs: three unrelated changes
re-pinned rows whose code nobody touched, and the re-pinning is the moment a reviewer
stops reading and starts pasting. Rows here key on the fully-qualified top-level type.
A reference is charged to the top-level type whose code makes it, so moving the
code that uses another module out of a type leaves a stale row that says exactly
which type stopped depending on what.

**A new `Cluckwork.Architecture.Tests` project**, as the design's §8 proposed. The
Application test project already carries `Microsoft.CodeAnalysis.CSharp` and a walker
that parses every `.cs` under `src/`, including `Cluckwork.Api`, which the project
does not reference. A new project would cost a CI matrix leg (#775), a
`SolutionTestProjectSplitTests` reconcile, a coverage entry and a lock file, and buy
nothing.

**The ledger under `docs/`.** A file a test reads is code. `changed-paths.mjs`
classifies everything under `docs/` as documentation, and its own comment records why
`specs/` was kept out of that list: a test reads `GLOSSARY.md`. The ledger sits beside
the guard that reads it, wired through `CopyToOutputDirectory` like the tenant-bypass
allow-list, and this record is the prose about it. One copy of the data.

**Claiming the project roots as subtrees.** The two `GlobalUsings.cs` files declare
no namespace, so they fall to their project root, `Cluckwork.Domain` and
`Cluckwork.Application`, and someone has to own those. A subtree claim by Platform
would also swallow every future `Cluckwork.Domain.<Module>` silently, which kills
the unowned-namespace leg exactly where a new module first appears. Platform claims
those two roots through `exactNamespaces`, which match the whole name and never a
prefix of it, so a new `Cluckwork.Domain.Foo` is reported unowned until a ledger row
says who it belongs to. `Cluckwork.Infrastructure` and `Cluckwork.Api` stay subtree
claims on purpose: their sub-namespaces are adapters and persistence, and a new
endpoint folder is Platform by rule.

**A syntax-only walk (replaced in #1071).** The first walk read `using` directives and
qualified names. It charged a file's imports to every top-level type in the file and
never bound a member access, so the ledger carried six rows no code realised and missed
three real references, among them `FarmModule` reading `DiscountCeiling.Percent`. The
walk now compiles each project holding a module-owned file, with the parse options,
implicit usings and references `CompatibilityExceptionScanner` uses, and charges every
type a node binds to, or the type declaring the member it binds to, to the node's
top-level type. That includes calls the compiler chooses without a name in the source:
user-defined conversions, collection-initializer `Add` and the `foreach` enumerator
pattern. Inferred generic arguments are not charged unless a name binds to them. A
sibling project's built assembly older than that project's newest source fails the
walk, because stale metadata can bind a member to the wrong type without a diagnostic. Using directives are skipped, so an unused import is not an edge. A
claimed type's references belong to its claimant. A compile error in a module-owned
file fails the walk, except CS8795, the stub of a source-generated partial method.
Files only Platform owns are never bound, which is why `Cluckwork.Api`, whose packages
the test project does not reference, costs nothing. The `global using` rule stays for
the syntax walks: the adapter-reach and peer-contract guards still resolve names
through a file's own imports.

## What this does NOT cover

**Platform is the free hub.** `Cluckwork.Api.*`, `Cluckwork.Infrastructure.*` except
`Identity`, `Domain.Common`, `Domain.Auditing`, `Application.Common` and the AppHost
are one owner of kind `platform`, and an edge touching a platform owner on either end
is not tracked. That is the design's §3.4 row (adapters write to every module) and
column (every module calls Platform). It means an endpoint, a repository, a seeder or
a job may still reference anything. Narrowing that is Track B: table ownership (#845),
the adapter-privilege ratchet (#846) and the seam-surface guard (#847).

**`kind` is documentation.** `W` (writes through the other owner in one transaction)
against `R` (reads or validates) is not derivable from syntax. The guard checks the
vocabulary, review checks the letter. #848 regenerates the coupling matrix from it.
One cell already disagrees with the design: §3.4 classes Finance → Farm as `W`
because Finance takes a lock-aware currency snapshot from Farm, but the code reads
the account row under `FOR SHARE` and mutates nothing there, the same shape §3.4
classes `R` on Commerce → Farm and Inventory → Farm. The ledger records `R` with the
disagreement in the reason, so the regenerated matrix reflects the code.

**No move is authorised.** The design's status line still governs: no production
refactor is authorised by the ledger. Track C needs the owner's decision.

**Ownership decisions the ledger takes, and that a later slice may revisit:**
`Domain.Media` is Farm, because every caller is the logo or banner path and nothing
else uses it; `Infrastructure.Identity` is Access, per the design's capability
inventory, which is why the Identity services' `Account` loads show as Access → Farm
rather than vanishing into Platform; `Features.Audit`, `Reports` and `Export` are
Insights.

## How it is enforced

`tests/Cluckwork.Application.Tests/Architecture/ModuleLedgerRealTreeTests.cs`, which
runs in the `application` leg of the CI test matrix and in the opt-in pre-commit hook.
The temp-tree tests in `ModuleLedgerTests.cs` pin each failure class on its own named
assertion. Five mutations were recorded red before the claim was made (an undeclared
`using` in a Flocks handler, a deleted Farm → Commerce cell, a symbol no file
realises, a floor above the file count, a new `Cluckwork.Domain.Foo` namespace no
owner claims); their output is attached to the PR that landed this record.
