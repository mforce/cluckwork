# T8 #536 — Cross-tenant isolation hardening: enumerating guard + two-farm E2E matrix

> Status: DRAFT for owner signoff (Phase 5). Issue: #536. Epic: #530 (Phase 1.6).
> Design review: claude (fresh context, read-only) — 8 merge-blocking findings, all folded in here.
> Method: `docs/decisions/407-writing-a-guard.md` — walk everything, exclude deliberately; mutation first, claim second.

## Goal

Isolation today is proven by spot checks. This slice adds (1) an **enumerating guard** that
fails the build when any tenant-filter bypass in `src/` is not on an explicit, justified
allow-list, and (2) a **two-farm end-to-end matrix** that provisions two farms the way a
customer gets them and proves the whole pipeline is scoped — including negative isolation
with the other farm's real IDs.

Premise (per #536, corrected): the current bypass sites are **not leaking**. This slice is
about *keeping* that true and proving the pipeline, not fixing known holes. If the walk
finds a genuinely wrong bypass, it is fixed here with a failing test.

## Out of scope (per issue)

- `SeedDefaults.FarmId`/`HouseId` — per-account stand-ins; every index is `(AccountId, FarmId, …)`.
- Intra-account authorization (worker reading an unassigned flock) — #388, different axis.
- `DbSet.Find`/`FindAsync` cache path, owned-type navigations, `GetDbConnection()` — named
  below as **known uncovered shapes**; recorded so nobody reads the guard as total.

## Part 1 — The guard (Approach C)

### Mechanism

A new test class `TenantBypassGuardTests` (location: `tests/Cluckwork.Application.Tests` — it
builds `AppDbContext` without a database and needs no Docker, so the pre-commit hook runs
it; NOT the integration project).

Three legs:

1. **Surface discovery from the model, not recall (M1).**
   `new AppDbContext(design-time options).Model.GetEntityTypes().Where(e => e.GetQueryFilter() is null)`
   yields the filter-free entity set (`Users`, `UserRoles`, `Roles`, …). For each, the
   corresponding `DbSet` accessors (`db.Users`, `db.UserRoles`, …) are the banned pattern.
   A floor assertion pins the discovered set's size and names so a model change that
   silently adds a filter-free entity fails loudly instead of vacuously expanding (or
   shrinking) coverage.

2. **Roslyn syntax walk (M2, M3).** Every `.cs` under `src/` is parsed
   (`CSharpSyntaxTree.ParseText`, no compilation needed). Find, in code only (comments are
   structurally absent from the syntax tree — the false-positive control is free, not a
   regex):
   - invocations of `IgnoreQueryFilters` (any receiver);
   - invocations of the banned Identity string-lookups: `FindByEmailAsync`,
     `FindByNameAsync`, `FindByLoginAsync`, `GetUsersInRoleAsync`, and any `SignInManager`
     member;
   - member accesses `db.<FilterFreeSet>` (leg 1's set) and `UserManager.Users` — each
     must carry an `AccountId` comparison in its query (leg 3).
   The enclosing method is `FirstAncestorOrSelf<BaseMethodDeclarationSyntax>` —
   expression-bodied members, local functions and attributes resolve correctly. A bypass in
   a local function is **not** covered by its parent's allow-list entry (the entry names the
   local function's *containing* method only if the call sits in the method body proper;
   local-function calls are their own sites, keyed by `ContainingMethod.LocalName`).

3. **`db.<FilterFreeSet>` predicate check (M4/F4 — shape, not provenance).** Every query
   over a filter-free set must contain an `AccountId ==` (or `.AccountId.`) comparison in
   its `Where`/join predicate. The check proves **shape, not provenance** — it cannot verify
   the compared value is the *resolved* tenant rather than an arbitrary field. The claim is
   scoped to that in the test header and the ADR.

### Allow-list

Committed file `tests/Cluckwork.Application.Tests/Data/tenant-bypass-allowlist.json`
(created-or-append stated in the runbook; checked for existence first).

Entry shape: `{ "symbol": "Namespace.Type.Method(paramTypes) or Namespace.Type.Method.Local(localName)",
"file": "relative/path.cs", "justification": "one line, why this bypass is safe" }`.

Rules (M5):
- **Symbol display names** — `Namespace.Type.Method(System.Guid, ...)`; overloads and
  generics disambiguate.
- **Stale-entry red**: an entry matching zero sites fails the build. A deleted bypass must
  not leave a live exemption.
- **Per-method, never by-file (M7)** — including `AccountUserDirectory` itself, the one file
  where a new unscoped lookup is most likely to be written.

### Wrapper defeat (M6)

One extension method (`static IQueryable<T> Unfiltered<T>(this IQueryable<T>) =>
x.IgnoreQueryFilters()`) would be one allow-listed site with unlimited green callers.
Assertion: any method that forwards an `IgnoreQueryFilters` result (returns its invocation,
or calls `.IgnoreQueryFilters()` and passes the result on) must itself be allow-listed, AND
its callers must be allow-listed or the forwarding method's own body must be the only site.
Practically: the walk treats a forwarding method's call sites as occurrences too.

### Mutation matrix (M8) — each mutant dies on its OWN named assertion

| # | Mutant | Must red on |
|---|--------|-------------|
| M1 | `IgnoreQueryFilters()` added to a non-allow-listed method | `UnlistedBypass_Fails` |
| M2 | Allow-list entry deleted, site kept | same test, assertion names the entry |
| M3 | Entry for a nonexistent site added | `StaleEntry_Fails` |
| M4 | `AccountId` comparison dropped from a `db.Users` query | `MissingAccountIdCompare_Fails` |
| M5 | A filter-free DbSet renamed in the model | floor assertion `DiscoveredSurface_Floor` (not a vacuous pass) |
| M6 | Bypass token written in a comment | **stays green** (false-positive control) |
| M7 | Bypass moved behind a wrapper extension | `WrapperForwarding_Fails` |

Green baseline is recorded before the matrix runs; rebuild between restore and re-run.

### Implementation resolution (2026-08-22, after driver-direct build)

The build converged on **option 1** for the `db.<FilterFreeSet>` leg, which changes two
rows of the matrix above. Documented here so the spec and the shipped guard agree:

- **The `db.<tenant-table>` leg is a STABILITY test, not a shape gate** (Part 1, leg 3).
  The shape check cannot distinguish a by-id / by-hash / caller-scoped query (safe) from an
  unscoped one (a real leak) — the "shape, not provenance" limit the reviewer named.
  Instead of auto-failing on shape, the leg records the 16 candidate sites in
  `Data/filter-free-set-sites.tsv` with the REASON each is scoped, and
  `RealSourceTree_FilterFreeSetSitesAreStableAndClassified` fails only on DRIFT: a new
  unclassified candidate, a disappeared site, or a `needs-review` sentinel. Nothing is
  silently un-banned — a new unscoped `db.<tenant-table>` query cannot land without a
  classification decision. This replaces the `MissingAccountIdCompare_Fails` auto-gate for
  the filter-free set (the raw-SQL predicate gate below still auto-fails).

- **A raw-SQL row-lock predicate walk was ADDED** (M3/M4 lever, now real).
  Every `ExecuteSqlRaw` / `FromSqlRaw` / `ExecuteSqlInterpolated` / `FromSqlInterpolated` /
  `Sql(...)` statement that carries `FOR UPDATE` or `FOR SHARE` MUST name `AccountId` in
  its SQL text, checked independently of the allow-list (the allow-list entry's
  justification *claims* the predicate; this walk *proves* it). All 13 current raw-SQL
  lock queries carry it — confirming the issue's premise. `db.<tenant-table>` by-id/by-hash
  scoping is the part the raw-SQL walk does NOT cover (those are EF queries, not raw SQL),
  which is exactly why they moved to the stability leg.

- **The graphify knowledge graph is NOT the tool for this guard.** Tested: `explain "Users"`
  resolves to the k6 simulation file, the `AppDbContext` node carries entity-type refs (72)
  but no `db.<DbSet>` query call-sites, and raw-SQL string literals / `.sql` files are absent
  (0 nodes). The graph models schema/call relationships, not query call-sites or
  SQL→table edges. Follow-up **#583** scopes the missing code↔table linkage (fed by the
  existing `tools/schema-docs` generator, which already has the table/column truth). The
  guard ships with a Roslyn walk + a documented (not yet enforced) graph-completeness
  cross-check placeholder that activates when #583 lands.

