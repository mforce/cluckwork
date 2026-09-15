# Declare adapter tiers before the surfaces that need them exist (#843)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md);
> this file records the rationale and the limits of the guard.

**Status:** accepted
**Date:** 2026-09-15

## What happened

No incident. This is epic #514, slice 2, Track A, stacked on #846's reach
ratchet. #806 is about to add MCP tool classes under `src/Cluckwork.Api/Mcp/`
that inject repositories directly — `docs/plans/770-mcp-server/01-design.md:112`
sanctions `WaterTools(McpCallContext call, IWaterUsageRepository water)`. #514's
own adapter rule says an adapter may call a module contract or an approved
workflow, not a repository. Both cannot hold at once. Waiting for #806 to merge
and then weakening the reach ratchet to fit it is the failure #407 spent five
review rounds on: a guard read as safety after it had already been loosened to
match the violation. This slice records the exemption on #514's own side,
named and dated, before the code that needs it exists.

## The rule

A tier declares a named privilege a module may not otherwise have, over a
namespace, tied to the surface that will map it and a review issue that gives
the exemption an end date. `adapterTiers` in
`tests/Cluckwork.Application.Tests/Architecture/Data/module-ledger.json` is an
array of `{ namespace, privilege, surface, reason, reviewBy }` rows; today it
holds one, `Cluckwork.Api.Mcp` → `DirectRepository`, surfaced by `MapMcp`,
reviewed by `#806`. `AdapterTierScanner` walks every `.cs` under `src/` for a
type carrying `[McpServerToolType]` (simple, qualified, or `Attribute`-suffixed,
or reached through a `using X = ...;` alias — a file-level or namespace-block
alias in the same file, or a `global using` alias declared anywhere in the same
project) and every invocation of a declared surface call (`MapMcp` today). A tool type
outside every declared tier namespace is red. A mapped surface with no tier row
is red, and the failure prints the JSON row to add. A tier row whose surface is
never invoked and whose namespace holds no type is `Dormant` — informational,
never red, because the row is meant to be committed before the code exists.
`privilege` and `surface` are each a closed set, both read from
`AdapterTier.KnownSurfaces` beside `AdapterTier.DirectRepositoryPrivilege` — a
map from each supported surface to the privilege it grants, today just
`MapMcp` → `DirectRepository`. `AdapterTierScanner` walks invocations of the
same map's keys, so a row naming a surface the scanner does not walk is a
registry error rather than a silently inert exemption. `reviewBy` must match
`^#[0-9]+$`, so an exemption with no end date is a registry error, alongside a
blank or missing field and a duplicated `namespace` or `surface`.

A tier is also an adapter root with a privilege. `AdapterReachScanner` unions
every `adapterTiers[].namespace` into its own namespace roots — so a tool
class's repository parameters are walked for module reach exactly like an
endpoint's — and into its persistence-forbidden set, because `DirectRepository`
authorizes a repository, not a direct `AppDbContext`. A tool class reaching an
undeclared module still needs an `adapters` row; one that takes `AppDbContext`
still fails.

## Why not the obvious alternative

Keying the tier on the folder (`src/Cluckwork.Api/Mcp/` existing) was rejected:
that folder does not exist yet, so a guard keyed on it would be red from the
moment this slice lands until #806 merges, training everyone to ignore it
exactly the way a chronically-red CI check does. `Dormant` is the alternative —
green today, informational, and it goes stale (not red) the day #806 adds a
type under the namespace or maps `/mcp`, which is the signal for that PR's
author to drop the real-tree Dormant assertion rather than route around it.

Binding the tool-attribute detection to the actual
`ModelContextProtocol.Server.McpServerToolType` symbol was rejected for the
same reason the reach ratchet stays syntax-only: no MCP package reference
exists in `src/` yet, so a semantic check has nothing to bind against. A syntax
match on the attribute's last identifier, both bare and `Attribute`-suffixed,
is what a guard that must be committable today can do.

