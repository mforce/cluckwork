# T8 #536 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (driver-direct — the driver executes; Phase 11 verification is the gate, not a second implementer).

**Goal:** An enumerating tenant-bypass guard (Roslyn + EF model discovery + committed allow-list) plus a two-farm E2E isolation matrix, per the signed-off design `docs/superpowers/specs/2026-08-22-t8-cross-tenant-isolation-guard-design.md`.

**Architecture:** The guard lives in `Cluckwork.Application.Tests` (no DB needed — `AppDbContext` model builds in-memory; runs in the pre-commit hook). It walks every `.cs` under `src/` with Roslyn, discovers the filter-free entity surface from the EF model, and fails on any unlisted bypass, stale allow-list entry, missing `AccountId` predicate, or wrapper-forwarding bypass. The matrix lives in `Cluckwork.Api.IntegrationTests` (Testcontainers Postgres 18.4): two farms provisioned through `AccountProvisioner`, full egg loop as farm B, negative isolation with farm A driving farm B's real IDs (404 + no-mutation).

**Tech Stack:** .NET 10, xUnit 2, Roslyn (`Microsoft.CodeAnalysis.CSharp` 4.14.0 — the compiler version bundled with .NET 10 SDK), EF Core, Testcontainers.

## Global Constraints

- Warnings are errors (`TreatWarningsAsErrors`). Build must end `0 Warning(s) 0 Error(s)`.
- Nullable enabled, no unused usings — build-breaking.
- NuGet: committed `packages.lock.json` + `--locked-mode` — the CodeAnalysis package bump regenerates the lock file **in the same commit** (AGENTS.md).
- No edits under `src/` unless Part 3 finds a real bypass (⛔ owner decision if so).
- Guard-writing rules (`docs/decisions/407-writing-a-guard.md`): mutation first, claim second; walk everything, exclude deliberately; rebuild between restore and re-run; `touch` the file after restoring a mutant.
- TDD order per task: failing test recorded failing *for the stated reason* before the code that passes.
- `git ls-files <path>` before every "create"; whole-file write onto an existing path is a silent delete.

---

### Task 0: Baseline ledger (BEFORE any guard test exists)

**Files:** none.

- [ ] **Step 1:** `dotnet test Cluckwork.sln -c Release` (Docker up) — record the **full-solution** total test count as the ledger baseline. This is the number Task 8 compares against. A filtered or mid-slice number makes the delta vacuous (review finding F6).
- [ ] **Step 2:** Record in the PR draft: baseline count, date, commit.

### Task 1: Guard scaffold — discovery floor + Roslyn walk skeleton