**Mutation results (all run, all red on the named assertion, all reverted):**

| Mutant | Lever | Result |
|--------|-------|--------|
| Unlisted `IgnoreQueryFilters` in a new method | `RealSourceTree_AllBypassesAreAllowListed` | ✅ red — "unexcused bypass … M2MutantUnlisted" |
| `IgnoreQueryFilters` removed from a listed method (stale entry) | `RealSourceTree_AllBypassesAreAllowListed` | ✅ red — "stale allow-list entry … FindBySlugAsync" |
| `AccountId` dropped from a `FOR UPDATE` raw SQL | `RealSourceTree_AllBypassesAreAllowListed` | ✅ red — "raw-SQL row lock missing an AccountId predicate (M4)" |
| Flock query filter removed (filter-free set grows) | `TenantBypassDiscoveryTests` pinned-set `Assert.Equal` | ✅ red — Actual gained `Flock` |
| Syntax-error file in the walk | `ParseError_IsSurfacedNotSwallowed` | ✅ red — `report.ParseErrors` non-empty, `Evaluate` names it |
| File-count floor tautology | `RealTreeFileFloor_IsMeaningfulNotTautological` | ✅ guards — floor (400) < actual (420) and ≥ half the tree |
| Wrapper forwarding `IgnoreQueryFilters` | `WrapperForwarding_Fails` (temp tree) | ✅ red (Task 2) |