## What this does NOT cover

The walk is syntax-only, exactly like #846's. It matches on the attribute's
last identifier segment, so it cannot distinguish a real
`ModelContextProtocol.Server.McpServerToolType` from an unrelated type of the
same name; that is the same trade #846 makes for `AppDbContext`/`DbContext`.
`SurfaceCalls` matches on the invoked method name only, receiver-independent —
a call to an unrelated `MapMcp` method on a different type would also count.
Today's tree has none, and the ledger's tier row is what a reviewer reads
against the real one when #806 lands. The closed privilege set holds one value
because MCP is the only tier this slice adds; growing it needs the value added
to `AdapterTier.DirectRepositoryPrivilege`'s closed set and a stated reason,
not a silent widening. This slice changes no `src/` code, CI workflow, or
package dependency, and it does not ask #806–#809 to call contracts instead of
repositories — that remains a Track C question this ledger absorbs for now.

## How it is enforced

`tests/Cluckwork.Application.Tests/Architecture/AdapterTierTests.cs` exercises
the scanner on temporary trees: one test per registry error, `ToolTypeOutsideTier`,
`SurfaceWithoutTier`, a green `Dormant` row, a green in-tier type, both attribute
spellings, a file-local alias and a same-project `global using` alias resolving
to the tool attribute, an alias to an unrelated type not matching, a surface
call inside a lambda or local function, and a parse error.
`AdapterTierRealTreeTests` gates the real `src/` tree, confirms the `MapMcp` row
is declared, and pins it `Dormant` today — that last assertion is written to go
stale, not to stay true forever. `AdapterReachTests` adds two integration
cases: a tool class under a tier namespace with an undeclared repository
parameter is red, and one with `AppDbContext` is a persistence violation.

Six real-tree mutations were run against the built scanners and reverted with
`git checkout --` or by deleting the added file. Mutations touching only
`src/` ran with `--no-build`; mutations 2 and 3 edit the ledger's JSON content
file, which `--no-build` does not recopy into the test output directory, so
those two ran after a rebuild. Output files are local evidence under
`/tmp/514/mutations-843/`.

| Mutation | Result | Output file |
|---|---|---|
| `[McpServerToolType] class Probe {}` under `Cluckwork.Api.Endpoints` | RED, `ToolTypeOutsideTier` at `Probe.cs:3` | `1-tooltype-outside-tier.txt` |
| Add `app.MapMcp("/mcp");` to `Program.cs`, empty `adapterTiers` | RED, `SurfaceWithoutTier`, prints the JSON row to add | `2-surface-without-tier.txt` |
| Remove `reviewBy` from the MCP row | RED, registry error: blank `reviewBy` | `3-remove-reviewby.txt` |
| `WaterTools(IWaterUsageRepository water)` under `Cluckwork.Api.Mcp` | RED, undeclared reach `WaterTools.ctor -> GeneralInventory` | `4-tier-namespace-undeclared-reach.txt` |
| Same class taking `AppDbContext` | RED, forbidden persistence type at `WaterTools.cs:3` | `5-tier-namespace-appdbcontext.txt` |
| codex-sol review of #880: a class outside `Cluckwork.Api.Mcp` carrying a locally-declared `McpServerToolTypeAttribute`, referenced only through a `using ToolMarker = ...;` alias | RED, `ToolTypeOutsideTier` at `AdapterTierAliasProbe.cs:7` | `6-alias-outside-tier.txt` |

Run the gate with:

```sh
dotnet test tests/Cluckwork.Application.Tests \
  --filter FullyQualifiedName~AdapterTierRealTreeTests \
  --logger 'console;verbosity=detailed'
```

When #806 lands `/mcp` and its tool classes under `Cluckwork.Api.Mcp`: add each
tool class's repository reach as an `adapters` row exactly like an endpoint's,
and drop `AdapterTierRealTreeTests.McpTierRow_IsDormantToday` — its own failure
is the signal that the row it pins is no longer dormant.
