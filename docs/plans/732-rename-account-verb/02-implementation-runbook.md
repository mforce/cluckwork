# Runbook — #732: `rename-account`, a one-shot verb that changes a farm code

You are an autonomous coding agent with FULL tools (read, edit, write, bash) in the `cluckwork` repo
(.NET 10 / EF Core 10.0.11 / Npgsql / Postgres via Testcontainers; React 19 + Vite SPA; cwd = repo root
of the worktree on branch `feat/732-rename-account-verb`). Execute this runbook top to bottom. You do
EVERYTHING: edit, build, test, commit, push, open the PR.

**Mode: feature.** Every behaviour-changing increment is RED-first: run the failing test, record that it
failed *for the named reason*, then apply the code. Generated/registry increments have no red phase.

## Facts this runbook asserts (all read from the base commit — trust these over your assumptions)

Base is `2f6e242f3062cf54f23c2b55c67f611c4190a2cb`. **`main` on your machine may be behind this**; the
worktree you are in is cut from it. Verify in Step 0.

| Claim | Where it was read |
|---|---|
| `CliDispatcher.Commands` is the ONE registration; `ProcessRoles.OneShotVerbs` is *derived* from it (`[.. CliDispatcher.Commands.Select(c => c.Name), HealthCheckCliCommand.Verb]`) — there is **no second verb list to edit** | `src/Cluckwork.Api/Hosting/ProcessRole.cs:44` |
| `Account.TryValidateSlug(string?)` returns `Result<string>`: trims, **rejects** uppercase (does not fold), enforces 3–32 `[a-z0-9-]` no edge hyphen, then rejects `ReservedSlugs`; both branches use error code `Account.SlugInvalid` | `src/Cluckwork.Domain/Accounts/Account.cs:102-116`, pattern `:39` |
| `Result.Failure(Error)` exists (non-generic); `Result` is **not** implicitly convertible to `Result<string>` | `src/Cluckwork.Domain/Common/Result.cs:15-18` |
| `Error.NotFound(resource, id)` → code `"{resource}.NotFound"`, description `"{resource} '{id}' was not found."` | `src/Cluckwork.Domain/Common/Error.cs:7-8` |
| `AccountSuspensionService`'s not-found is `Error.NotFound("Accounts", accountId)` — **plural, and the id, not the slug** | `src/Cluckwork.Infrastructure/Identity/AccountSuspensionService.cs:118` |
| `GetCurrentLockedAsync` selects `WHERE "Id" = {tenant.AccountId} FOR UPDATE` with `IgnoreQueryFilters()` — it is **tenant-keyed**, so `tenant.Resolve(id)` first is a precondition, not a formality | `src/Cluckwork.Infrastructure/Repositories/AccountRepository.cs:33-38` |
| `TenantStampInterceptor` inspects the **`AccountId` property only** (Added/Modified/Deleted). A tracked `Slug` update is unremarkable to it. An **unresolved** tenant disables checking entirely | `src/Cluckwork.Infrastructure/Persistence/Interceptors/TenantStampInterceptor.cs:101-124` |
| `IX_Accounts_Slug` is `UNIQUE ("Slug")` — **one column, global, no `AccountId`**. So a `FOR UPDATE` lock on the source row does NOT reserve the destination code; the index is the authority | `AccountConfiguration.cs:21`, `Migrations/20260818235944_AddAccountSlug.cs:58-62`, `docs/schema/public.Accounts.md:57` |
| `AccountProvisioner.IsSlugConflict(DbUpdateException)` is `internal static` in the **same namespace** (`Cluckwork.Infrastructure.Identity`) — reuse it, do not re-implement | `src/Cluckwork.Infrastructure/Identity/AccountProvisioner.cs:132-137` |
| The provisioner's own shape is `try { AmbientTransaction.RunAsync(...) } catch (DbUpdateException ex) when (IsSlugConflict(ex))` — the catch is OUTSIDE the transaction delegate | `AccountProvisioner.cs:94-128` |
| `AmbientTransaction.RunAsync` → `SingleAttemptExecution` on the owned path: the unit is **never replayed** (#269). Keep the whole unit inside the delegate | `src/Cluckwork.Infrastructure/Persistence/AmbientTransaction.cs:56-70` |
| `IAuditWriter.WriteAsync` appends to the caller's unit of work and **never saves**; it throws on an unresolved tenant **or actor** (#500) | `src/Cluckwork.Infrastructure/Repositories/AuditWriter.cs:29-48` |
| `CurrentUserContext.ResolveSystemActor(label)` lives in `Cluckwork.Infrastructure.Identity`; `SystemActors` constants live in `Cluckwork.Application.Common` | `src/Cluckwork.Infrastructure/Identity/CurrentUserContext.cs:55`, `src/Cluckwork.Application/Common/SystemActors.cs` |
| `AuditVocabularyCoverageTests.AssertActionIsRegistryReference` accepts **only** `AuditActions.X` or `cond ? AuditActions.A : AuditActions.B` as the action argument — a local variable, a parameter or a literal fails | `tests/Cluckwork.Application.Tests/Common/AuditVocabularyCoverageTests.cs:459-467` |
| `AccountSlugLookup.Normalize` folds case + trims (for the **current** code, an operator at a shell). `AccountSlugLookup.ResolveAsync` returns `Guid?` and is allow-listed in the tenant-bypass registry | `src/Cluckwork.Api/Cli/SuspendAccountCliCommand.cs:88-113` |
| The verb's failure contract: exit `1` + one stderr line, never a stack trace; `--slug` missing → `"suspend-account requires --slug <farm-code>."`; unknown → `"No farm with code '<slug>'."` | `SuspendAccountCliCommand.cs:36-56`, `:70-77` |
| Only the **slug** is ever echoed, never the farm **Name** (name is tenant-controlled free text; the slug's regex makes it safe by construction, #560) | `SuspendAccountCliCommand.cs:58-60` |
| Test helpers: `factory.SeedAccountWithUserAsync(email)` creates an account with slug `"farm-" + accountId.ToString("N")[..12]`; `factory.WithTenantScopeAsync(accountId, db => …)` resolves the tenant; `factory.LoginAsync(email)` returns `TokenPairDto` | `tests/Cluckwork.Api.IntegrationTests/Infrastructure/TestHarness.cs:34-52, 158-178, 409-420` |
| The subprocess harness to copy verbatim is `AccountLifecycleCommandTests.StartCommand` (env vars: `ASPNETCORE_ENVIRONMENT=Production`, `ConnectionStrings__Default`, `Database__Provider`, `Database__AllowInsecureConnection`, the four `Jwt__*`) | `tests/Cluckwork.Api.IntegrationTests/AccountLifecycleCommandTests.cs:17-35` |
| `AccountSlugRaceTests` nests two `WithTenantScopeAsync` contexts and calls the **aggregate method** directly — it pins the `Version` token, not the service. Do **not** nest two awaiting locking services (the first can wait on a lock the second holds) | `tests/Cluckwork.Api.IntegrationTests/AccountSlugRaceTests.cs:23-40` |
| `AccountProvisioningTests.ConcurrentProvisioning_UsesTheSlugIndexAsTheAuthority` is the existing "index is the authority" race shape, using a `…SkippingSlugPrecheckForTestAsync` bypass | `tests/Cluckwork.Api.IntegrationTests/AccountProvisioningTests.cs:287-322` |

## Rules

- Transcribe the exact code blocks VERBATIM (comments and whitespace included). Do not reformat, rename,
  or "improve" them. Blocks marked **PROTECTED** are correctness-critical (tenant isolation, audit
  accountability, concurrency): transcribe or **stop**, never repair.
- Run the commands EXACTLY as given. Do not invent flags.
- **`--no-build` is forbidden in every mutation test run**: after planting a mutant you must rebuild or
  the suite exercises the pre-mutation binary. The required mutation rebuild command may use
  `--no-restore` because it still recompiles all changed source; locked restore G3 has already run. A
  mutation row's "Rebuild command run" cell is the proof you did that.
- After every build/test command, if it is not clean, STOP and fix before continuing. **An expected RED is
  a clean result** — but only that exact RED: the command as written, the named test, failing at the named
  assertion, matching the **stable discriminator** the row gives. Generated fragments (GUIDs, timestamps,
  `<generated slug>`) are expected to differ. **Anything else is a STOP, however red it looks**: a compile
  error, a discovery/runner failure, zero tests collected, a *different* test failing, or a baseline
  failure that changed shape.
- **Every gate command a step runs cites its gate row by ID**, never retyped. A filtered invocation names
  the row it narrows.
- If a block here conflicts with an existing test, STOP and report the conflict. Do NOT relax or delete
  that test — it may be pinning the behaviour deliberately, in which case this runbook is wrong.
- **Blocks marked PROTECTED are never edited, for any reason.** If one fails to compile or fails at
  runtime, STOP and report the exact error. Do not fix it minimally, do not adapt it.
- Any OTHER block can fail three ways: (1) doesn't compile → fix minimally, report the error and your fix;
  (2) compiles, fails at runtime because the *block* is wrong → same, and report it prominently as a
  runbook defect; (3) an assertion fails against the product → **report the RED, never widen it**.
- A **mutation check** means: plant the bug on purpose, run the named test, see whether it goes red. RED =
  that test guards that code. GREEN = nothing was watching. Label every mutant in place with
  `// MUTANT M<n>: <what this breaks>` and delete the marker on restore.
- Run the FULL suite (**G2**, in the FOREGROUND) and report its final summary line verbatim. A "complete"
  report ending "waiting for the background run" is not a result.
- Work only on `feat/732-rename-account-verb`. Never commit to `main`.

**Protected-block probe — what was probed about the framework each PROTECTED block hooks into:**
- *`GetCurrentLockedAsync` + `tenant.Resolve`*: probed by reading the SQL inside the repository
  (`AccountRepository.cs:33-38`) **and** the live caller that already depends on the ordering
  (`AccountSuspensionService.cs:88` resolve → `:110` locked read). The claim "the locked read is
  tenant-keyed, so an unresolved tenant matches no row" is proved by that service's own comment, not by my
  inference. The single-assignment throw is read at `TenantContext.cs:22-35`.
- *`TenantStampInterceptor` on a `Slug`-only update*: probed by reading `StampTenant`'s predicate
  (`TenantStampInterceptor.cs:101-124`) — it selects the property **named `AccountId`** and never reads
  `Slug`. No framework event to fire, because no hook is entered: the block's interaction with the
  interceptor is a property-name match, which is textually inspectable. **This is a signature-shape probe,
  not a routing probe** — the block enters no new interception point.
- *`AuditWriter`'s two guards*: both are plain `throw` statements read at `AuditWriter.cs:29-48`, and both
  are already exercised by `AuditActorTests.WriteAsync_WithUnresolvedActor_Throws` /
  `…_AddsNothingToTheChangeTracker` (`tests/Cluckwork.Api.IntegrationTests/AuditActorTests.cs:29-59`),
  which is the "made the event happen" evidence for the actor path.
- *`IX_Accounts_Slug` as the race authority*: not inferred from the index name — read from the migration's
  `CreateIndex(… column: "Slug", unique: true)` (`20260818235944_AddAccountSlug.cs:58-62`), the generated
  `CREATE UNIQUE INDEX` in `docs/schema/public.Accounts.md:57`, and the existing race test that proves the
  translation path end-to-end (`AccountProvisioningTests.cs:287-322`).

**Existing instances of this pattern:** the pattern is *an operator one-shot verb that mutates one account
row under a resolved tenant with an audit row*. **Two existing instances**, both read in full:
`AccountSuspensionService` + `SuspendAccountCliCommand` / `ReactivateAccountCliCommand` (transaction,
`ResolveSystemActor`, audit-before-save, exit-1-with-stderr, slug-only echo), and `AccountProvisioner` +
`ProvisionAccountCliCommand` (cross-account slug pre-read, `IsSlugConflict` translation, `try` outside
`AmbientTransaction`). **How this block differs from them, field by field:** (a) it adds a **post-lock
staleness check** neither has, because a rename's *input* is the mutable value itself and a suspend's is
not; (b) it reads `Slug` before mutating to compute `changed`, where suspension computes `stateChanged`
from `IsActive` (`AccountSuspensionService.cs:130`) — same idea, different field; (c) it catches the
unique violation like the provisioner but on an **UPDATE**, where the provisioner catches it on an INSERT;
(d) it echoes **two** slugs (`old → new`) where suspend echoes one. Nothing in this design is novel.

## Verify prerequisites (run first)

```bash
git rev-parse --abbrev-ref HEAD        # expect: feat/732-rename-account-verb
git rev-parse HEAD                     # expect: 2f6e242f3062cf54f23c2b55c67f611c4190a2cb  (or a descendant with only THIS runbook's commit above it)
git status --short                     # expect: empty
docker info --format '{{.ServerVersion}}'   # expect: a version string; G2 needs Docker
node --version                         # expect: UNVERIFIED (informational, does not gate) — driver's node was v22.x, CI uses its own setup-node
```

**Commit gate (read from `.githooks/pre-commit`, not memory):** it runs
`dotnet test tests/Cluckwork.Domain.Tests` + `tests/Cluckwork.Application.Tests` when `.cs`/`.csproj`/`.sln`/
props are staged, and `cd web && npm run typecheck` when `web/**` is staged. It tests the **working tree**,
and it is **opt-in per clone**: `git config --get core.hooksPath` is **empty in this worktree**, so it will
not fire unless you enable it. **Do not enable it and do not bypass it** — either way, every increment
below must compile and be behaviourally usable at its own commit.

## Caller ledger — one row per increment

| Increment | Contract changed | Every production caller (repo-wide enumeration, below) | What each does AT THIS COMMIT | Same-commit or later? | Observed at that commit — Phase 11 fills |
|---|---|---|---|---|---|
| 1 | `Account.Rename(string?)` — **new** method, no existing caller changes | none — nothing calls it yet | n/a: no caller exists, so nothing can break | same commit (the domain method and its unit tests land together) | |
| 2 | `AccountRenameService.RenameAsync` — **new** type + one new `AddScoped` line | none external — `AccountSuspensionService`, `AccountProvisioner`, `AdminRecoveryService`, `IdentityProvider` are untouched | unchanged behaviour; the DI line adds a registration, it changes none | same commit | |
| 3 | `rename-account` verb — new entry in `CliDispatcher.Commands` | `ProcessRoles.OneShotVerbs` (derived), `CliDispatcher.TryRunAsync`, `Program.cs` dispatch | all three see one more name; `healthcheck` and every existing verb unaffected | same commit as the command class | |
| 4 | `AuditActions.AccountRename` + `SystemActors.RenameAccount` + SPA vocabulary | `AuditVocabularyCoverageTests` (both facts), `AuditPage`'s filter list, `enums.ts` 3 maps, `catalogParity` | server emits the action only from increment 2's service; the SPA list must be extended in THIS commit or `AuditActions_registry_matches_the_SPA_AUDIT_ACTION_VALUES_list` is red | **same commit — increments 2, 3 and 4 are one commit boundary if you reorder them; do not commit increment 2 alone expecting CI green** (see note below) | |
| 5 | tenant-bypass registry rows (test data) | `TenantBypassRealTreeTests.RealSourceTree_AllBypassesAreAllowListed` | red between increment 3 and 5 if increment 3 introduces a new `IgnoreQueryFilters()` site | **see note** | |
| 6 | docs only | `TenancyDocsFreshnessTests`, `SchemaDocsTests` image pins (both walk tracked files) | prose-only edits; must not introduce a bare `postgres:<tag>` or a "tenancy is dormant" phrase | same commit | |

> **Commit policy — never commit a red tree.** Increments 2–5 are one commit boundary because
> `AuditActions.AccountRename` immediately makes `AuditVocabularyCoverageTests` require the SPA value,
> and each new `IgnoreQueryFilters()` site immediately makes the tenant-bypass guard require its registry
> row. Run each increment's narrowed RED/GREEN steps as written, but **skip the commit commands in 2e,
> 3d and 4d**. After finishing increment 5, stage every file from increments 2–5 and make ONE commit with
> subject `feat(cli): rename-account changes a farm code with an audit trail (#732)`. Before that commit,
> G1, the service/CLI/vocabulary/tenant-bypass narrowed suites, G5, G6 and G7 must all be green.

**Repo-wide reference enumeration** (run yourself to confirm, no file-type filter):
`git grep -ln "AccountSlugLookup\|IX_Accounts_Slug\|TryValidateSlug\|ReservedSlugs\|AuditActions\.Account\|farm code\|immutable" -- ':!*/obj/*' ':!*/bin/*' ':!graphify-out/*'`
— the driver's run found: `Account.cs`, `AccountProvisioner.cs`, `AccountSuspensionService.cs`,
`FirstRunAdminService.cs`, `AdminRecoveryService.cs`, `AccountRepository.cs`, `AccountConfiguration.cs`,
`ProvisionAccountCliCommand.cs`, `SuspendAccountCliCommand.cs`, `ReactivateAccountCliCommand.cs`,
`ListAccountsCliCommand.cs`, `RecoverAdminCliCommand.cs`, `AuthEndpoints.cs`, `LoginRequestValidator.cs`,
`AuditActions.cs`, `SystemActors.cs`, `docs/architecture.md`, `docs/decisions/530-*.md`,
`docs/runbooks/provisioning-a-new-farm.md`, `docs/security/log-redaction-policy.md`,
`specs/product/GLOSSARY.md`, `specs/product/specs.md`, `AGENTS.md`, `deploy/.env.example`,
`src/Cluckwork.Api/Hosting/CluckworkTelemetryServiceCollectionExtensions.cs`,
`ServingBootGuards.cs`, `web/src/i18n/{en,es,tl}.ts`, `web/src/auth/farmCodeCache.ts`,
`web/public/theme-init.js`, `web/src/lib/brand.ts`.

## Gate commands — every command COPIED from its source

| ID | Gate | Source (path + job/step) | Command, verbatim | Baseline on `2f6e242` | Clean looks like |
|---|---|---|---|---|---|
| G1 | build | `.github/workflows/ci.yml` → `Build and test` → `Build` | `dotnet build Cluckwork.sln --configuration Release --no-restore` | clean, `0 Warning(s) 0 Error(s)` (driver ran the Debug form: same 0/0) | ends `0 Warning(s)` / `0 Error(s)`; **warnings are errors**, so any warning is a failure |
| G2 | test | `.github/workflows/ci.yml` → `Build and test` → `Test` | `dotnet test Cluckwork.sln --configuration Release --no-build --verbosity normal` | **2304 total, 0 failed** — Domain 365, Application 241, AppHost 10, Integration 1688 (driver-verified; integration standalone in 4 m 27 s) | every assembly `Failed: 0`; **block on a delta from those counts**, not on absolute green |
| G3 | restore (locked-mode) | `ci.yml` → `Restore dependencies` | `dotnet restore Cluckwork.sln --locked-mode` | CI-attested on `2f6e242f3062cf54f23c2b55c67f611c4190a2cb` (job `Build and test`), not driver-verified | no `NU1004`. **This slice adds no package** — a red means you touched a `.csproj` / `Directory.Packages.props` / `packages.lock.json`. On a trip: STOP and report, do not regenerate a lock file |
| G4 | schema docs | `ci.yml` → `Verify schema docs are current` | `tools/schema-docs/generate.sh --check` | `docs/schema/ is up to date.` rc=0 (driver-verified) | same line. **No migration in this slice** — a red means `docs/schema/` was edited, which is do-not-touch |
| G5 | web tests | `ci.yml` → `web` → `Test with coverage gate` | `npm run test:coverage` (run in `web/`) | `Test Files 119 passed (119)`, `Tests 2706 passed (2706)`, coverage `All files 91.03/87.28/86.29/94`, no threshold configured | all files green. **The count RISES by design** (increments 4 and 7 add cases): record the number after each web increment and report the delta. A **fall**, or a `Test Files` count below 119, is a STOP |
| G6 | web build | `ci.yml` → `web` → `Typecheck and build` | `npm run build` (in `web/`) | clean, `WEB_EXIT=0` | clean |
| G7 | service worker | `ci.yml` → `web` → `Verify service-worker guarantees` | `npm run verify:sw` (in `web/`) | `67 shell entries precached, 46 JavaScript assets verified` rc=0 | same shape; asset count may rise only if you added an asset (you are not) |

## Documentation surfaces — filled now, RUN at Phase 11

Locale set is derived from the repo, not typed: `web/src/i18n/index.ts` → `RESOURCES` keys, enumerated by
`catalogParity.test.ts` iterating `RESOURCES` itself. Today that set is `en`, `es`, `tl`.

| Surface | Path / key | Locales | Increment | Verification procedure (run at Phase 11) | Verified by + SHA |
|---|---|---|---|---|---|
| Audit-action label in the Audit UI filter + rows | `web/src/i18n/{en,es,tl}.ts` → `enums:"auditAction.Account.Rename"`; `web/src/i18n/enums.ts` → `AUDIT_ACTION_VALUES`, `AUDIT_ACTION_KEYS`, `AUDIT_ACTION_ENTITY_TYPE` | en, es, tl (from `RESOURCES`) | 4 | `cd web && npx vitest run src/i18n/enums.test.ts src/i18n/catalogParity.test.ts` **and** read `auditActionLabel("Account.Rename")` renders non-empty in each locale via the test's own loop | |
| In-app glossary: farm code no longer immutable | `web/src/i18n/en.ts` → `help:glossaryFarmCodeDef` ("It is lowercase and it does not change.") + `es`, `tl` equivalents | en, es, tl | 7 | `cd web && npx vitest run src/routes/HelpPage.test.tsx` and grep the three catalogs for the old sentence — must be **absent** in all three | |
| In-app help: the new system actor | `web/src/i18n/{en,es,tl}.ts` → `help:auditSystemActors` | en, es, tl | 7 | `npx vitest run src/routes/HelpPage.test.tsx` — the existing test asserts each actor string renders; extend it for `(rename-account)` | |
| Operator runbook | `docs/runbooks/provisioning-a-new-farm.md` §"Renaming the default farm's code" | en (docs are English-only) | 6 | read the section: the `UPDATE "Accounts"` block and the `psql` invocations are **gone**; `rename-account` appears; `grep -n 'postgres:' docs/runbooks/provisioning-a-new-farm.md` returns nothing | |
| Product glossary | `specs/product/GLOSSARY.md` — **two** claims: `:689` ("chosen once and **immutable**") and `:726` ("A farm code is immutable") | en | 6 | `grep -n "immutable" specs/product/GLOSSARY.md` → no line about the farm code being immutable | |
| Decision record + epic | `docs/decisions/732-farm-code-rename.md` (new), `docs/decisions/530-multi-farm-tenancy.md` row 10, epic #530 checklist T10 | en | 6 | `TenancyDocsFreshnessTests` green; the #530 table row points at the new record | |
| PR title (the release note) | PR title only | en | 8 | must be a conventional subject: `feat(cli): …` — below 1.0.0 a `feat:` is a **patch** bump; no `!`, no `BREAKING CHANGE` (this is additive) | |

## Step 0 — confirm where you are

```bash
git rev-parse --abbrev-ref HEAD
git rev-parse HEAD
git status --short
```
## Step 0b — emit the START EVIDENCE BLOCK (your FIRST output; before any edit, before any gate)

```
START EVIDENCE — #732
cwd:                              (from pwd)
branch:                           (from git branch --show-current)
HEAD:                             (from git rev-parse --short HEAD)
baseline .NET total:              (dotnet test tests/Cluckwork.Domain.Tests -v q --nologo 2>&1 | grep "Passed!")
baseline AccountSlugTests cases:  (dotnet test tests/Cluckwork.Domain.Tests --filter "FullyQualifiedName~AccountSlugTests" -v q --nologo 2>&1 | grep "Passed!")
baseline web total:               ((cd web && npx vitest run --reporter=basic 2>&1 | grep "Tests "))
```
Every baseline number is measured, not copied from this file. If any of the four commands errors, STOP and
report the error rather than emitting a blank row. **If you emit a PROCEED BLOCK without this block above
it, your work is unverifiable and will be re-run.**

## Step 0 — verify the base (or a descendant whose
only extra commits are yours), or the tree is dirty, **STOP and report**. Do not pull, do not fast-forward,
and **do not run this anywhere else**: the driver's checkout of `main` sits three commits BEHIND this base
and does not contain #731's raw-SQL runbook section, so a session started there fails increment 6 for a
reason that has nothing to do with its work. Your cwd must be `/home/mforce/.cluckwork-slices/732/worktree`
— confirm with `pwd` and report it.

===================================================================================
# INCREMENT 1 — `Account.Rename` (domain): validate, no-op, bump Version
===================================================================================

## 1a. RED — add the failing domain tests

Append to `tests/Cluckwork.Domain.Tests/Accounts/AccountSlugTests.cs` (existing file, 116 lines, ends with
`Reactivate_ReactivatesAndBumpsVersion` then `}`) — **append inside the class, do not create a new file**:

```csharp
    // #732 — Rename is the one path that can change an existing farm's code. It returns
    // Result (an invalid or reserved code is an expected failure, not an invariant
    // violation), unlike Create, which keeps the throwing ValidateSlug backstop.
    [Fact]
    public void Rename_ChangesTheSlugAndBumpsVersion()
    {
        var account = Farm("lifecycle-farm");

        var result = account.Rename("renamed-farm");

        Assert.True(result.IsSuccess);
        Assert.Equal("renamed-farm", account.Slug);
        Assert.Equal(1, account.Version);
    }

    [Fact]
    public void Rename_TrimsButDoesNotFoldCase()
    {
        var account = Farm("lifecycle-farm");

        Assert.Equal("renamed-farm", account.Rename("  renamed-farm  ").IsSuccess ? account.Slug : null);
        Assert.True(account.Rename("Renamed-Two").IsFailure);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("-farm")]
    [InlineData("farm-")]
    [InlineData("bad_slug")]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Rename_RejectsAnInvalidSlug_AndChangesNothing(string candidate)
    {
        var account = Farm("lifecycle-farm");

        var result = account.Rename(candidate);

        Assert.True(result.IsFailure);
        Assert.Equal("Account.SlugInvalid", result.Error.Code);
        Assert.Equal("lifecycle-farm", account.Slug);
        Assert.Equal(0, account.Version);
    }

    [Fact]
    public void Rename_RejectsEveryReservedSlug_AndChangesNothing()
    {
        // Reserved codes are valid in SHAPE, so they must be refused by the reserved
        // branch specifically — the same distinction Create_RejectsEveryReservedSlug pins.
        Assert.NotEmpty(Account.ReservedSlugs);
        foreach (var reserved in Account.ReservedSlugs)
        {
            var account = Farm("lifecycle-farm");

            var result = account.Rename(reserved);

            Assert.True(result.IsFailure);
            Assert.Equal("Account.SlugInvalid", result.Error.Code);
            Assert.Equal("lifecycle-farm", account.Slug);
            Assert.Equal(0, account.Version);
        }
    }

    // Renaming to the code the farm already has is a NO-OP, and the no-op must not
    // advance Version: Version is the token a Farm Settings form holds open against, so a
    // command that changed nothing must not make that form fail. Same reasoning as the
    // stateChanged gate in AccountSuspensionService.
    [Fact]
    public void Rename_ToTheSameCode_ChangesNothing_AndDoesNotBumpVersion()
    {
        var account = Farm("lifecycle-farm");

        var result = account.Rename("lifecycle-farm");

        Assert.True(result.IsSuccess);
        Assert.Equal("lifecycle-farm", account.Slug);
        Assert.Equal(0, account.Version);
    }

    [Fact]
    public void Rename_ToTheSameCode_AcceptsTheTrimmedForm()
    {
        var account = Farm("lifecycle-farm");

        var result = account.Rename("  lifecycle-farm  ");

        Assert.True(result.IsSuccess);
        Assert.Equal(0, account.Version);
    }
```

Run it and RECORD THE FAILURE:

| Gate row + narrowing | Command as run | Named test | Assertion | Stable discriminator | Generated fragments | Path driven | What the fixture already seeds | Which other guard returns the same failure | Negative-test proof |
|---|---|---|---|---|---|---|---|---|---|
| G2 narrowed to this class | `dotnet test tests/Cluckwork.Domain.Tests --filter "FullyQualifiedName~AccountSlugTests" -v q --nologo` | `AccountSlugTests.Rename_ChangesTheSlugAndBumpsVersion` (and the other five) | compile of the test file | **`error CS1061` naming `'Account' does not contain a definition for 'Rename'`** | none | direct call to the aggregate method — acceptable here because the *aggregate* is the contract under test and increments 2–3 add the forgetting paths (service, verb) with their own tests | `Farm(slug)` = `Account.Create(Guid.NewGuid(), "Test Farm", slug, "UTC", "USD")` → `Slug == slug`, `Version == 0` | none — no other member is named `Rename` on `Account` (`ExpenseCategory.Rename` is a different type) | n/a — positive test |

**This RED is a compile error, and that is the expected shape** — the method does not exist yet, so the
test cannot compile. It is NOT the "compile error = STOP" case because the STOP rule is about a *gate*
failing unexpectedly; here the missing symbol **is** the named discriminator. If instead it fails with a
runner error, zero tests collected, or a `Rename` that already exists, STOP and report.

## 1b. GREEN — the domain method

**PROTECTED — read-at: src/Cluckwork.Domain/Accounts/Account.cs:102-116 (TryValidateSlug), Account.cs:129-138 (Suspend/Reactivate Version++ convention), src/Cluckwork.Domain/Common/Result.cs:15-18 (Result.Failure)**

Find this exact block in `src/Cluckwork.Domain/Accounts/Account.cs` (occurs exactly once — the driver
counted):
```csharp
    public void Reactivate()
    {
        IsActive = true;
        Version++;
    }
```
Replace with:
```csharp
    public void Reactivate()
    {
        IsActive = true;
        Version++;
    }

    // #732 — the farm code is no longer immutable. Returns Result rather than throwing
    // because an invalid or reserved code is an EXPECTED failure on a path an operator
    // drives by hand; Create keeps the throwing ValidateSlug backstop for every other
    // factory caller. Validation is TryValidateSlug, the same single rule provisioning
    // uses: one regex and one reserved set own both paths.
    //
    // The same-code case returns success WITHOUT touching Version, and that is not
    // cosmetic. Version is the token UpdateFarmSettingsHandler compares a Farm Settings
    // save against, so a command that changed nothing must not advance it — the same
    // reasoning as the stateChanged gate in AccountSuspensionService. "Every aggregate
    // mutation bumps Version" stays true because on a no-op there is no mutation.
    public Result Rename(string? newSlug)
    {
        var validated = TryValidateSlug(newSlug);
        if (validated.IsFailure)
            return Result.Failure(validated.Error);

        if (string.Equals(validated.Value, Slug, StringComparison.Ordinal))
            return Result.Success();

        Slug = validated.Value;
        Version++;
        return Result.Success();
    }
```

Also in the same file, the comment block above `ReservedSlugs` currently asserts the opposite of what is
about to be true. Find this exact block (occurs exactly once):
```csharp
    // Farm code (#531). Lowercase, URL-safe, stored ALREADY-NORMALIZED so a
    // plain unique index suffices — deliberately NOT a lower("Slug") expression
    // index (the four in InitialCreate are un-regenerable #407 fixtures; no
    // reason to mint a fifth). Immutable this epic (decision 10): there is no
    // ChangeSlug, on purpose — a provisioning typo has no in-epic fix, which is
    // why #533's provision-account echoes the slug before it commits.
```
Replace with:
```csharp
    // Farm code (#531). Lowercase, URL-safe, stored ALREADY-NORMALIZED so a
    // plain unique index suffices — deliberately NOT a lower("Slug") expression
    // index (the four in InitialCreate are un-regenerable #407 fixtures; no
    // reason to mint a fifth). Renameable since #732 by the `rename-account`
    // verb and nothing else — there is deliberately no endpoint or Settings
    // field, and no retired-code list: a code a farm has moved off is
    // immediately reusable, which docs/decisions/732-farm-code-rename.md
    // records as the accepted cost.
```

## 1c. RED/GREEN — pin the Version token with two tracked snapshots

Before adding `Rename` the existing race class compiles; append this test inside
`tests/Cluckwork.Api.IntegrationTests/AccountSlugRaceTests.cs` after the reactivate test. Run it **before**
adding `Version++` in 1b if practical: the stable RED is **no exception** (`Assert.IsType` gets null),
because both updates match Version 0. If 1b is already transcribed, temporarily remove only `Version++`,
run this test to record that same RED, restore it, rebuild, and re-run GREEN.

```csharp
    [Fact]
    public async Task TwoConcurrentRenames_TheLoserGetsAConcurrencyConflict()
    {
        var accountId = await factory.SeedAccountWithUserAsync(Unique("rename"));

        var conflict = await Record.ExceptionAsync(() =>
            factory.WithTenantScopeAsync(accountId, dbA =>
                factory.WithTenantScopeAsync(accountId, async dbB =>
                {
                    var a = await dbA.Accounts.FirstAsync();
                    var b = await dbB.Accounts.FirstAsync();

                    Assert.True(a.Rename("first-" + accountId.ToString("N")[..10]).IsSuccess);
                    await dbA.SaveChangesAsync();

                    Assert.True(b.Rename("second-" + accountId.ToString("N")[..10]).IsSuccess);
                    await dbB.SaveChangesAsync();
                })));

        Assert.IsType<DbUpdateConcurrencyException>(conflict);
    }
```

Run:
`dotnet test tests/Cluckwork.Api.IntegrationTests --filter "FullyQualifiedName~AccountSlugRaceTests.TwoConcurrentRenames" -v q --nologo`
It must report **1 passed** after restore. This is the repo-required parallel-race guard for every new
aggregate mutation; the service's `FOR UPDATE` test does not substitute for the EF Version-token proof.

## 1d. Build and re-run
Run **G1**, then **G2 narrowed** to `AccountSlugTests` as in 1a. Both must be green.

**Do not expect a count you derived by counting attributes.** The base class reports **19 discovered
cases** (9 `[Fact]` + 1 `[Theory]` whose `InlineData` rows expand to 10); this increment adds 6 `[Fact]`
and 1 `[Theory]` with 6 rows = **11 new cases**, so the narrowed run must report **30 passed**. Derive it
yourself instead of trusting that arithmetic: run the narrowed filter BEFORE appending, write the number
down, then after appending run it again and report both numbers and the delta. **A delta other than +11 is
a STOP** (a theory row that never ran is exactly how a guard ships vacuous).

## 1e. Commit Increment 1
```bash
git add src/Cluckwork.Domain/Accounts/Account.cs tests/Cluckwork.Domain.Tests/Accounts/AccountSlugTests.cs \
        tests/Cluckwork.Api.IntegrationTests/AccountSlugRaceTests.cs
git commit -m "feat(domain): Account.Rename validates a farm code and bumps Version (#732)"
```

===================================================================================
# INCREMENT 2 — the service, its audit row, and the two inert constants
===================================================================================

## 2a. RED — the service's behaviour, through the service

Create `tests/Cluckwork.Api.IntegrationTests/AccountRenameServiceTests.cs`:

```csharp
namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// #732 — service contract below the CLI. The stale-source test is a REAL two-writer
// schedule: a transaction holds the source row, the service resolves the old slug and
// queues on FOR UPDATE, then the holder renames and commits. The service must inspect the
// freshly locked row and refuse rather than overwrite that committed rename.
[Collection(IntegrationCollection.Name)]
public sealed class AccountRenameServiceTests(CluckworkWebApplicationFactory factory)
{
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}@test.local";
    private static string Slug(Guid accountId) => "farm-" + accountId.ToString("N")[..12];
    private static string Target(string prefix, Guid accountId) => prefix + accountId.ToString("N")[..11];

    private async Task<RenameOutcome> RenameAsync(string current, string next)
    {
        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<AccountRenameService>()
            .RenameAsync(current, next, "drill", CancellationToken.None);
        return new RenameOutcome(result.IsSuccess, result.IsFailure ? result.Error.Code : null,
            result.IsSuccess ? result.Value.Changed : null);
    }

    private sealed record RenameOutcome(bool Success, string? ErrorCode, bool? Changed);

    private Task<string> SlugAsync(Guid accountId) =>
        factory.WithTenantScopeAsync(accountId, db => db.Accounts
            .Where(a => a.Id == accountId).Select(a => a.Slug).SingleAsync());

    private Task<int> VersionAsync(Guid accountId) =>
        factory.WithTenantScopeAsync(accountId, db => db.Accounts
            .Where(a => a.Id == accountId).Select(a => a.Version).SingleAsync());

    private async Task<(AppDbContext Db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Tx, int Pid)>
        FenceAccountAsync(Guid accountId)
    {
        var tenant = new TenantContext();
        tenant.Resolve(accountId);
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(factory.ConnectionString).Options,
            tenant, new FlockScope());
        var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "Accounts" WHERE "Id" = {accountId} FOR UPDATE""");
        return (db, tx, await db.BackendPidAsync());
    }

    [Fact]
    public async Task Rename_ChangesTheCode_BumpsVersion_AndWritesOneAuditRow()
    {
        var accountId = await factory.SeedAccountWithUserAsync(Unique("rename-ok"));
        var current = Slug(accountId);
        var target = Target("ok", accountId);
        var versionBefore = await VersionAsync(accountId);

        var outcome = await RenameAsync(current, target);

        Assert.True(outcome.Success, $"expected success, got {outcome.ErrorCode}");
        Assert.True(outcome.Changed);
        Assert.Equal(target, await SlugAsync(accountId));
        Assert.Equal(versionBefore + 1, await VersionAsync(accountId));
        var audit = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .Where(a => a.AccountId == accountId && a.Action == "Account.Rename").SingleAsync());
        Assert.Equal("Account", audit.EntityType);
        Assert.Equal(accountId, audit.EntityId);
        Assert.Equal("drill", audit.Reason);
        Assert.Equal(SystemActors.RenameAccount, audit.ActorEmail);
        Assert.Equal(Guid.Empty, audit.ActorUserId);
        Assert.Contains("\"from\":\"" + current + "\"", audit.DetailsJson);
        Assert.Contains("\"to\":\"" + target + "\"", audit.DetailsJson);
    }

    [Fact]
    public async Task Rename_WhenSourceChangesAfterLookup_RefusesAndDoesNotOverwrite()
    {
        var accountId = await factory.SeedAccountWithUserAsync(Unique("rename-stale"));
        var stale = Slug(accountId);
        var committed = Target("won", accountId);
        var attempted = Target("old", accountId);
        var versionBefore = await VersionAsync(accountId);
        var (db, tx, pid) = await FenceAccountAsync(accountId);
        await using var _ = db;
        await using var __ = tx;

        var rename = Task.Run(() => RenameAsync(stale, attempted));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(rename, pid),
            "rename must reach and block on the source-row FOR UPDATE before the competing commit");
        Assert.False(rename.IsCompleted, "the source-row lock must actually hold the rename");

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "Accounts" SET "Slug" = {committed}, "Version" = "Version" + 1 WHERE "Id" = {accountId}""");
        await tx.CommitAsync();
        var outcome = await rename;

        Assert.False(outcome.Success);
        Assert.Equal("Account.SlugStale", outcome.ErrorCode);
        Assert.Equal(committed, await SlugAsync(accountId));
        Assert.Equal(versionBefore + 1, await VersionAsync(accountId));
        Assert.Equal(0, await factory.WithTenantScopeAsync(accountId, context => context.AuditEvents
            .CountAsync(a => a.AccountId == accountId && a.Action == "Account.Rename")));
    }

    [Fact]
    public async Task Rename_ToTheSameCode_ChangesNothing()
    {
        var accountId = await factory.SeedAccountWithUserAsync(Unique("rename-noop"));
        var slug = Slug(accountId);
        var versionBefore = await VersionAsync(accountId);

        var outcome = await RenameAsync(slug, slug);

        Assert.True(outcome.Success);
        Assert.False(outcome.Changed);
        Assert.Equal(versionBefore, await VersionAsync(accountId));
        Assert.Equal(0, await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .CountAsync(a => a.AccountId == accountId && a.Action == "Account.Rename")));
    }

    [Fact]
    public async Task Rename_WithMissingSource_ReturnsNotFound()
    {
        var outcome = await RenameAsync("missing-" + Guid.NewGuid().ToString("N")[..11], "unused-target");
        Assert.False(outcome.Success);
        Assert.Equal("Accounts.NotFound", outcome.ErrorCode);
    }

    [Fact]
    public async Task Rename_ToATakenCode_ReturnsSlugTaken_AndLeavesBothFarms()
    {
        var first = await factory.SeedAccountWithUserAsync(Unique("rename-taken-a"));
        var second = await factory.SeedAccountWithUserAsync(Unique("rename-taken-b"));

        var outcome = await RenameAsync(Slug(second), Slug(first));

        Assert.False(outcome.Success);
        Assert.Equal("Account.SlugTaken", outcome.ErrorCode);
        Assert.Equal(Slug(first), await SlugAsync(first));
        Assert.Equal(Slug(second), await SlugAsync(second));
    }

    [Fact]
    public async Task Rename_TwoFarmsRaceForOneCode_ExactlyOneWinsThroughTheIndexCatch()
    {
        var first = await factory.SeedAccountWithUserAsync(Unique("rename-race-a"));
        var second = await factory.SeedAccountWithUserAsync(Unique("rename-race-b"));
        var target = Target("race", first);
        var firstSlug = Slug(first);
        var secondSlug = Slug(second);
        var (firstDb, firstTx, firstPid) = await FenceAccountAsync(first);
        var (secondDb, secondTx, secondPid) = await FenceAccountAsync(second);
        await using var _1 = firstDb;
        await using var _2 = firstTx;
        await using var _3 = secondDb;
        await using var _4 = secondTx;

        var firstRename = Task.Run(() => RenameAsync(firstSlug, target));
        var secondRename = Task.Run(() => RenameAsync(secondSlug, target));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(firstRename, firstPid));
        Assert.True(await factory.WaitUntilDoneOrBlockedAsync(secondRename, secondPid));
        Assert.False(firstRename.IsCompleted);
        Assert.False(secondRename.IsCompleted);
        // Each source row has one contender and each service's only blocking statement is
        // its source FOR UPDATE. These holder-specific waits prove both destination
        // pre-reads completed while target was still unclaimed.

        await firstTx.CommitAsync();
        await secondTx.CommitAsync();
        var outcomes = await Task.WhenAll(firstRename, secondRename);

        Assert.Single(outcomes, outcome => outcome.Success);
        Assert.Single(outcomes, outcome => outcome.ErrorCode == "Account.SlugTaken");
        Assert.Equal(1, await factory.WithTenantScopeAsync(first, db => db.Accounts
            .IgnoreQueryFilters().CountAsync(a => a.Slug == target)));
        Assert.Equal(1, await factory.WithTenantScopeAsync(first, db => db.AuditEvents
            .IgnoreQueryFilters().CountAsync(a =>
                (a.AccountId == first || a.AccountId == second) && a.Action == "Account.Rename")));
        var loser = outcomes[0].Success ? second : first;
        Assert.Equal(loser == first ? firstSlug : secondSlug, await SlugAsync(loser));
    }
}
```

| Gate row + narrowing | Command as run | Named test | Assertion | Stable discriminator | Generated fragments | Path driven | What the fixture seeds | Which other guard returns the same failure | Negative-test proof |
|---|---|---|---|---|---|---|---|---|---|
| G2 narrowed | `dotnet test tests/Cluckwork.Api.IntegrationTests --filter "FullyQualifiedName~AccountRenameServiceTests" -v q --nologo` | all of them | compile of the test file | **`error CS0246` for `AccountRenameService`** (type not found) — **and, expected, also `CS0117` for `SystemActors.RenameAccount`**, because the test references the constant step 2b adds | none | the service method, which is what the verb will call; increment 3's verb tests cover the CLI path | `SeedAccountWithUserAsync` → `Account.Create(…, "farm-"+id.N[0..12], "UTC", "USD")`, `Version == 0`, one Owner | n/a | n/a |

Expected RED: compile errors naming **`AccountRenameService`** (CS0246) and **`SystemActors.RenameAccount`**
(CS0117). Both are the missing-symbol shape this increment exists to close. Anything else — a runner
failure, a *different* test red, a container/Docker error, or a test that ran and failed an assertion — is
a STOP.

## 2b. GREEN — the two inert constants first (so the service compiles against them)

In `src/Cluckwork.Application/Common/AuditActions.cs`, find (occurs exactly once):
```csharp
    public const string AccountSuspend = "Account.Suspend";
    public const string AccountReactivate = "Account.Reactivate";
```
Replace with:
```csharp
    public const string AccountSuspend = "Account.Suspend";
    public const string AccountReactivate = "Account.Reactivate";
    // #732 — the farm code changed. Written ONLY on a real change: renaming a farm to the
    // code it already has is a no-op and appends nothing, so the trail stays one row per
    // actual change. The row carries from/to, because the code it names is gone.
    public const string AccountRename = "Account.Rename";
```

In `src/Cluckwork.Application/Common/SystemActors.cs`, find (occurs exactly once):
```csharp
    /// <summary>Operator bringing a farm back (#534). Author of the Account.Reactivate row.</summary>
    public const string ReactivateAccount = "(reactivate-account)";
```
Replace with:
```csharp
    /// <summary>Operator bringing a farm back (#534). Author of the Account.Reactivate row.</summary>
    public const string ReactivateAccount = "(reactivate-account)";

    /// <summary>Operator changing a farm's code (#732). Author of the Account.Rename row.</summary>
    public const string RenameAccount = "(rename-account)";
```

## 2c. GREEN — the service

Create `src/Cluckwork.Infrastructure/Identity/AccountRenameService.cs`:

**PROTECTED — read-at: AccountSuspensionService.cs:88 (resolve before locked read), :110-118 (locked read + not-found shape), :130 (pre-mutation state read), :193-215 (audit before save, AuditActions.X argument); AccountProvisioner.cs:94-137 (try outside AmbientTransaction, IsSlugConflict); AccountRepository.cs:33-38 (tenant-keyed FOR UPDATE); AuditWriter.cs:29-48 (both fail-closed guards); AuditVocabularyCoverageTests.cs:459-467 (accepted action-argument shapes)**

```csharp
namespace Cluckwork.Infrastructure.Identity;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Cluckwork.Domain.Accounts;
using Cluckwork.Domain.Common;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// #732 — changes a farm's code. The operator surface is the `rename-account` verb; this is
// the domain path that replaces the guarded raw UPDATE #731 documented, which bumped
// Version by hand, wrote no audit row and checked neither the pattern nor the reserved set.
//
// Three things have to be true at once, which is why this is a service and not a handler:
//
//   1. The row mutated is still the farm the operator named when lookup ran. Resolving
//      slug -> id and taking FOR UPDATE are TWO awaited database statements, not one atomic
//      operation: another transaction can rename that row between them. The locked row's
//      slug is therefore compared with currentSlug before mutation. Without that fence a
//      stale command overwrites the rename that won the race.
//   2. The destination code stays unique. IX_Accounts_Slug is UNIQUE ("Slug") with no
//      account component, so the source row's lock reserves nothing: two farms renamed to
//      one code both pass any pre-read and the INDEX decides. The friendly pre-read below
//      is convenience; the catch is the guarantee (same split as AccountProvisioner).
//   3. The rename and its audit row commit together or not at all. IAuditWriter appends to
//      this unit of work and never saves, so the row lands with the rename or not at all.
//
// Deliberately NOT here: a retired-code list. A code a farm has moved off is immediately
// reusable, and `rename-account --slug <retired>` therefore targets whoever holds it now.
// That is the accepted cost of #732 and docs/decisions/732-farm-code-rename.md says so;
// the verb's help text and the runbook both tell the operator to run list-accounts first.
public sealed class AccountRenameService(
    AppDbContext db,
    TenantContext tenant,
    IAccountRepository accounts,
    IAuditWriter audit,
    CurrentUserContext currentUser)
{
    public async Task<Result<AccountRenameOutcome>> RenameAsync(
        string currentSlug, string? newSlug, string? reason, CancellationToken ct = default)
    {
        var validated = Account.TryValidateSlug(newSlug);
        if (validated.IsFailure)
            return Result.Failure<AccountRenameOutcome>(validated.Error);
        var target = validated.Value;

        // This lookup and the locked read below are separate statements. The id is stable,
        // but the slug on that row is not; the post-lock equality fence below closes that
        // race. Resolving the tenant remains a precondition for the locked read.
        var accountId = await ResolveCurrentAsync(currentSlug, ct);
        if (accountId is null)
            return Result.Failure<AccountRenameOutcome>(Error.NotFound("Accounts", currentSlug));

        // Friendly UX only. This can race with another farm targeting the same code,
        // so IX_Accounts_Slug and the catch below remain the correctness guarantee.
        if (!string.Equals(currentSlug, target, StringComparison.Ordinal)
            && await IsTargetTakenAsync(target, accountId.Value, ct))
            return SlugTaken(target);

        tenant.Resolve(accountId.Value);

        // #500 — no signed-in human by design (an operator at a shell), so this declares
        // WHICH non-person it is, exactly as the suspend/reactivate/provision verbs do.
        currentUser.ResolveSystemActor(SystemActors.RenameAccount);

        try
        {
            return await AmbientTransaction.RunAsync(db.Database, async (transaction, token) =>
            {
                var account = await accounts.GetCurrentLockedAsync(token);
                if (account is null)
                    return Result.Failure<AccountRenameOutcome>(Error.NotFound("Accounts", accountId.Value));

                // The slug lookup happened before this lock. A concurrent committed rename
                // leaves the id valid but the operator's source code stale; never overwrite it.
                if (!string.Equals(account.Slug, currentSlug, StringComparison.Ordinal))
                {
                    await transaction.RollbackAsync(token);
                    return Result.Failure<AccountRenameOutcome>(Error.Conflict(
                        "Account.SlugStale",
                        $"'{currentSlug}' is no longer this farm's code. Run list-accounts and "
                        + "re-run with the code it has now."));
                }

                // Read BEFORE mutating: Rename sets Slug, so asking the aggregate
                // afterwards cannot answer "did this command change anything?" — the same
                // pre-mutation read AccountSuspensionService makes of IsActive.
                var changed = !string.Equals(account.Slug, target, StringComparison.Ordinal);

                var rename = account.Rename(target);
                if (rename.IsFailure)
                {
                    await transaction.RollbackAsync(token);
                    return Result.Failure<AccountRenameOutcome>(rename.Error);
                }

                // Written only on a real change, so the trail is one row per change rather
                // than one per keystroke. The action is a direct AuditActions reference
                // because AuditVocabularyCoverageTests fails closed on any other shape.
                if (changed)
                    await audit.WriteAsync(
                        AuditActions.AccountRename, nameof(Account), account.Id,
                        reason: reason,
                        // from/to because the code the row names no longer exists, and the
                        // same accountability payload break-glass and suspension carry: the
                        // actor names the COMMAND, so the shell it ran on is the only trace.
                        details: new
                        {
                            @from = currentSlug,
                            to = target,
                            host = Environment.MachineName,
                            osUser = Environment.UserName,
                        },
                        ct: token);

                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return Result.Success(new AccountRenameOutcome(changed));
            }, ct);
        }
        catch (DbUpdateException ex) when (AccountProvisioner.IsSlugConflict(ex))
        {
            return SlugTaken(target);
        }
    }

    private static Result<AccountRenameOutcome> SlugTaken(string target) =>
        Result.Failure<AccountRenameOutcome>(Error.Conflict(
            "Account.SlugTaken",
            $"'{target}' is already another farm's code. Choose another; codes are unique "
            + "across every farm on this deployment."));

    // A friendly early answer, never the authority: another transaction can still claim
    // target after this read. The unique index + catch above closes that race.
    private Task<bool> IsTargetTakenAsync(string target, Guid sourceId, CancellationToken ct) =>
        db.Accounts.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(account => account.Id != sourceId && account.Slug == target, ct);

    // Reads ACROSS accounts with no tenant resolved, so IgnoreQueryFilters is required
    // rather than defensive — without it the account filter matches Guid.Empty and every
    // real farm reads as absent. This is the same justified call site as
    // AccountSlugLookup.ResolveAsync, and #536's registry needs its own row.
    private async Task<Guid?> ResolveCurrentAsync(string slug, CancellationToken ct)
    {
        var matches = await db.Accounts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(account => account.Slug == slug)
            .Select(account => account.Id)
            .ToListAsync(ct);
        // Slug carries a global unique index, so 0 or 1. Count==1 rather than
        // SingleOrDefault so a hand-corrupted database reads as "no such farm"
        // (the quieter failure for an operator tool) instead of throwing.
        return matches.Count == 1 ? matches[0] : null;
    }
}

// Changed = "this command changed the code", so the verb can tell an operator their
// re-run was a no-op without re-reading the database. Deliberately NOT "the farm is fine".
public sealed record AccountRenameOutcome(bool Changed);
```

> **Two things the implementer must check and report rather than assume.** (1) `@from` — `from` is a
> contextual keyword in C# anonymous-object initializers; if `new { from = … }` compiles, use `from` and
> report that it did, because the test asserts `"from":"…"`. If it does not compile, keep `@from` and
> report that too — **the JSON property name must end up `from` either way**; verify by reading the
> assertion in 2a after the run. (2) `nameof(Account)` must render the string `Account` (the file has
> `using Cluckwork.Domain.Accounts;`); the test asserts `EntityType == "Account"`. If it renders anything
> else, STOP and report — do not switch to a string literal, which `AuditVocabularyCoverageTests` does not
> police but the repo's own #258 rule does for actions and the entity-type walk reads literally.

## 2d. Register it

In `src/Cluckwork.Api/Hosting/CluckworkIdentityServiceCollectionExtensions.cs`, find (occurs exactly once):
```csharp
        // #532 — no CLI or HTTP surface yet; #534's operator verbs resolve it.
        services.AddScoped<AccountSuspensionService>();
```
Replace with:
```csharp
        // #532 — no CLI or HTTP surface yet; #534's operator verbs resolve it.
        services.AddScoped<AccountSuspensionService>();
        // #732 — the rename verb. Same always-available-in-Production posture: it has to
        // work against a real database, and it is not environment-gated.
        services.AddScoped<AccountRenameService>();
```

## 2e. Build and run — DO NOT COMMIT until increment 5
Run **G1**, then **G2 narrowed** to `AccountRenameServiceTests` (all green), then **G2 narrowed** to
`AuditVocabularyCoverageTests` — **expect this one RED** on
`AuditActions_registry_matches_the_SPA_AUDIT_ACTION_VALUES_list` naming `Account.Rename` missing from the
client. **That red is expected and is fixed by increment 4**; report it, do not "fix" it by editing the
test, and do not proceed to commit until you have also run **G2 narrowed** to
`TenantBypassRealTreeTests` and reported its result (it may already be red on the new
`ResolveCurrentAsync` site — increment 5 fixes that; report, don't fix).

```bash
git add src/Cluckwork.Application/Common/AuditActions.cs \
        src/Cluckwork.Application/Common/SystemActors.cs \
        src/Cluckwork.Infrastructure/Identity/AccountRenameService.cs \
        src/Cluckwork.Api/Hosting/CluckworkIdentityServiceCollectionExtensions.cs \
        tests/Cluckwork.Api.IntegrationTests/AccountRenameServiceTests.cs
# DO NOT COMMIT HERE — continue to increment 5
```
These staged-file examples are bookkeeping only; **do not commit here**. Continue through increment 5.

===================================================================================
# INCREMENT 3 — the `rename-account` verb
===================================================================================

## 3a. RED — the CLI contract, through the real binary

Append to `tests/Cluckwork.Api.IntegrationTests/AccountLifecycleCommandTests.cs` **inside the class**
(existing file; it already has `StartCommand`, `RunAsync`, `Slug(accountId)`, `VersionAsync`,
`IsActiveAsync`, `LiveRefreshTokenCountAsync` — reuse them, do not redefine them):

```csharp
    // #732 — the rename verb. Driven as a subprocess like every other verb, because the
    // contract under test is the exit code and the printed line, and only the process
    // that exits produces those.
    private Task<(int ExitCode, string Stdout, string Stderr)> RunRename(string current, string next,
        string extra = "") =>
        RunAsync($"rename-account --slug {current} --new-slug {next}{extra}");

    [Fact]
    public async Task RenameVerb_ChangesTheCode_PrintsOldAndNew_AndWritesOneAuditRow()
    {
        var email = $"rename-command-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var slug = Slug(accountId);

        var (exitCode, stdout, stderr) = await RunRename(slug, "renamed-by-verb",
            " --reason \"rebrand drill\"");

        Assert.True(exitCode == 0, $"expected exit 0, got {exitCode}. stdout={stdout} stderr={stderr}");
        Assert.Contains(slug, stdout);
        Assert.Contains("renamed-by-verb", stdout);
        Assert.Equal("renamed-by-verb", await factory.WithTenantScopeAsync(accountId, db => db.Accounts
            .Where(a => a.Id == accountId).Select(a => a.Slug).SingleAsync()));

        var audit = await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .Where(a => a.AccountId == accountId && a.Action == "Account.Rename")
            .SingleAsync());
        Assert.Equal("rebrand drill", audit.Reason);
        Assert.Equal("(rename-account)", audit.ActorEmail);
        Assert.Equal(Guid.Empty, audit.ActorUserId);
    }

    // The current code is matched case-insensitively (an operator typing SECOND-FARM at a
    // shell means second-farm) while the NEW code is not folded — TryValidateSlug rejects
    // uppercase. Both directions in one test so the asymmetry cannot be "fixed" by
    // normalizing both.
    [Fact]
    public async Task RenameVerb_FoldsCaseForTheCurrentCode_ButRejectsAnUppercaseNewCode()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"rename-case-{Guid.NewGuid():N}@test.local");
        var slug = Slug(accountId);

        var foldedTarget = "fold" + slug[^11..];
        var folded = await RunRename(slug.ToUpperInvariant(), foldedTarget);
        Assert.True(folded.ExitCode == 0, $"expected exit 0, got {folded.ExitCode}. stderr={folded.Stderr}");

        var uppercaseTarget = await RunRename(foldedTarget, "RENAMED-TWO");
        Assert.Equal(1, uppercaseTarget.ExitCode);
        Assert.Contains("Account.SlugInvalid", uppercaseTarget.Stderr);
    }

    [Fact]
    public async Task RenameVerb_RunTwice_ExitsZero_AndWritesNoSecondAuditRow()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"rename-repeat-{Guid.NewGuid():N}@test.local");
        var slug = Slug(accountId);

        var target = "rep" + slug[^11..];
        var first = await RunRename(slug, target);
        var versionAfterFirst = await VersionAsync(accountId);
        var second = await RunRename(target, target);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("already", second.Stdout);
        Assert.Equal(versionAfterFirst, await VersionAsync(accountId));
        Assert.Equal(1, await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
            .CountAsync(a => a.AccountId == accountId && a.Action == "Account.Rename")));
    }

    [Fact]
    public async Task RenameVerb_ToAnotherFarmCode_ExitsOne_NamingSlugTaken_AndChangesNothing()
    {
        var first = await factory.SeedAccountWithUserAsync($"rename-taken-a-{Guid.NewGuid():N}@test.local");
        var second = await factory.SeedAccountWithUserAsync($"rename-taken-b-{Guid.NewGuid():N}@test.local");

        var result = await RunRename(Slug(second), Slug(first));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Account.SlugTaken", result.Stderr);
        Assert.Equal(Slug(second), await factory.WithTenantScopeAsync(second, db => db.Accounts
            .Where(a => a.Id == second).Select(a => a.Slug).SingleAsync()));
    }

    [Theory]
    [InlineData("api")]     // reserved: valid in shape, refused by the reserved branch
    [InlineData("ab")]      // too short
    [InlineData("SELF")]    // sentinel: swapped below for THIS farm's own code -> no-op
    public async Task RenameVerb_WithAnUnusableNewCode_BehavesPerContract(string candidate)
    {
        var accountId = await factory.SeedAccountWithUserAsync($"rename-bad-{Guid.NewGuid():N}@test.local");
        var slug = Slug(accountId);
        if (candidate == "SELF") candidate = slug;

        var result = await RunRename(slug, candidate);

        if (candidate == slug)
        {
            // Renaming to the code it already has is a NO-OP, not a failure: an operator
            // retrying a half-remembered command must not see a red exit. No audit row,
            // no Version bump — pinned above and in the service tests.
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("already", result.Stdout);
            Assert.Equal(0, await factory.WithTenantScopeAsync(accountId, db => db.AuditEvents
                .CountAsync(a => a.AccountId == accountId && a.Action == "Account.Rename")));
            return;
        }

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Account.SlugInvalid", result.Stderr);
        Assert.Equal(slug, await factory.WithTenantScopeAsync(accountId, db => db.Accounts
            .Where(a => a.Id == accountId).Select(a => a.Slug).SingleAsync()));
    }

    // The sessions-survive acceptance criterion, asserted rather than reasoned about:
    // the refresh cookie and access token bind to the account id, so a rename must not
    // end anybody's session.
    [Fact]
    public async Task RenameVerb_LeavesAnExistingSessionWorking()
    {
        var email = $"rename-session-{Guid.NewGuid():N}@test.local";
        var accountId = await factory.SeedAccountWithUserAsync(email);
        var slug = Slug(accountId);
        var tokens = await factory.LoginAsync(email);

        Assert.Equal(0, (await RunRename(slug, "live" + slug[^11..])).ExitCode);

        var response = await factory.CreateClient()
            .PostRefreshAsync(tokens.RefreshToken, expectedAccount: accountId.ToString());
        Assert.True(response.IsSuccessStatusCode,
            $"refresh after a rename must still work, got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task RenameVerb_WithoutTheNewCode_ExitsOne_NamingTheFlag()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"rename-flag-{Guid.NewGuid():N}@test.local");

        var result = await RunAsync($"rename-account --slug {Slug(accountId)}");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--new-slug", result.Stderr);
    }

    [Fact]
    public async Task RenameVerb_WithAnUnknownCurrentCode_ExitsOne_NamingTheCode()
    {
        var accountId = await factory.SeedAccountWithUserAsync($"rename-unknown-{Guid.NewGuid():N}@test.local");
        var before = await VersionAsync(accountId);

        var result = await RunRename("missing-farm", "new" + Guid.NewGuid().ToString("N")[..11]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("missing-farm", result.Stderr);
        Assert.Equal(before, await VersionAsync(accountId));
    }
```

| Gate row + narrowing | Command as run | Named test | Assertion | Stable discriminator | Generated fragments | Path driven | What the fixture seeds | Which other guard returns the same failure | Negative-test proof |
|---|---|---|---|---|---|---|---|---|---|
| G2 narrowed | `dotnet test tests/Cluckwork.Api.IntegrationTests --filter "FullyQualifiedName~AccountLifecycleCommandTests" -v q --nologo` | `RenameVerb_ChangesTheCode_PrintsOldAndNew_AndWritesOneAuditRow` | `Assert.True(exitCode == 0, …)` or the subprocess timeout | **either** the harness's 60-second timeout **or** `expected exit 0, got <nonzero>` from a serving boot guard; `Unhandled exception` is allowed only in this pre-registration RED because the process took the serving path instead of the verb | `<generated slug>`, `<generated account id>` | the real `dotnet Cluckwork.Api.dll rename-account …` subprocess — the only path that produces an exit code | account + Owner via `SeedAccountWithUserAsync`, slug = `farm-<id12>` | `Account.SlugTaken` and `Account.SlugInvalid` also exit 1 — the discriminator is the **exit-0 expectation**, which only a working verb can produce | the same test asserts the audit row exists, so a discarded write cannot masquerade as success |

**Expected RED, and it is the slow shape — read this before you panic.** `CliDispatcher.TryRunAsync`
returns `null` for an unregistered verb, and `ProcessRoles.From` classifies an unknown first argument as
**Serving**, so `dotnet Cluckwork.Api.dll rename-account …` **starts the web host**. In these tests that
means one of two outcomes, both the expected red:
- the subprocess never exits and `SeedCommandRunner.RunToCompletionAsync` throws its timeout
  (`SubprocessTimeout` is 60 s in this class), or
- the serving boot throws first under `ASPNETCORE_ENVIRONMENT=Production` (the #260 proxy-trust / #319
  allowed-hosts guards) and the assertion fails on `expected exit 0, got <nonzero>`.

**So this RED is slow (up to a minute per new test) and may print `Unhandled exception` — that is NOT the
STOP condition here**, unlike the lifecycle verbs' own contract. The STOP conditions are: the new tests
**pass**, or a *pre-existing* suspend/reactivate test goes red. Report which of the two shapes you saw.

## 3b. GREEN — the verb

Create `src/Cluckwork.Api/Cli/RenameAccountCliCommand.cs`:

**PROTECTED — read-at: src/Cluckwork.Api/Cli/SuspendAccountCliCommand.cs:32-77 (flag handling, not-found line, exit-1 contract, slug-only echo), SuspendAccountCliCommand.cs:88-113 (AccountSlugLookup), src/Cluckwork.Domain/Accounts/Account.cs:102-116 (TryValidateSlug rejects uppercase), AccountRenameService.cs as authored in increment 2 of this runbook**

```csharp
namespace Cluckwork.Api.Cli;

using Cluckwork.Domain.Accounts;
using Cluckwork.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

// `rename-account --slug <current> --new-slug <new> [--reason <text>]` (#732) — changes a
// farm's code. The reason it exists: a database upgraded from before multi-farm tenancy
// gets `default-farm` from the AddAccountSlug migration, nothing asks at migration time,
// and #731's only path was a hand-guarded UPDATE that bumped Version by hand, wrote no
// audit row and checked neither the pattern nor the reserved set. This is that write with
// the domain in front of it.
//
// Same run-then-exit shape as the lifecycle verbs, classified OneShot automatically via
// CliDispatcher.Commands (#347), and deliberately NOT environment-gated — it has to work
// against a real Production database. Safety is shell access plus a conspicuous
// Account.Rename audit row carrying from/to and --reason.
//
// WHAT THE OPERATOR MUST KNOW, and the two things this prints rather than assumes:
//   * Run list-accounts first. A code a farm has moved off is immediately reusable, so
//     --slug names whoever holds that code NOW, not the farm you meant last week.
//   * Existing sessions keep working: cookies and tokens bind to the account id. What
//     goes stale is client-side and cosmetic — the remembered code on the sign-in form
//     and the per-farm palette cache — and both refresh on the next explicit sign-in.
public sealed class RenameAccountCliCommand : ICliCommand
{
    public string Name => "rename-account";

    public async Task<int> RunAsync(WebApplication app, string[] args)
    {
        try
        {
            using var scope = app.Services.CreateScope();

            // The CURRENT code is folded like every other verb's --slug: an operator
            // typing SECOND-FARM at a shell means second-farm. The NEW code is NOT
            // folded, and that asymmetry is the domain's rule, not a slip —
            // TryValidateSlug rejects uppercase so the stored value is guaranteed
            // lowercase, which is what lets IX_Accounts_Slug be a plain index.
            var current = AccountSlugLookup.Normalize(CliDispatcher.ArgValue(args, "--slug"));
            if (current is null)
            {
                await Console.Error.WriteLineAsync(
                    "rename-account requires --slug <current-farm-code>.");
                return 1;
            }

            // Checked for PRESENCE before the domain runs: TryValidateSlug's description
            // quotes the offending value, and for an absent flag that is an empty string —
            // an operator would get "'' is not a valid farm code" instead of the flag name.
            var requested = CliDispatcher.ArgValue(args, "--new-slug");
            if (requested is null)
            {
                await Console.Error.WriteLineAsync(
                    "rename-account requires --new-slug <new-farm-code>.");
                return 1;
            }

            var newSlug = Account.TryValidateSlug(requested);
            if (newSlug.IsFailure)
            {
                await Console.Error.WriteLineAsync(
                    $"rename-account failed: {newSlug.Error.Code} — {newSlug.Error.Description}");
                return 1;
            }

            var accountId = await AccountSlugLookup.ResolveAsync(scope.ServiceProvider, current);
            if (accountId is null)
            {
                await Console.Error.WriteLineAsync($"No farm with code '{current}'.");
                return 1;
            }

            var service = scope.ServiceProvider.GetRequiredService<AccountRenameService>();
            var result = await service.RenameAsync(
                current, newSlug.Value, CliDispatcher.ArgValue(args, "--reason"),
                CancellationToken.None);
            if (result.IsFailure)
            {
                await Console.Error.WriteLineAsync(
                    $"rename-account failed: {result.Error.Code} — {result.Error.Description}");
                return 1;
            }

            // Both codes are echoed and nothing else: they are slug-regex values, safe by
            // construction. The farm NAME is tenant-controlled free text whose validator
            // bounds only length, so printing it needs ListAccountsCliCommand's
            // control-character strip (#560) — which is why no verb in this family prints it.
            await Console.Out.WriteLineAsync(result.Value.Changed
                ? $"Farm renamed: {current} → {newSlug.Value}. Existing sessions keep working; "
                  + "users must use the new code at their next sign-in."
                : $"Farm '{current}' already has that code — nothing changed and no audit row "
                  + "was written.");
            return 0;
        }
        catch (Exception ex)
        {
            // Fail-loud per the family's contract: an unexpected error (DB unreachable, a
            // lost concurrency race) is exit 1 and one clean stderr line, never a stack
            // trace. The service's transaction rolls back, so nothing is half-changed.
            await Console.Error.WriteLineAsync($"rename-account failed: {ex.Message}");
            return 1;
        }
    }
}
```

Register it — find this exact block in `src/Cluckwork.Api/Cli/CliDispatcher.cs` (occurs exactly once):
```csharp
        new SuspendAccountCliCommand(),
        new ReactivateAccountCliCommand(),
    ];
```
Replace with:
```csharp
        new SuspendAccountCliCommand(),
        new ReactivateAccountCliCommand(),
        new RenameAccountCliCommand(),
    ];
```
**That is the only registration.** `ProcessRoles.OneShotVerbs` is derived from this array
(`ProcessRole.cs:44`); there is no second verb list, and adding one would be the mistake #347 exists to
prevent.

## 3c. GREEN — teach the #347 minimal-config suite the new verb (REQUIRED, not optional)

`OneShotVerbMinimalConfigTests.EveryDispatchedVerb_HasAMinimalConfigCase` walks
`ProcessRoles.OneShotVerbs` and fails any verb with no case, so registering the verb without adding a
case leaves **G2 red**. In `tests/Cluckwork.Api.IntegrationTests/OneShotVerbMinimalConfigTests.cs`,
find (occurs exactly once):
```csharp
        { "suspend-account --slug no-such-farm", "Production" },
        { "reactivate-account --slug no-such-farm", "Production" },
```
Replace with:
```csharp
        { "suspend-account --slug no-such-farm", "Production" },
        { "reactivate-account --slug no-such-farm", "Production" },
        // #732 — Production on purpose, and both codes are absent so the verb reaches its
        // OWN clean exit 1 rather than mutating anything. What must never happen is a crash
        // out of service registration before the verb's code runs (#331's class).
        { "rename-account --slug no-such-farm --new-slug no-target-farm", "Production" },
```

## 3d. Build and run — DO NOT COMMIT until increment 5
Run **G1**, then **G2 narrowed** to `AccountLifecycleCommandTests` (all green, including the pre-existing
suspend/reactivate cases). Then run **G2 narrowed** to `CliDispatcherTests` and **expect RED** on
`Registry_ContainsEveryVerb_WithNoDuplicateNames` — its expected array is hand-pinned. Fix it in this
commit: find (occurs exactly once)
```csharp
            ["bootstrap-admin", "list-accounts", "migrate", "provision-account", "reactivate-account", "recover-admin", "seed", "suspend-account"],
```
and replace with the same list with `"rename-account"` inserted in **alphabetical order** — which is
**after `recover-admin` and before `seed`**, because `reco` < `rena` < `seed`:
```csharp
            ["bootstrap-admin", "list-accounts", "migrate", "provision-account", "reactivate-account", "recover-admin", "rename-account", "seed", "suspend-account"],
```
Re-run until green. Do not "fix" it by dropping `.OrderBy` from the assertion.

```bash
git add src/Cluckwork.Api/Cli/RenameAccountCliCommand.cs src/Cluckwork.Api/Cli/CliDispatcher.cs \
        tests/Cluckwork.Api.IntegrationTests/AccountLifecycleCommandTests.cs \
        tests/Cluckwork.Api.IntegrationTests/CliDispatcherTests.cs \
        tests/Cluckwork.Api.IntegrationTests/OneShotVerbMinimalConfigTests.cs
# DO NOT COMMIT HERE — continue to increment 5
```

===================================================================================
# INCREMENT 4 — the SPA audit vocabulary, in every locale
===================================================================================

## 4a. RED — pin the new action's label in every locale

Append inside the first `describe` block of `web/src/i18n/enums.test.ts` (mirroring the existing
`resolves Account.Provisioned through its own key in every locale` test verbatim in shape):

```typescript
  // #732 — the audit filter and the row label are driven by the same three maps, and a
  // missing locale key falls back to English silently. Pinned per locale like the
  // Account.Provisioned case above.
  it("resolves Account.Rename through its own key in every locale", async () => {
    for (const [language, catalog] of Object.entries(RESOURCES)) {
      await i18n.changeLanguage(language);
      expect(auditActionLabel("Account.Rename")).toBe(
        catalog.enums["auditAction.Account.Rename"],
      );
    }
  });
```

| Gate row + narrowing | Command as run | Named test | Assertion | Stable discriminator | Generated fragments | Path driven | What the fixture seeds | Which other guard returns the same failure | Negative-test proof |
|---|---|---|---|---|---|---|---|---|---|
| G5 narrowed | `cd web && npx vitest run src/i18n/enums.test.ts` | `enums module (#182) > resolves Account.Rename through its own key in every locale` | `expect(auditActionLabel("Account.Rename")).toBe(catalog.enums[…])` | **`Account.Rename`** in the received/expected diff, or `Unable to find …` — report the actual text | none | `auditActionLabel()`, the function `AuditPage` renders through — not the raw map | `RESOURCES` = en/es/tl | `catalogParity` also fails on a missing key — different file, so report both | n/a |

Expected RED: the assertion fails because `AUDIT_ACTION_VALUES` has no `"Account.Rename"` and no locale
catalog has the key.

## 4b. GREEN — the three maps and three catalogs

`web/src/i18n/enums.ts` — find (occurs exactly once):
```typescript
  "Account.Suspend", "Account.Reactivate", "Account.Provisioned",
```
Replace with:
```typescript
  "Account.Suspend", "Account.Reactivate", "Account.Provisioned", "Account.Rename",
```
find (occurs exactly once):
```typescript
  "Account.Provisioned": "enums:auditAction.Account.Provisioned",
```
Replace with:
```typescript
  "Account.Provisioned": "enums:auditAction.Account.Provisioned",
  "Account.Rename": "enums:auditAction.Account.Rename",
```
find (occurs exactly once):
```typescript
  "Account.Provisioned": "Account",
```
Replace with:
```typescript
  "Account.Provisioned": "Account",
  "Account.Rename": "Account",
```

`web/src/i18n/en.ts` — find (occurs exactly once):
```typescript
    "auditAction.Account.Provisioned": "Farm provisioned",
```
Replace with:
```typescript
    "auditAction.Account.Provisioned": "Farm provisioned",
    "auditAction.Account.Rename": "Farm code changed",
```
`web/src/i18n/es.ts` — find (occurs exactly once):
```typescript
    "auditAction.Account.Provisioned": "Granja aprovisionada",
```
Replace with:
```typescript
    "auditAction.Account.Provisioned": "Granja aprovisionada",
    "auditAction.Account.Rename": "Se cambió el código de la granja",
```
`web/src/i18n/tl.ts` — find (occurs exactly once):
```typescript
    "auditAction.Account.Provisioned": "Ginawa ang bukid",
```
Replace with:
```typescript
    "auditAction.Account.Provisioned": "Ginawa ang bukid",
    "auditAction.Account.Rename": "Binago ang code ng bukid",
```

> **Translation note (AGENTS.md #688).** These three strings are UI labels, not help prose naming a
> labelled control, so #688's label-pairing rule does not bind — but the **es** wording must agree with
> the farm-code vocabulary already used in `es.ts`, and **tl** with its own. Read
> `grep -n '"auditAction.Account.Suspend"' -A 3 web/src/i18n/es.ts web/src/i18n/tl.ts` and the sign-in
> strings that name the farm code, and if a better-established noun exists for "code" in either locale,
> **use it and report the substitution** — a native-speaker review (Phase 1.5 epic item) is the follow-up,
> and an inconsistent noun here is exactly what #688 records.

## 4c. GREEN — the Audit UI offers it

Append this test to `web/src/routes/AuditPage.test.tsx`, beside the existing #247 filter test (which
renders an **empty** result list and asserts the `<option>` elements are present — it does **not** build a
row; the driver read it at `AuditPage.test.tsx:142-154`). It uses the file's existing `renderAudit()` and
`screen`, and needs no new import:

```tsx
  // #732 — the same #247 rule: a new server-emitted action must be OFFERED in the filter,
  // labelled, and value-preserved. When the client list drifted, rows showed only under
  // "All actions" and no test noticed.
  it("offers the farm code change action as filterable, labelled and value-preserved (#732)", async () => {
    renderAudit();
    await screen.findByText("No audit events yet.");
    const rename = screen.getByRole("option", {
      name: "Farm code changed",
    }) as HTMLOptionElement;
    expect(rename.value).toBe("Account.Rename");
    // The raw code must not leak as the visible option text.
    expect(screen.queryByRole("option", { name: "Account.Rename" })).not.toBeInTheDocument();
  });
```
If `renderAudit()`'s empty-state text is not `No audit events yet.` at your head, read the #247 test and
use whatever line it waits on — that is a runbook defect to report, not a reason to invent a harness.

## 4d. Run — DO NOT COMMIT until increment 5
Run **G5**, **G6**, **G7** (all green; G5's test count must be **above** the 2706 baseline by exactly the
cases you added — report the new number), then **G2 narrowed** to `AuditVocabularyCoverageTests` — which
must now be **green** (this increment closes increment 2's expected red).

```bash
git add web/src/i18n/enums.ts web/src/i18n/en.ts web/src/i18n/es.ts web/src/i18n/tl.ts \
        web/src/i18n/enums.test.ts web/src/routes/AuditPage.test.tsx
# DO NOT COMMIT HERE — continue to increment 5
```

===================================================================================
# INCREMENT 5 — teach the tenant-bypass registry the new query site
===================================================================================

No red phase of its own: this increment **is** the fix for a guard that is red (or goes red) since
increment 2. Run it now regardless.

## 5a. Find out what the guard actually wants — do not guess the key

```bash
dotnet test tests/Cluckwork.Application.Tests --filter "FullyQualifiedName~TenantBypass" -v q --nologo
```
Read the failure text. `TenantBypassRealTreeTests` prints **the identity to paste** for each unclassified
site (#632: keyed by enclosing symbol + set + a hash of the query's Roslyn tokens — **never a line
number**, and a comment above the query is invisible to it while an edit to the query is not).

## 5b. Add the rows it printed

`tests/Cluckwork.Application.Tests/TenantBypass/Data/tenant-bypass-allowlist.json` — append an entry for
both `Cluckwork.Infrastructure.Identity.AccountRenameService.ResolveCurrentAsync` **and**
`Cluckwork.Infrastructure.Identity.AccountRenameService.IsTargetTakenAsync` (paste the exact symbols and
signatures the failure printed; the file is a JSON array of
`{ "symbol", "file", "justification" }`, and the existing
`Cluckwork.Api.Cli.AccountSlugLookup.ResolveAsync(…)` entry is the wording model). Justification must say
why the bypass is justified, not what it does:
For `ResolveCurrentAsync`:
`Operator CLI resolves a farm by its globally unique code before any tenant is resolved; the service's
locked read is tenant-keyed. Same justified call site as AccountSlugLookup.ResolveAsync (#536).`
For `IsTargetTakenAsync`:
`Operator CLI checks global destination-code availability before any tenant is resolved; the global
IX_Accounts_Slug index and unique-violation translation remain authoritative (#732).`

If the failure also lists sites in `Data/filter-free-set-sites.tsv` (the `db.<Table>` leg), add one row
per printed identity — **tab-separated, four columns, the fourth a NON-EMPTY reason**; a blank reason
passes every other check and excuses a query with no justification (#698).

**Do not re-pin any row whose SQL nobody touched.** If a row you did not write shows as stale, STOP and
report it — that is the #632 symptom, not your edit.

## 5c. Run and commit
```bash
dotnet test tests/Cluckwork.Application.Tests --filter "FullyQualifiedName~TenantBypass" -v q --nologo
```
Must be green.
```bash
git add tests/Cluckwork.Application.Tests/TenantBypass/Data/
git commit -m "feat(cli): rename-account changes a farm code with an audit trail (#732)"
```

===================================================================================
# INCREMENT 6 — docs: the runbook stops teaching raw SQL
===================================================================================

No red phase (docs). **Two guards walk tracked prose and will fire on what you write here** — this is the
increment most likely to cost a round trip, so read both rules first:
- `SchemaDocsTests.PostgresImagePin_IsOneIdenticalStringAcrossEveryTrackedFile` — scans **every tracked
  file including `docs/runbooks/`**. A bare `postgres:<tag>` in prose fails it (#508 was earned exactly
  this way). **Deleting** #731's section removes two such literals, which is fine; **do not add any**.
- `TenancyDocsFreshnessTests.NoTrackedFileDescribesTenancyAsDormant` — scans every tracked file for
  the stale-tenancy expressions encoded in that test. Read the regex there; do not copy its matched
  literals into any tracked document while explaining history.

## 6a. Replace the runbook section

In `docs/runbooks/provisioning-a-new-farm.md`, delete the whole section that begins
`## Renaming the default farm's code` and ends immediately before `## Drill`, and write in its place a
section with the same heading containing: **when to use** (an upgraded database stamped `default-farm`),
**not this runbook** (a farm created by `provision-account` — its code was chosen on purpose), **blast
radius** (one row; sessions bind to the account id so nobody is signed out; client-side remembered code
and palette cache go stale until the next explicit sign-in; `?farm=<old>` links and printed material are
stale the moment it commits), **prerequisites** (`list-accounts` shows the farm; the DML-only runtime role
is enough; no migrator credential), **procedure** (`list-accounts`, then
`rename-account --slug <current> --new-slug <new> --reason "<change ref>"`, then `list-accounts` again),
**verify** (sign in with the new code; the old code returns `Auth.UnknownFarmCode`), **if it fails** — one
line per error the service can return: `Account.SlugInvalid`, `Account.SlugTaken`, `Account.SlugStale`,
`Accounts.NotFound`.

Two sentences you MUST include, in your own words but with this content:
1. A code a farm has moved off is **immediately reusable**, so run `list-accounts` immediately before
   renaming — `--slug` names whoever holds that code now.
2. Tell every user the new code **before** it lands; the sign-in form's remembered code and every
   bookmarked `?farm=<old>` link go stale at commit time.

Do **not** keep the SQL `UPDATE`, the `psql` invocations, or the "no audit row is written; record it in
the deployment repo" instruction. Do not add a "if you are on an older image" fallback with SQL in it —
that reintroduces the literal `postgres:` image reference and the un-audited path this slice exists to
retire.

## 6b. Correct the two glossary claims and the epic

- `specs/product/GLOSSARY.md` — **two** places: the "Farm code" paragraph (`:689`-ish, "chosen once and
  **immutable** — a provisioning typo has no in-app fix this phase") and the "Farm provisioning"
  paragraph (`:726`-ish, "A farm code is immutable, so the command echoes its normalized value").
  Rewrite both: the code is now renameable **by the `rename-account` operator verb and nothing else** —
  no endpoint, no Settings field — and the echo-before-write rationale in the provisioning paragraph
  should keep its meaning (the code is still chosen deliberately) while dropping the immutability claim.
  Confirm with `grep -n "immutable" specs/product/GLOSSARY.md`.
- `docs/decisions/732-farm-code-rename.md` — **new file**, following `docs/decisions/TEMPLATE.md`
  (What happened · The rule · Why not the obvious alternative · What this does NOT cover · How it is
  enforced). Content: #731's raw-SQL path was the symptom; the rule is that a farm code changes only
  through `Account.Rename` via the verb, with the audit row and the `Version` bump; the obvious
  alternative rejected is an HTTP endpoint / Settings field (epic #530 decision 2 rejected a
  cross-tenant operator HTTP API, and a Settings field would let a farm rename itself out of its own
  users' sign-in mid-shift); **what this does NOT cover** is the retired-code list (a renamed-away code
  is reusable — accepted cost, named in the verb's help) and the client-side caches; **how it is
  enforced**: `AccountRenameServiceTests`, `AccountLifecycleCommandTests`, `CliDispatcherTests`,
  `OneShotVerbMinimalConfigTests`, `AuditVocabularyCoverageTests`, `TenantBypassRealTreeTests` — by name.
  Status `accepted`, date today, and say **"No incident"** for the retired-code choice and "earned" for
  the raw-SQL path (#731 documented it).
- **The decision table row 10 is in the EPIC ISSUE BODY, not in a file.** Verify with
  `gh issue view 530 --json body -q .body | grep -n "Farm-code rename"` — it prints
  `| 10 | Farm-code rename | **Deferred.** The slug is immutable in this epic | Renameable with a retired-code list |`.
  **Do not edit that body** (AGENTS.md: an issue body is history). Instead, in the new decision record,
  state in its first paragraph that it **reverses epic #530 decision 10**, and add the epic checklist
  entry as a **comment** on #530 (`gh issue comment 530 --body "T10 shipped in PR #<n>; decision 10
  reversed by docs/decisions/732-farm-code-rename.md"`). The epic checklist line for T10 is checked off by
  the driver at 13b, not by you.
- `docs/decisions/530-multi-farm-tenancy.md` — **no table to edit** (verified: the file has numbered
  prose sections, no decision table). Add one line at the end of its §4 ("Why `Account.Slug` uses a plain
  unique index" paragraph, which is the section that speaks about the code's immutability) reading:
  `Renameable since #732 by the rename-account verb; the plain unique index still suffices because
  Account.Rename writes an already-normalized value. See 732-farm-code-rename.md.` Find the exact
  paragraph by `grep -n "The reasoning is repeated at" docs/decisions/530-multi-farm-tenancy.md` and
  append after that sentence. If the anchor moved, STOP and report rather than guessing a location.
- `docs/architecture.md:22` — the one-shot-verb diagram lists the verbs by name; add `rename-account`
  there. `deploy/.env.example:32` — same list in a comment; add it.
  `src/Cluckwork.Api/Hosting/CluckworkTelemetryServiceCollectionExtensions.cs:14-15` and
  `ServingBootGuards.cs:9` — both enumerate the verbs in a comment; add `rename-account` to both.
  These are comments, but they are the enumeration a future author reads as complete; AGENTS.md's
  runbook-craft rule says a comment that enumerates a set is a guard with no test, so **update them
  rather than leaving them one verb stale**.

## 6c. Commit
```bash
git add docs/runbooks/provisioning-a-new-farm.md specs/product/GLOSSARY.md \
        docs/decisions/732-farm-code-rename.md docs/decisions/530-multi-farm-tenancy.md \
        docs/architecture.md deploy/.env.example \
        src/Cluckwork.Api/Hosting/CluckworkTelemetryServiceCollectionExtensions.cs \
        src/Cluckwork.Api/Hosting/ServingBootGuards.cs
git commit -m "docs(732): rename-account replaces the raw-SQL farm code procedure"
```

===================================================================================
# INCREMENT 7 — the in-app help and glossary stop saying it cannot change
===================================================================================

AGENTS.md's documentation rule makes this a missing-test-grade omission if skipped: a user-visible
concept changed, so the SPA Help page and in-app glossary change in the same PR.

## 7a. RED — the help page must name the new system actor

Append inside the `describe` in `web/src/routes/HelpPage.test.tsx` that already asserts
`(suspend-account)` / `(provision-account)` render (around line 310-325 — read it first):
```tsx
    // #732 — the actor list and the accountability sentence are the operator's only
    // in-app explanation of what a bracketed name means; a new verb is invisible until
    // it appears here.
    it("names the rename-account system actor in every help catalog", () => {
      for (const catalog of [en, es, tl])
        expect(catalog.help.auditSystemActors).toContain("(rename-account)");
    });
```
| Gate row + narrowing | Command as run | Named test | Assertion | Stable discriminator | Generated fragments | Path driven | What the fixture seeds | Which other guard returns the same failure | Negative-test proof |
|---|---|---|---|---|---|---|---|---|---|
| G5 narrowed | `cd web && npx vitest run src/routes/HelpPage.test.tsx` | `names the rename-account system actor in every help catalog` | `toContain("(rename-account)")` | **`(rename-account)`** in the diff | none | `HelpPage`'s rendered `auditSystemActors` string | `en`/`es`/`tl` catalogs | none | n/a |

## 7b. GREEN — three catalogs, two strings each

- `help:auditSystemActors` in `en.ts` / `es.ts` / `tl.ts`: add one clause naming `(rename-account)` as
  the command that changes a farm's code, carrying the machine and the reason like the suspend/reactivate
  clause does. Keep the closing sentence (`Everything else names the person who did it.`) **last** — the
  existing test asserts it.
- `help:glossaryFarmCodeDef` in all three: the English currently says
  `It is lowercase and it does not change.` Replace with a sentence that it is lowercase and that **an
  operator can change it** on request, and that the change means signing in with the new code. Translate
  the replacement in `es` and `tl` too — `catalogParity` checks keys, **not** that a translation was
  updated, so an English sentence left in `es.ts` passes every gate and is exactly the #662/#688 class.

## 7c. Run and commit
Run **G5** (green, count above baseline), **G6**, **G7**.
```bash
git add web/src/i18n/en.ts web/src/i18n/es.ts web/src/i18n/tl.ts web/src/routes/HelpPage.test.tsx
git commit -m "docs(web): help and glossary describe a renameable farm code (#732)"
```

===================================================================================
# FINAL — full gates, then the mutation ledger, then push and PR
**Order is load-bearing:** the PR body must report mutation results, so **the mutation ledger runs BEFORE
the push**, and every mutation row's rebuild must already be done.
===================================================================================

```bash
dotnet restore Cluckwork.sln --locked-mode                     # G3
dotnet build Cluckwork.sln --configuration Release --no-restore # G1
dotnet test Cluckwork.sln --configuration Release --no-build --verbosity normal   # G2 — FOREGROUND
tools/schema-docs/generate.sh --check                          # G4
(cd web && npm ci && npm run test:coverage && npm run build && npm run verify:sw)  # G5 G6 G7 — subshell
git status --short                                              # expect: empty
```
**Then run the whole MUTATION CHECKS ledger below, restore every mutant, rebuild, and re-run G2 once.**
Only after the ledger is complete and `git grep -n -e MUTANT -e 'DEBUG-' -- src tests` prints nothing do you
push. The web commands above run in a **subshell** so the root-relative commands after them do not execute
from inside `web/`.
Report **every** summary line verbatim: the four .NET assembly totals (and the total), the web
`Test Files` / `Tests` lines, and `docs/schema/ is up to date.` **Compare each against the baseline as a
delta, not as absolute green:** the .NET total starts at **2304** and must be **2304 + (new cases you
authored)**, and the web total starts at **2706** and must rise by yours. Report the arithmetic
(`2304 + <new> = <observed>`), and a total BELOW the baseline is a STOP — a test that stopped being
discovered is not a passing suite.

Then — and only after every mutation row is recorded and the mutant grep above prints nothing:
```bash
git push -u origin feat/732-rename-account-verb
gh pr create --base main --title "feat(cli): rename-account verb to change a farm code (#732)" --body "…"
```
PR body must state, in its own words: what ships; that #731's SQL section is **replaced**; the two
accepted costs (reusable retired code; stale client caches); **which mutation rows this session ran and
which are implementer-attested, not driver-verified**; and the new test counts. Do not write "verified"
for anything you did not run.

===================================================================================
# MUTATION CHECKS — prove the guards bite
===================================================================================

Every mutant below **compiles**. Apply → run the NAMED test → restore → **rebuild** → confirm green.
Label each in place with `// MUTANT M<n>: <what this breaks>` and delete the marker on restore.
Finish with: `git grep -n -e MUTANT -e 'DEBUG-' -- src tests` → **must print nothing** (tracked text only;
a recursive tree grep matches compiled binaries and will fail this check spuriously).

Rebuild command for every row, both sides (paste from your run, do not restate a gate row):
`dotnet build Cluckwork.sln --configuration Release --no-restore`

| # | Kind | Mutate | Supplied elsewhere? | Named test | Expected result + failure | Rebuild command run | Observed |
|---|---|---|---|---|---|---|---|
| C | control | `src/Cluckwork.Infrastructure/Identity/AccountRenameService.cs`: change the audit `reason:`-adjacent comment text inside the `details:` block comment (a `//` line only — no code). Confirm no test reads it: `git grep -n "the same accountability payload" -- tests` must print nothing. | n/a — not a deletion | *(none)* | **GREEN** — report it green | | |
| 1 | guard | `Account.cs` `Rename`: delete the `Version++;` line | nothing — this line is the only source | `AccountSlugTests.Rename_ChangesTheSlugAndBumpsVersion` | **RED** — `Assert.Equal() Failure: expected 1, actual 0` | | |
| 1b | guard | same `Version++;` deletion, but run the concurrency guard rather than the value guard | nothing | `AccountSlugRaceTests.TwoConcurrentRenames_TheLoserGetsAConcurrencyConflict` | **RED** — no `DbUpdateConcurrencyException`; both stale snapshots save, proving why the numeric assertion alone is insufficient | | |
| 2 | guard | `Account.cs` `Rename`: delete the entire no-op `if (string.Equals(…)) return Result.Success();` block (do not substitute a constant condition; warnings are errors) | nothing | `AccountSlugTests.Rename_ToTheSameCode_ChangesNothing_AndDoesNotBumpVersion` | **RED** — `Assert.Equal() Failure: expected 0, actual 1` (Version advanced by a command that changed nothing) | | |
| 3 | guard | `Account.cs` `Rename`: replace `TryValidateSlug(newSlug)` with `Result.Success((newSlug ?? "").Trim())` — i.e. skip the pattern **and** the reserved set | nothing | `AccountSlugTests.Rename_RejectsEveryReservedSlug_AndChangesNothing` | **RED** — `Assert.True() Failure` on `result.IsFailure` for `api` | | |
| 4 | guard | `AccountRenameService`: delete the post-lock `if (!string.Equals(account.Slug, currentSlug…))` block | the tenant-keyed lock preserves the id but does **not** make the earlier slug lookup atomic with that lock | `AccountRenameServiceTests.Rename_WhenSourceChangesAfterLookup_RefusesAndDoesNotOverwrite` | **RED** — after the blocker commits, the service succeeds and changes the row to the attempted code instead of returning `Account.SlugStale`; the committed winner is overwritten | | |
| 5 | guard | `AccountRenameService`: change `var changed = !string.Equals(account.Slug, target…)` to `var changed = true` | nothing | `AccountRenameServiceTests.Rename_ToTheSameCode_ChangesNothing` | **RED** — `Assert.False(outcome.Changed)` fails, and the audit-count assertion would also fail; report which fired first | | |
| 6 | guard | `AccountRenameService`: move the `if (changed) await audit.WriteAsync(…)` block to **after** `await transaction.CommitAsync(token)` | the audit row's own `AddAsync` — this tests ordering, not existence | `AccountRenameServiceTests.Rename_ChangesTheCode_BumpsVersion_AndWritesOneAuditRow` | **RED** — `SequenceContains`/`SingleAsync` finds no `Account.Rename` row: the code changed and committed with no trail | | |
| 7 | guard | `AccountRenameService`: replace `currentUser.ResolveSystemActor(SystemActors.RenameAccount);` with `_ = currentUser;` (preserves a harmless parameter read so CS9113 does not mask the actor guard) | nothing | `AccountRenameServiceTests.Rename_ChangesTheCode_BumpsVersion_AndWritesOneAuditRow` | **RED** — `InvalidOperationException` from `AuditWriter` ("Audit events require a resolved actor…"), surfacing as the verb's/service's failure, not a silent `(unresolved)` row (#500) | | |
| 8 | guard | `AccountRenameService`: delete the whole `try {` / `} catch (DbUpdateException ex) when (…) { … }` wrapper, leaving the bare `return await AmbientTransaction.RunAsync(…)` — **deleting only the `catch` leaves an invalid `try` with no handler and will not compile** | nothing | `AccountRenameServiceTests.Rename_TwoFarmsRaceForOneCode_ExactlyOneWinsThroughTheIndexCatch` | **RED** — both destination pre-reads completed while the target was free, so the losing `DbUpdateException` escapes instead of becoming `Account.SlugTaken` | | |
| 9 | guard | `AccountRenameService`: delete the `IsTargetTakenAsync` pre-read block, leaving the unique-index catch intact | the database index + catch is deliberately authoritative | `AccountRenameServiceTests.Rename_ToATakenCode_ReturnsSlugTaken_AndLeavesBothFarms` | **GREEN is expected and must be reported as a deliberate second layer** — the friendly error now comes from the catch. Row 8 separately proves that catch with a race that cannot hit the pre-read. Restore before continuing | | |
| 10 | guard | `AccountRenameService`: delete ONLY the `tenant.Resolve(accountId.Value);` line (leave the lookup and its null-check above it intact) | the lookup still resolves the id and the early-return still fires for unknown codes; only the **locked read** loses its tenant | `AccountRenameServiceTests.Rename_ChangesTheCode_BumpsVersion_AndWritesOneAuditRow` | **RED** — the service returns `Accounts.NotFound` (`GetCurrentLockedAsync` selects `WHERE "Id" = Guid.Empty`), so `Assert.True(outcome.Success, "expected success, got Accounts.NotFound")` fails | | |
| 11 | guard | `AccountRenameService`: replace the missing-id early return with `accountId = Guid.Empty;` (assignment preserves nullable flow), then leave the normal `tenant.Resolve(accountId.Value)` and destination pre-read in place | the destination pre-read and locked read are deliberate later layers | `AccountRenameServiceTests.Rename_WithMissingSource_ReturnsNotFound` | **GREEN is expected:** the destination pre-read runs, then the locked read returns NotFound. Next also delete the locked-read null return and change the **first following dereference**, `account.Slug`, to `account!.Slug` so nullable flow is suppressed from that point; the same test must be **RED** with `NullReferenceException` at the stale-slug comparison. Restore all edits and rebuild | | |
| 12 | guard | `CliDispatcher.cs`: remove `new RenameAccountCliCommand(),` from `Commands` | nothing | `CliDispatcherTests.Registry_ContainsEveryVerb_WithNoDuplicateNames` | **RED** — the pinned verb array no longer matches | | |
| 13 | guard | `OneShotVerbMinimalConfigTests.cs`: delete the `rename-account` case **step 3c added** | nothing | `OneShotVerbMinimalConfigTests.EveryDispatchedVerb_HasAMinimalConfigCase` | **RED** — `verb(s) classified OneShot but never run here: rename-account` | | |
| 14 | guard | `web/src/i18n/enums.ts`: remove `"Account.Rename"` from `AUDIT_ACTION_VALUES` only (leave the key maps) | nothing | `AuditVocabularyCoverageTests.AuditActions_registry_matches_the_SPA_AUDIT_ACTION_VALUES_list` | **RED** — set mismatch naming `Account.Rename` missing from the client | | |
| 15 | guard | `web/src/i18n/tl.ts`: delete the `auditAction.Account.Rename` line (one locale only) | `fallbackLng` serves English, so **the UI still renders something** — which is why this needs a test | `web/src/i18n/enums.test.ts` → `resolves Account.Rename through its own key in every locale` **and** `catalogParity.test.ts` | **RED** — parity `extra key in en / missing in tl` | | |

**Closed-set ledger:** `n/a — this slice's guards are not closed sets.` The one input with a set shape is
the farm code, and it is **open-valued** (any string): the rows above cover every *included* class the
domain names (valid shape) plus the two *excluded* classes it names (invalid shape via M3's `api`/short
cases, reserved via M3), and the excluded side beyond those is unbounded because the pattern is a regex
over arbitrary input, not an enumeration — so a per-member ledger has no members to enumerate. The
reserved **set** itself is covered member-by-member by the existing
`Rename_RejectsEveryReservedSlug_AndChangesNothing` loop, which iterates `Account.ReservedSlugs` rather
than sampling one.

**Criterion × surface ledger** (the audit label is one criterion delivered on three surfaces):

| Criterion | Surface | Shared with, and how established | Mutation on that surface | Named test that must go RED | Observed | Rebuild command run |
|---|---|---|---|---|---|---|
| The new audit action is labelled, not raw | Audit UI filter option list (`AUDIT_ACTION_VALUES`) | none — the filter list is this array, established by reading `AuditPage`'s filter construction and `enums.ts:298` | M14 | `AuditVocabularyCoverageTests.AuditActions_registry_matches_the_SPA_AUDIT_ACTION_VALUES_list` | | |
| The new audit action is labelled, not raw | Row label lookup (`AUDIT_ACTION_KEYS` + `en.ts`) | shares the `auditActionLabel()` entry point with the filter — established by reading `enums.ts`'s `AUDIT_ACTION_KEYS` and `auditActionLabel` | delete the `AUDIT_ACTION_KEYS` line only (leave the array) | `enums.test.ts` → `maps every value to a real enums:* key present in en.ts` | | |
| The new audit action is labelled, not raw | Per-locale catalogs (`es.ts`, `tl.ts`) | none — separate files; `catalogParity` establishes the set equality by iterating `RESOURCES` | M15 | `enums.test.ts` → `resolves Account.Rename … in every locale` + `catalogParity.test.ts` | | |

**Restore check after every row:** rebuild, then run the full **G2** once at the end and confirm the
totals match the FINAL section's numbers.

## Report back — every value here is produced by a step above

1. Base SHA (`git rev-parse HEAD` at Step 0) and final head SHA (`git rev-parse HEAD` after FINAL).
2. G1/G2/G3/G4/G5/G6/G7 final summary lines, verbatim, and the .NET total.
3. Per increment: the commit SHA and its subject.
4. The 1a/2a/3a/4a/7a RED tables as you actually observed them (command, named test, discriminator).
5. Every mutation row: kind, observed result, the rebuild command you ran. Say which you did **not** run
   and why. Row 9's duplicate judgement, explicitly.
6. The two flagged items from 2c: did `new { from = … }` compile, and what did `nameof(Account)` render.
7. The tenant-bypass identities you pasted in 5b, verbatim, and whether any row you did not author showed
   stale.
8. The `grep -n -e MUTANT -e 'DEBUG-' -- src tests` output (must be empty).
9. `git status --short` at the end (must be empty) and the PR URL.

## Stop-and-report limit

If any single increment needs a **third** attempt on the same step, or you hit a STOP paragraph, stop and
report rather than improvising: the runbook's rules outrank any local repair, and PROTECTED blocks are
never yours to fix.