Baseline before the matrix: 165 tests green in `Cluckwork.Application.Tests` (149 pre-guard + 16 guard).

### Known uncovered (stated in test header + ADR, per F1)

`FromSqlRaw`/`FromSqlInterpolated`/`ExecuteSqlRaw`/`ExecuteSqlInterpolated`/`SqlQuery<T>`
and raw-connection SQL bypass EF filters outright. The current raw-SQL `FOR UPDATE`/audit
paths (8 files) are covered by the matrix's static predicate walk (Part 2, leg 3), which
asserts every raw-SQL `FOR UPDATE` statement names `AccountId` in its WHERE. `DbSet.Find`
cache path and owned-type navigations remain uncovered — named, not silently accepted.

## Part 2 — Two-farm E2E matrix

One new integration test class `TwoFarmIsolationMatrixTests` in
`tests/Cluckwork.Api.IntegrationTests` (Docker/Testcontainers, as all integration tests).

### Fixture

Two farms provisioned **through `AccountProvisioner.ProvisionAsync`** (not the seeders —
#533's parity guard and this fixture must describe the same farm). Owners log in with the
returned temporary passwords, `change-password` clears `MustChangePassword`, and both get
authed clients. Farm B then drives the full loop:

flock → daily entry (by grade) → submit → egg lots → stock → customer → sales order →
FIFO allocation → stock decrement → expense → payment → report → export.

Then assertions:
- **Visibility:** farm A observes none of B's rows (counts by entity, incl. audit
  provenance via `GetProvenanceAsync`), and vice versa for A's pre-existing seed row.
- **Surfaces the spot checks never covered:** `ExportQueries`, `ReportQueries`, audit
  provenance.

### Negative isolation (the load-bearing half)