**Files:**
- Modify: `tests/Cluckwork.Application.Tests/Cluckwork.Application.Tests.csproj` (add `Microsoft.CodeAnalysis.CSharp` **5.0.0** — the Roslyn the .NET 10 SDK 10.0.400 actually bundles (C# 14 parser; review M2: 4.14.0 is C# 13 and would silently drop C# 14 syntax as error nodes) + `Cluckwork.Infrastructure` project reference; regenerate all affected `packages.lock.json` in the same commit)
- Create: `tests/Cluckwork.Application.Tests/TenantBypass/TenantBypassGuardTests.cs`
- Create: `tests/Cluckwork.Application.Tests/TenantBypass/GuardScanner.cs` (internal walker; keeps the test file readable)
- Create: `tests/Cluckwork.Application.Tests/TenantBypass/AllowList.cs` (loads/validates the JSON)
- Create: `tests/Cluckwork.Application.Tests/TenantBypass/Data/tenant-bypass-allowlist.json` (starts `[]`)

**Interfaces:**
- Produces: `GuardScanner.Scan(ReadOnlyMemory<string> srcRoot, ReadOnlyMemory<string> allowListPath) → GuardReport` where `GuardReport` has `Occurrences` (each: `Kind`, `File`, `Line`, `EnclosingSymbol` (display string `Namespace.Type.Method(params)` or `…Method.Local(localName)`), `PredicateHasAccountId` (nullable)), `AllowListEntries`, `UnmatchedEntries`, `Errors`.
- Produces: `AllowList.Load(path) → IReadOnlyList<AllowListEntry>`; entry = `{ Symbol, File, Justification }`.

- [ ] **Step 1: Red test — discovery floor.** `DiscoveredSurface_Floor`: build the model via
  `new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=x;Username=x;Password=x").Options` + `new TenantContext()` (no connection opened — model only), then
  `model.GetEntityTypes().Where(e => e.GetDeclaredQueryFilters() is null).Select(e => e.ClrType.Name)` must equal the **sorted** set: `ApplicationRole`, `ApplicationUser`, `IdentityRoleClaim`, `IdentityUserClaim`, `IdentityUserLogin`, `IdentityUserRole`, `IdentityUserToken`, `RefreshToken` *(8 types — the 4 Identity claim/login/token tables have NO AccountId column, so leg 3's predicate rule does not apply to them; the stated rule for them is: any query against these sets is a bypass occurrence requiring an allow-list entry, full stop — review finding F7)*.
  - **API note (review M1, verified):** `IReadOnlyEntityType.GetQueryFilter()` is `[Obsolete("…Use GetDeclaredQueryFilters() instead.")]` in the resolved EF Core 10.0.11 (message string confirmed in the shipped dll). With `TreatWarningsAsErrors`, the old call is a build error. `GetDeclaredQueryFilters()` is declared-only and returns the keyed collection (EF 10 named filters); for `HasQueryFilter(e => …)` the declared set is non-empty, so `is null`-ness is equivalent here. **Task 1 Step 2b proves it:** a throwaway probe asserting `ApplicationUser`'s declared-filter count is 0 and `Account`'s is 1 — if the probe disagrees, the floor is wrong and stops here, not at pin time. Run it; record the failure text (empty list / missing type).
- [ ] **Step 2: Add the package + project reference; regenerate locks; build green.**
  `dotnet restore --locked-mode` after editing csproj must pass; if the lock is stale, `dotnet restore` then commit the updated locks. Build must end `0 Warning(s) 0 Error(s)`.
- [ ] **Step 2b: Probe the discovery API** (review M1): throwaway fact asserting `model.GetEntityTypes().Single(e => e.ClrType == typeof(Account)).GetDeclaredQueryFilters()` is non-empty and `typeof(ApplicationUser)`'s is empty/null. Delete the probe after it passes; the floor test (Step 1) keeps the pin.
- [ ] **Step 3: Red test — walk finds the known occurrences.** `Walk_FindsEveryBannedOccurrenceInSource`: run the scanner against the repo's real `src/` with the empty allow-list; assert the `IgnoreQueryFilters` occurrence count is **≥ 36** (current baseline: 36 code occurrences, 16 in comments — comments must NOT count) and that `src/Cluckwork.Infrastructure/Repositories/EggLotRepository.cs` appears. Record the RED reason (count 0 / exception).
- [ ] **Step 4: Implement `GuardScanner` minimally** to pass. Banned kinds (ALL of them — review M3: the design's Identity ban had no implementing task):
  - `IgnoreQueryFilters` invocations (any receiver);
  - `FromSql*`/`ExecuteSql*`/`SqlQuery` invocations;
  - **Identity string-lookups:** `FindByEmailAsync`, `FindByNameAsync`, `FindByLoginAsync`, `GetUsersInRoleAsync` (any receiver — `UserManager`/`UserStore`/`RoleManager`), any `SignInManager` member invocation, and `UserManager.Users` member access;
  - `db.<FilterFreeSet>` member accesses (leg 1's discovered set).
  Parse each `.cs` via `CSharpSyntaxTree.ParseText(File.ReadAllText)`; enclosing method via `FirstAncestorOrSelf<BaseMethodDeclarationSyntax>()` (local functions: if the call's nearest ancestor is a `LocalFunctionStatementSyntax`, key `EnclosingSymbol` as `ContainingMethod.Local(localFunctionName)`).
  **False-green guards (review M2 — a zero-occurrence result must be indistinguishable from a broken walk):** (a) assert `ParseDiagnostics` is empty for every file (any Error-severity diagnostic → test fails, names the file); (b) scanned-file-count floor: the walk must see ≥ the count of `*.cs` under `src/` minus `bin/`/`obj/` (excluded explicitly by path, and the floor pins that exclusion works); (c) `src/` root resolves from the test's `AppContext.BaseDirectory` by walking up to the directory containing `Cluckwork.sln` — if not found, fail, never default.
- [ ] **Step 5: Green.** `dotnet test tests/Cluckwork.Application.Tests --filter TenantBypass` — all three tests pass; record the summary line.
- [ ] **Step 6: Commit** `feat(test): tenant-bypass guard scaffold — Roslyn walk + model-driven discovery floor`.

### Task 2: Allow-list semantics (unlisted red, stale red, predicate red, wrapper red)

**Files:**
- Modify: `tests/Cluckwork.Application.Tests/TenantBypass/{GuardScanner,AllowList}.cs`, `Data/tenant-bypass-allowlist.json`
- Modify: `tests/Cluckwork.Application.Tests/TenantBypass/TenantBypassGuardTests.cs`

**Interfaces:** consumes Task 1's `GuardReport`. The four assertion methods this task adds: `UnlistedBypass_Fails`, `StaleEntry_Fails`, `MissingAccountIdCompare_Fails`, `WrapperForwarding_Fails` — each takes a temp source tree (written under `Path.GetTempPath()` by the test, never the repo) + an allow-list JSON, so the mutants of Task 5 can reuse them.

- [ ] **Step 1: Red — `UnlistedBypass_Fails`.** Temp file: one method calling `x.IgnoreQueryFilters()` with no allow-list entry → report must carry the occurrence AND the test's assertion `Assert.NotEmpty(report.Occurrences.Where(o => o.Kind == BypassKind.IgnoreQueryFilters))` must drive a would-be build failure. Record RED (scanner doesn't yet evaluate allow-list → no failure signal).
- [ ] **Step 2: Implement allow-list evaluation** in `GuardScanner`: an occurrence is *excused* iff an entry matches `File` (relative) + `EnclosingSymbol` exactly.
- [ ] **Step 3: Green** `UnlistedBypass_Fails`; then **red** `StaleEntry_Fails` (entry for `No.Such.Method` + empty source → `UnmatchedEntries` non-empty), implement stale detection, **green**.
- [ ] **Step 4: Red/green `MissingAccountIdCompare_Fails`.** Temp `db.Users.Where(u => u.Email == "x").ToListAsync()` (no `AccountId` in the predicate) must be flagged; `db.Users.Where(u => u.AccountId == tenant.Id)` must NOT. The check is **shape, not provenance** — say so in the test header (design M4/F4).
- [ ] **Step 5: Red/green `WrapperForwarding_Fails`.** Temp file with `static IQueryable<T> Unfiltered<T>(this IQueryable<T> q) => q.IgnoreQueryFilters();` + a caller in a second method → both the wrapper and the caller must be reported (design M6).
- [ ] **Step 6: Commit** `feat(test): tenant-bypass allow-list semantics — unlisted, stale, predicate, wrapper`.

### Task 3: Populate the real allow-list (the enumeration)

**Files:**
- Modify: `tests/Cluckwork.Application.Tests/TenantBypass/Data/tenant-bypass-allowlist.json` (append entries until the real-tree test passes)
- Create: `tests/Cluckwork.Application.Tests/TenantBypass/TenantBypassGuardTests.cs` → new test `RealSourceTree_AllBypassesAreAllowListed` (runs the scanner against the repo `src/`, asserts zero unexcused occurrences AND zero stale entries)

**Justification policy (owner-signed D3):** the seeder cluster (`DemoDataSeeder`, `SimulationDataSeeder`) shares one justification line — "runs at unresolved tenant by design (#280/#279); every query carries an explicit `AccountId` predicate or is a whole-account operation" — repeated per entry (entries are per-method; the *text* may be shared). Every other entry gets a site-specific line.

- [ ] **Step 1: Red.** `RealSourceTree_AllBypassesAreAllowListed` against the current tree with `[]` — record the RED: it must name the first unexcused occurrence (expect ~36 `IgnoreQueryFilters` + raw-SQL + filter-free-set sites).
- [ ] **Step 2: Enumerate, don't recall.** Run the scanner in a debug print (or a temporary xunit fact) and copy the full occurrence list. For each: read the site, write the justification, add the entry. **The seeder cluster gets the shared line.** Identity methods: the only expected site is `AccountUserDirectory` (per-method entries — M7, NOT by-file). `ListAccountsCliCommand`'s site is already annotated in its header as being on #536's list — carry that justification.
- [ ] **Step 3: Green** — zero unexcused, zero stale. Record the entry count (expect ~40–50).
- [ ] **Step 4: Commit** `feat(test): tenant-bypass allow-list — every current bypass enumerated with justification`.

### Task 4: Raw-SQL `FOR UPDATE` predicate walk

**Files:**
- Modify: `tests/Cluckwork.Application.Tests/TenantBypass/GuardScanner.cs` (new `BypassKind.ForUpdateRawSql`)
- Modify: `TenantBypassGuardTests.cs`

- [ ] **Step 1: Red.** `RawLockStatement_AlwaysNamesAccountId`: for every raw-SQL occurrence (`FromSql*`/`ExecuteSql*`/`SqlQuery`) in `src/` whose literal text contains `FOR UPDATE` **or `FOR SHARE`** (review M4: the same invariant covers `FOR SHARE` — `AccountRepository` line 28), the statement must reference AccountId in a predicate-sound way. The rule (review M4 — the old two readings both failed on real source):
  - the literal text between `WHERE` (or `AND`) and the lock clause must contain either the **quoted column** `"AccountId"`, OR an **interpolation hole whose expression text contains `AccountId`** (e.g. `{tenant.AccountId}`, `{accountId}`);
  - AND the referenced expression/column must plausibly bind the row being locked: for a `WHERE "Id" = {x} AND "AccountId" = {y}` shape, the hole's C# expression must be a parameter that the enclosing method receives or derives from `TenantContext` — checked as: the method's parameter list or body contains a declaration whose name matches the hole's expression (case-insensitive). This is still shape, not provenance — say so in the test header.
  - **Exemption:** a legitimately account-wide lock (none exist today) would need an allow-list entry keyed to the raw-SQL site — the allow-list format extends to `{ kind: "raw-sql", file, symbol, justification }` entries. Zero such entries today; the format exists so the next one is a reviewable line, not a rule edit.
  Current expectation: all pass (the issue's premise — all 12 raw-SQL lock paths carry explicit AccountId per #536). Record RED reason (walk not implemented).
- [ ] **Step 2: Implement** as specified above, including the exemption format.
- [ ] **Step 3: Green; record the count of FOR UPDATE statements checked** (expect ≥ 8 across the 6 repositories).
- [ ] **Step 4: Commit** `feat(test): raw-SQL FOR UPDATE predicate walk — every lock names AccountId`.

### Task 5: Mutation matrix (M8) — recorded run

**Files:** none new (reuses Task 2's temp-tree test methods). A `docs/decisions/536-…` note is NOT created here — the recorded run goes in the PR description.

Named-assertion mapping (review M5 — real-tree mutants red `RealSourceTree_AllBypassesAreAllowListed` / `RawLockStatement_AlwaysNamesAccountId`, NOT Task 2's temp-tree tests; M5's floor mutant is redefined):

| # | Mutant (real tree) | Named test that must red, on its own assertion |
|---|--------------------|------------------------------------------------|
| 1 | `IgnoreQueryFilters()` in a non-allow-listed method | `RealSourceTree_AllBypassesAreAllowListed` — assertion names the file+symbol |
| 2 | Allow-list entry deleted, site kept | same test, assertion names the missing entry |
| 3 | Entry for a nonexistent site added | `StaleEntry_Fails_RealTree` (new thin fact: loads the real allow-list, runs stale check) |
| 4 | `AccountId` comparison dropped from a real `db.Users` query | `RealSourceTree_AllBypassesAreAllowListed` — predicate assertion names the site |
| 5 | `HasQueryFilter` **added** to `ApplicationUser` in a throwaway probe (NOT a rename — that cascades across src/; review M5) | `DiscoveredSurface_Floor` — the pinned 8-name set changes |
| 6 | Bypass token written in a comment | **stays green** (false-positive control) |
| 7 | Bypass moved behind a wrapper extension (real tree) | `RealSourceTree_AllBypassesAreAllowListed` — wrapper assertion names both sites |

- [ ] **Step 1:** Green baseline: full guard subset + record.
- [ ] **Step 2–8:** For each mutant above: apply to the **real tree** (label in place `// MUTANT M<n>: …`), run the named test, record it red **on its named assertion** with the message, restore, `touch` the file, **rebuild**, re-run green.
- [ ] **Step 9:** `grep -rn "MUTANT" src/ tests/` → empty. Suite total equals Task 5's recorded total.
- [ ] **Step 10: Commit** (if anything changed; likely empty) — the recorded table goes to the PR body.

### Task 6: Two-farm E2E matrix

**Files:**
- Create: `tests/Cluckwork.Api.IntegrationTests/TwoFarmIsolationMatrixTests.cs`
- Modify (append, after `git ls-files` check): `tests/Cluckwork.Api.IntegrationTests/Infrastructure/TestHarness.cs` — helper `ProvisionTwoFarmsAsync()` returning `(farmA, farmB)` with both owners logged in and `MustChangePassword` cleared, using `AccountProvisioner` (NOT `SeedAccountWithUserAsync` — #533 parity).

**Interfaces:** consumes `factory.Services` (DI scope → `AccountProvisioner`), `factory.LoginForAccessTokenAsync`, `factory.CreateAuthedClient`, `client.PostWithKeyAsync` (existing patterns — mirror `TenantIsolationTests`/`TenantScopedLockTests`).

- [ ] **Step 1: Red — provisioning fixture only.** `BothFarmsProvision_AndIsolatedAtRest`: provision two farms, seed one flock+daily-entry in A, assert B's scoped queries see 0 of A's rows and A sees 0 of B's (empty). Record RED (fixture missing).
- [ ] **Step 2: Implement the harness helper.** `ProvisionAsync` per farm (unique slugs `farm-a-…`/`farm-b-…`), login with the returned temp password, `POST /api/v1/auth/change-password` to clear `MustChangePassword`, return authed clients + account ids.
- [ ] **Step 3: Green**, then **extend to the full loop as farm B**: flock → daily entry by grade → submit → egg lots → stock → customer → sales order → FIFO allocation → stock decrement → expense → payment → report → export. After each stage, farm A's client GETs the corresponding list endpoint and asserts 0 rows. Record each stage's RED as it is added (add stages one at a time, TDD-style).
- [ ] **Step 4: Surfaces the spot checks missed:** `GET /api/v1/exports/all` as A contains none of B's data; `GET /api/v1/reports/{production,sales,expenses,profit}` as A over B's date ranges are empty/zero; audit provenance: `GetProvenanceAsync` for B's entities returns nothing to A (drive through the audit endpoints that expose it).
- [ ] **Step 5: Negative isolation.** For farm B's real IDs, farm A drives: sales order confirm + void, egg-lot movement, inventory update, customer update/delete, expense update, flock update, daily-entry update. Each asserts **both** `404` **and** no-mutation (re-read B's row via a B-scoped query before/after; compare). One theory per surface, TDD order (add surface → red on the first failing assertion → implement/fix → green).
- [ ] **Step 6: Commit** `feat(test): two-farm isolation matrix — full loop + negative isolation with real IDs`.

### Task 7: (b)-local pre-runs on the four uncovered FOR UPDATE surfaces

**Files:** none committed unless a surface passes (then its stall test joins Task 6's file as a new fact).

- [ ] **Step 1:** For each of egg lots, inventory lots, accounts, daily entries: replicate the `TenantScopedLockTests` technique (hold B's lock via a plain non-retrying `AppDbContext` + raw `FOR UPDATE`; fire A's real HTTP request at B's row id; check `pg_blocking_pids`).
- [ ] **Step 2:** Record pass/fail per surface. **Pass →** add the test to the matrix (commit with Task 6's file). **Fail →** ⛔ D2 default applies: file a bug against #530 naming the surface, do NOT fix in-slice, do NOT ship a test asserting the stall.
- [ ] **Step 3: Commit** the passing tests only.

### Task 8: Full verification + PR

- [ ] **Step 1:** `dotnet build Cluckwork.sln -c Release --no-restore` → `0 Warning(s) 0 Error(s)`.
- [ ] **Step 2:** `dotnet test Cluckwork.sln -c Release --no-build` (Docker up) → full green; **suite-count ledger**: total before (**Task 0 baseline — full solution, pre-guard**) vs after, delta ≥ added.
- [ ] **Step 3:** `git diff --stat` vs the design's file list; `grep -rn "MUTANT\|\[DEBUG-" src/ tests/` → empty.
- [ ] **Step 4:** Branch `feat/t8-isolation-guard`, push, open PR with: the mutation-matrix table (Task 5), the (b)-pre-run results (Task 7), the suite-count ledger, and the known-uncovered list from the design.
- [ ] **Step 5:** ⛔ Phase 12 review loop (codex, pi/deepseek-v4-flash, and the remaining claude pass — one defect class each: `invariants`, `false-green`, `tenant-isolation`; merge-or-defer verdicts), then ⛔ Phase 13 merge ask to the owner.

## Self-review (post invariants-review, round 1)

- Spec coverage: Part 1 legs 1–3 → Tasks 1–4 (Identity ban now explicitly in Task 1 Step 4 — review M3); M1–M8 → Tasks 1–5; Part 2 → Tasks 6–7; Part 3 → ⛔ gate in Task 6 step 5 / Task 7 step 2; DoD → Task 8 (ledger baseline fixed — F6). No gaps.
- Placeholder scan: clean.
- Type consistency: `GuardReport`/`AllowListEntry`/`BypassKind` defined in Task 1, consumed unchanged in 2–5.
- **Hook budget:** the guard runs in `Cluckwork.Application.Tests` (pre-commit hook). Task 1 Step 5 measures and records the walk's wall time in the PR; if it exceeds the hook's 2s target, moving the guard to its own non-hook test project is ⛔ owner decision, not a silent swap. *(Owner ruled 2026-08-22: proceed as planned.)*