Farm A drives requests using **farm B's real IDs** — for every read, update and delete on:
sales orders (confirm/void), egg lots, inventory items, customers, expenses, flocks, daily
entries. Assert **both**:
1. the response is 404 (existence not leaked), AND
2. farm B's rows are byte-identical (nothing mutated) — catches write-through failures the
   #546 interceptor guard exists to stop.

### FOR UPDATE depth (Q1, as reviewed)

- **(a) everywhere:** the 404+no-mutation above covers all six locked surfaces.
- **(b) only where it exists:** `TenantScopedLockTests` already proves sales orders and
  inventory items never block on B's held lock — the matrix does not duplicate it.
- **Static walk:** every raw-SQL `FOR UPDATE` statement in `src/` must name `AccountId` in
  its WHERE clause (the ~20-line check; buys the all-six coverage claim).
- **Local pre-run (reviewer condition):** before the matrix ships, run the (b) technique
  once against each of the other four surfaces locally. Passes → promote into the matrix
  free. Fails → **filed bug, not a skipped test** (and not fixed inside this matrix).
- **Never** assert that A's probe *does* block — that pins a defect into a spec.

### Part 2 implementation resolution (2026-08-22)

The matrix shipped as a single `[Fact]` (`TwoFarms_FullEggLoop_IsMutuallyInvisible`), not a
cross-product of methods — the loop is driven once for B, and the visibility + negative-
isolation assertions cover the surfaces. Provisioning is through
`AccountProvisioner.ProvisionAsync` (both farms), owners log in with the returned temporary
passwords, and `change-password` clears `MustChangePassword` (the first-run flow).

**Two-layer defense (the mutation must defeat BOTH).** The negative-isolation 404 for a
foreign-tenant confirm is upheld by two independent layers:
1. the global query filter scopes `db.SalesOrders` to A's account, so `GetByIdAsync(B's id)`
   returns null → 404 `NotFound`;
2. the confirm handler's post-load `order.AccountId != accountId` check (defense in depth)
   returns `TenantMismatch` → also surfaced as 404 ("to avoid revealing that the resource
   exists").

Bypassing only the filter (1) still 404s via the check (2); removing only the check (2) still
404s via the filter (1). The mutation that reds the matrix is **both**: add
`IgnoreQueryFilters()` to `SalesOrderRepository.GetByIdAsync` AND delete the
`order.AccountId != accountId` line in `ConfirmSaleHandler` — then A's confirm of B's order
proceeds (422 `NotDraft`, already confirmed) and the 404 assertion reds. This was run and
verified (red), then reverted. The test documents this so a future reader does not mistake a
single-layer bypass for the leak the matrix guards against.

**change-password revokes the prior token.** The first-run `change-password` bumps the
credential epoch (#364), revoking every prior session, and returns a FRESH access token in the
body. The fixture rebuilds the authed client from that response's token; reusing the pre-change
token 401s on the next call (this is what the fixture hit before the rebuild).

## Part 1b — Review amendments (invariants reviewer, round 1; all verified against the resolved EF Core 10.0.11 dll)

- **Discovery API:** `IReadOnlyEntityType.GetQueryFilter()` is `[Obsolete(\"…Use GetDeclaredQueryFilters() instead.\")]` in EF 10.0.11 (message string confirmed in the shipped dll; `TreatWarningsAsErrors` makes the old call a build error). Leg 1 uses `GetDeclaredQueryFilters() is null`; equivalence for plain `HasQueryFilter` is proven by a throwaway probe (declared count: `Account`=1, `ApplicationUser`=0) before the floor pins.
- **Filter-free surface is 8 types, not 4:** the four Identity claim/login/token tables (`IdentityUserClaim`, `IdentityUserLogin`, `IdentityUserToken`, `IdentityRoleClaim`) have **no AccountId column** — for them, *any* query is a bypass occurrence (allow-list entry required); the AccountId-predicate rule applies only to the four that carry the column.
- **Roslyn pin:** 5.0.0 (the SDK 10.0.400-bundled compiler, C# 14), not 4.14.0.
- **False-green guards on the walk itself:** zero parse Error diagnostics, scanned-file-count floor with explicit `bin/`/`obj/` exclusion, `src/` root resolved by walking up to `Cluckwork.sln` (fail, never default).
- **Identity ban implemented:** `FindByEmailAsync`/`FindByNameAsync`/`FindByLoginAsync`/`GetUsersInRoleAsync`/`SignInManager` members/`UserManager.Users` are walked banned kinds from Task 1, not an assumption in Task 3.
- **FOR UPDATE walk:** covers `FOR SHARE` too; predicate-sound rule (quoted column OR interpolation hole binding an AccountId-named parameter of the enclosing method); allow-list format extends to raw-SQL site exemptions (zero today).
- **Mutation matrix:** mutants red the **real-tree** tests (not the temp-tree ones); M5 redefined as "add a `HasQueryFilter` to `ApplicationUser` in a probe" (a rename cascades across src/).
- **Ledger baseline:** full-solution count recorded **before** any guard test exists (Task 0).
- **Hook budget:** guard wall time measured at Task 1 Step 5 and recorded in the PR; if it exceeds the hook's 2s target, moving the guard to its own non-hook test project is ⛔ owner decision, not a silent swap.

## Part 3 — What the walk may fix

If Part 1's walk (or Part 2's local pre-runs) finds a genuinely wrong bypass: fix it here,
with a test that fails without the fix, and record it in the PR. Expected: zero, per the
issue's premise. Anything larger is ⛔ owner decision (it would be fixing a hole in a
surface another slice owns — check the #530 slice list first).

## Files (create-or-append; all checked for existence first)

| File | Action |
|------|--------|
| `tests/Cluckwork.Application.Tests/TenantBypassGuardTests.cs` | create |
| `tests/Cluckwork.Application.Tests/Data/tenant-bypass-allowlist.json` | create |
| `tests/Cluckwork.Application.Tests/Cluckwork.Application.Tests.csproj` | edit: add `Microsoft.CodeAnalysis.CSharp` (test-only; version matched to the .NET 10 compiler, lock file regenerated in the same commit) |
| `tests/Cluckwork.Api.IntegrationTests/TwoFarmIsolationMatrixTests.cs` | create |
| `tests/Cluckwork.Api.IntegrationTests/Infrastructure/TestHarness.cs` | edit (append): two-farm provisioning helper if the matrix needs shared scaffolding |

Do-not-touch: everything under `src/` unless Part 3 finds a real bypass; seeders; the
frozen `InitialCreate` migration; `docs/schema/` (no schema change).

## Verification (Definition of Done)

1. `dotnet build Cluckwork.sln -c Release` — `0 Warning(s) 0 Error(s)`.
2. `dotnet test Cluckwork.sln -c Release --no-build` — full suite green (Docker up).
3. **Mutation matrix (M8)** run and recorded: each mutant red on its named assertion,
   restored, **rebuilt**, green baseline re-confirmed.
4. Suite-count ledger: totals before/after; delta ≥ added.
5. (b)-local pre-run results for the four uncovered surfaces: pass→promoted or fail→filed.
6. No `MUTANT`/`[DEBUG-` markers; `git diff --stat` matches the file list above.

## Open owner decisions (folded or pending)

- **D1 (this doc):** adopt Approach C as specified — pending your signoff.
- **D2:** if the (b)-local pre-run fails for any surface, file the bug against #530 and
  continue (default) vs. fix inside this slice.
- **D3:** allow-list justifications for the seeder cluster (~25 sites) get a shared one-line
  justification ("run at unresolved tenant; every query carries explicit `AccountId`")
  rather than 25 bespoke lines — proposed, cheap to object to.
