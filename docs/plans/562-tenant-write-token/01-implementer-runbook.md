# Runbook — #562: the database refuses a detached cross-tenant write (AccountId as a concurrency token)

You are an autonomous coding agent with FULL tools (read, edit, write, bash) in the `cluckwork` repo
(.NET 10 / EF Core 10 / Npgsql / Postgres via Testcontainers; cwd = the repo root of a git worktree on
branch `fix/562-account-id-concurrency-token`). Execute this runbook top to bottom. You do EVERYTHING:
edit, build, test, commit, push, open the PR.

**Mode: bugfix.** The regression tests are the spec. They are authored here verbatim, ordered RED before
the fix, and each must fail with the captured symptom named in its row — not merely fail.

## Rules
- Transcribe the exact code blocks VERBATIM (comments and whitespace included). Do not reformat, rename,
  or "improve" them. Blocks marked **PROTECTED** are correctness-critical (tenant isolation): transcribe
  or stop, never repair.
- Run the commands EXACTLY as given. Do not invent flags.
- After every build/test command, if it is not clean, STOP and fix before continuing. **An expected RED
  is a clean result** — but only that exact RED: the command as written, the named test, failing at the
  named assertion with the stable discriminator the row names. Generated fragments (GUIDs) differ and are
  expected to. **Anything else is a STOP, however red it looks**: a compile error, a discovery or runner
  failure, zero tests collected, a different test failing, or a baseline failure that has changed shape.
- **Every gate command a step runs cites its gate row** — by ID, never retyped. A filtered invocation
  names which row it narrows.
- Do NOT touch: `src/Cluckwork.Infrastructure/Persistence/Migrations/20260801190854_InitialCreate.cs` or
  any existing migration (frozen, #407); `src/Cluckwork.Infrastructure/Repositories/**` (the fix is not
  at the repository layer — #561 already pinned those reads); `tests/Cluckwork.Api.IntegrationTests/TenantWriteGuardTests.cs`
  (INV-3: the tracked-path checks are unchanged and so is their test); `src/Cluckwork.Infrastructure/Identity/IdentityProvider.cs`
  (its stale "#176 xmin" comment at ~1762 is a noted follow-up, not this slice); `docs/schema/**` by hand
  (generated; regenerate only via the script, and this slice expects NO change there); `web/**`;
  `specs/product/GLOSSARY.md` and the Help page (no user-visible behaviour changes).
- Files you may create or edit — the complete allow-list:
  - `src/Cluckwork.Infrastructure/Persistence/AppDbContext.cs`
  - `src/Cluckwork.Infrastructure/Persistence/Interceptors/TenantStampInterceptor.cs`
  - `src/Cluckwork.Application/Common/SecurityEvents.cs`
  - `src/Cluckwork.Infrastructure/Persistence/Migrations/<timestamp>_AccountIdConcurrencyToken.cs` and `.Designer.cs` (generated in Increment 3)
  - `src/Cluckwork.Infrastructure/Persistence/Migrations/AppDbContextModelSnapshot.cs` (regenerated in Increment 3 — never hand-edited)
  - `tests/Cluckwork.Api.IntegrationTests/DetachedTenantWriteTests.cs` (new)
  - `tests/Cluckwork.Api.IntegrationTests/AccountIdConcurrencyTokenModelTests.cs` (new)
  - `tests/Cluckwork.Api.IntegrationTests/TenantWriteRefusalLoggingTests.cs` (new)
  - `tests/Cluckwork.Api.IntegrationTests/TrackedMutationReadTests.cs` (comments and one message only)
  - `AGENTS.md`, `docs/decisions/530-multi-farm-tenancy.md`, `docs/security/log-redaction-policy.md`
  - `docs/plans/562-tenant-write-token/01-implementer-runbook.md` (this file — committed in Increment 4, repo convention)
  - `docs/plans/562-tenant-write-token/PR-BODY.md` (scratch for `gh pr create`; created in FINISH, NEVER committed, deleted once the PR exists)
  Anything else, STOP and report.
- Work only on the branch named above. Never commit to `main`.
- A **mutation check** means: plant a bug on purpose, run the named test, and see whether it notices.
  RED means the test guards the code; GREEN means nothing was watching. Then restore, **rebuild**, and
  re-run to confirm you are clean.
- Run the FULL test suite — **G2 exactly as its row records it** — in the FOREGROUND at the end of
  Increment 4 and report its final summary lines verbatim. Do NOT background it.
- If a code block here conflicts with an existing test, STOP and report the conflict. Do NOT relax or
  delete that test.
- **Blocks marked PROTECTED are never edited, for any reason.** If one fails to compile or fails at
  runtime, STOP and report the exact error.
- Any OTHER block can fail three ways: (1) does not compile → fix minimally, report the exact error and
  your fix; (2) compiles but the block itself is wrong at runtime → same, and report it prominently as a
  runbook defect; (3) an assertion fails against the product → report the RED, never widen the assertion.

**Protected-block probe — filled by the driver:** every PROTECTED block below was applied, built and run
by the driver in a throwaway worktree on the base commit before this runbook was written
(`/tmp/562-preflight`, 2026-09-02): the walk compiled and turned all five Increment-1 tests green; the
interceptor's `ThrowingConcurrencyException`/`Async` overrides were verified against the EF Core 10 API
reference (signature and that EF routes `DbUpdateConcurrencyException` through them rather than through
`SaveChangesFailed` — the preflight's first draft used `SaveChangesFailed` and observed that EF never
reached it); the optional-logger constructor composes with the seven existing hand-constructions
`new TenantStampInterceptor(new TenantContext())` (one in `AppDbContextDesignTimeFactory.cs:83`, six in
migration tests) without touching them; `entry.Property(p.Name)` on a key property of an owned entry
resolves the owner's shadow key (observed on `InventoryItem.DefaultUnitCost#Money`).

**Existing instances of this pattern:** the walk is novel in this repo — the closest sibling is the
index walk immediately above it in `OnModelCreating` (#532, "walk every index and remove the ones not
led by AccountId"), which it mirrors in shape (walk `builder.Model`, exclude by a named property, no
hand list). The concurrency hook is novel — no other interceptor in `src/` overrides
`ThrowingConcurrencyException`. The `{SecurityEvent}` log line mirrors
`CluckworkRateLimitingServiceCollectionExtensions.cs:99` field for field (constant first, then named
scalars, Warning level).

## Verify prerequisites (run first)
```bash
git branch --show-current      # expect: fix/562-account-id-concurrency-token
git status --porcelain         # expect: only "?? docs/plans/562-tenant-write-token/01-implementer-runbook.md" (this file, untracked until Increment 4)
git log -1 --format=%H         # expect: 80b53f4bdf29d07652ef434852b1ac18d36208f5 (observed by the driver: `git rev-parse HEAD` on main, 2026-09-02)
dotnet --version               # expect: 10.0.302 (driver host; a different 10.0.x is informational, does not gate)
dotnet ef --version            # expect: 10.0.11 (driver host; informational, does not gate)
docker info --format '{{.ServerVersion}}'   # expect: a version string — Testcontainers needs a reachable daemon; gates every G2 run
git config core.hooksPath      # expect: .githooks (shared repo config; the pre-commit hook runs Domain + Application unit tests on any .cs commit, against the WORKING TREE, bypassable with --no-verify — do not bypass it)
```

**The commit gate is a prerequisite, not a surprise.** `.githooks/pre-commit` runs
`dotnet test tests/Cluckwork.Domain.Tests` and `tests/Cluckwork.Application.Tests` on any staged
`.cs`/`.csproj`/`.sln`, against the working tree (not the index). Every increment below commits a
compiling, green tree, so the hook passes by construction; if it fails, STOP and report — do not
`--no-verify`.

## Caller ledger — driver fills before dispatch

| Increment | Contract changed | Every production caller (from the enumeration) | What each does AT THIS COMMIT | Same-commit or later? | Observed at that commit (Phase 11) |
|---|---|---|---|---|---|
| 1 | none — model metadata only; no signature changes. Every `UPDATE`/`DELETE` on an `AccountId`-bearing entity gains `AND "AccountId" = @original`. | every tracked-loaded write in `src/` (repositories, `IdentityProvider`, `AccountSuspensionService`, `DailyEntryLockSweep`, seeders, Identity `UserStore`) — enumerated by the D2b review: none changes `AccountId` after construction, and `ApplicationUser.AccountId` already has `SetAfterSaveBehavior(Throw)` | compiles AND behaves: the new conjunct is always true for a tracked-loaded row | same commit | driver: full integration suite with the walk applied on the base, 1645/1645 (run 3, 2026-09-02) |
| 2 | `TenantStampInterceptor(TenantContext tenant, ILogger<TenantStampInterceptor>? logger = null)` — second parameter OPTIONAL | repo-wide enumeration, no file-type filter, of `TenantStampInterceptor(`: `src/Cluckwork.Api/Hosting/CluckworkPersistenceServiceCollectionExtensions.cs:50` (DI, `AddScoped` — resolves the logger), `src/Cluckwork.Infrastructure/Persistence/AppDbContextDesignTimeFactory.cs:83` and six tests (`AccountSlugMigrationTests.cs:39`, `FarmBannerMigrationDowngradeTests.cs:33`, `AccountScopedIdentityMigrationTests.cs:39`, `WorkerSaleAllocationPolicyMigrationTests.cs:27`, `SchemaDocsTests.cs:64`, `BaseReferenceDataMigrationTests.cs:49`) — all `new TenantStampInterceptor(new TenantContext())` | all compile unchanged (optional parameter) and behave: the hand-built ones get `NullLogger` | same commit | driver: Phase 11 build + those six tests green |
| 3 | none — empty migration + snapshot | `migrate` verb, `Database:MigrateOnStartup` boot path, the sim harness, `tools/schema-docs/generate.sh` | one extra `__EFMigrationsHistory` row, no DDL | same commit | driver: G4 |
| 4 | none — docs and comments | none | n/a | same commit | n/a |

| Expectation | Observed by | On | Gates which step |
|---|---|---|---|
| `dotnet ef migrations add` writes into `src/Cluckwork.Infrastructure/Persistence/Migrations/` (the snapshot's folder) with a 14-digit UTC timestamp prefix, and the generated `Up`/`Down` are EMPTY | the driver's preflight, `20260903010438_AccountIdConcurrencyToken.cs` | driver host, throwaway worktree | Increment 3 (a non-empty `Up` is a STOP) |
| `dotnet ef migrations has-pending-model-changes` prints `No changes have been made to the model since the last migration.` after Increment 3 | driver preflight | driver host | Increment 3 |
| `tools/schema-docs/generate.sh --check` prints `docs/schema/ is up to date.` after Increment 3 — the token is metadata, not schema | driver preflight (run 2) | driver host | Increment 3 (a diff is a STOP) |
| The model-only `AppDbContext` walk discovers exactly **29** `AccountId`-bearing entity types with a non-key `AccountId` | driver preflight, model-test RED list | driver host | Increment 1 (fewer than 29 is a STOP — the walk stopped walking) |

## Gate commands — driver fills before dispatch

Copied verbatim from `.github/workflows/ci.yml`, job `build-and-test`. Baselines were run by the driver
on `80b53f4b` on 2026-09-02 (`baseline-release.log` in the driver's records).

| ID | Gate | Source (path + job/step) | Command, verbatim | Baseline on the base SHA | Clean looks like |
|---|---|---|---|---|---|
| G1 | build | `.github/workflows/ci.yml`, `build-and-test`, step `Build` | `dotnet build Cluckwork.sln --configuration Release --no-restore` | clean: `0 Warning(s)` / `0 Error(s)` (warnings are errors in this repo) | the same two lines |
| G2 | test | `.github/workflows/ci.yml`, `build-and-test`, step `Test` | `dotnet test Cluckwork.sln --configuration Release --no-build --verbosity normal` | Domain 365, Application 234, AppHost 10, IntegrationTests 1645 — all passed, nothing already red | the same four `Test Run Successful.` blocks with IntegrationTests risen by **9** (3 + 2 + 4 new tests) → `Total tests: 1654`; the other three unchanged. Read against the baseline, not absolute green. **Narrowed form** (cite as "G2 narrowed to <test class>"): `dotnet test tests/Cluckwork.Api.IntegrationTests --configuration Release --no-build --filter "FullyQualifiedName~<TestClass>" --logger "console;verbosity=normal"` — the `--no-build` is deliberate: run G1 first, every time. |
| G3 | restore, locked | `.github/workflows/ci.yml`, `build-and-test`, step `Restore dependencies` | `dotnet restore Cluckwork.sln --locked-mode` | clean (exit 0) | exit 0 — this slice adds no package, so a `NU1004` here means something else changed; STOP |
| G4 | schema docs current | `.github/workflows/ci.yml`, `build-and-test`, step `Verify schema docs are current` | `tools/schema-docs/generate.sh --check` | `docs/schema/ is up to date.` | the same line — this slice expects NO docs change; a STALE report is a STOP |
| G5 | NuGet audit | `.github/workflows/ci.yml`, `build-and-test`, step `Audit NuGet dependencies (high+, blocking)` | `dotnet list package --vulnerable --include-transitive --format json --output-version 1 \| node .github/scripts/vuln-gate.mjs --ecosystem nuget --level high` | CI-attested on `80b53f4b`, not driver-verified (CI runs it on every PR; no package changes in this slice) | exit 0 — unaffected by this slice; do not run it, CI will |
| — | web job | `.github/workflows/ci.yml`, job `web` | none — no `web/` change in this slice; CI's web job runs unchanged bytes | n/a | n/a |

## Documentation surfaces — driver fills before dispatch

| Surface | Path / key | Locales | Increment | Verification procedure (RUN at Phase 11) | Verified by + SHA |
|---|---|---|---|---|---|
| Repo rule | `AGENTS.md`, the `**Multi-tenancy:**` bullet | n/a (English-only repo docs) | 4 | `grep -n "concurrency token on every entity that carries one (#562)" AGENTS.md` returns the bullet | driver fills |
| Decision record | `docs/decisions/530-multi-farm-tenancy.md` §5 | n/a | 4 | `grep -n "What it did not cover, and what closed it (#562" docs/decisions/530-multi-farm-tenancy.md`; `TenancyDocsFreshnessTests` green (in G2) | driver fills |
| Security-event contract | `docs/security/log-redaction-policy.md`, new "Tenant-isolation events" table + alert bullet | n/a | 4 | `grep -n "Tenant.WriteRefusedByDatabase" docs/security/log-redaction-policy.md` returns 3 lines (table row, alert bullet, and the constant's name in prose) | driver fills |
| In-code contract | `TenantStampInterceptor.cs` header + `TrackedMutationReadTests.cs` header | n/a | 2, 4 | `grep -rn "#562" src tests` — no line says the gap is open | driver fills |
| GLOSSARY / in-app Help | — | en/es/tl | — | **none — no user-visible behaviour**: a refused cross-tenant write was a 500-class bug path, now a 409; say so in the PR body | n/a |

## Step 0 — branch
The worktree was created on the branch already (Phase 10). Confirm only:
```bash
git branch --show-current   # expect: fix/562-account-id-concurrency-token
```

===================================================================================
# INCREMENT 1 — the database refuses a detached cross-tenant write (INV-1, INV-2)
===================================================================================

## 1a. RED — add the regression tests and the model guard
Check before you create — both must NOT exist:
```bash
git ls-files tests/Cluckwork.Api.IntegrationTests/DetachedTenantWriteTests.cs tests/Cluckwork.Api.IntegrationTests/AccountIdConcurrencyTokenModelTests.cs   # expect: empty
```
Create `tests/Cluckwork.Api.IntegrationTests/DetachedTenantWriteTests.cs` with EXACTLY this content (**PROTECTED**):
```csharp
namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Inventory;
using Microsoft.EntityFrameworkCore;

// #562 — the write guard's provenance gap, closed at the database.
//
// TenantStampInterceptor compares AccountId's ORIGINAL value against the
// resolved tenant, and that value is the database's only for an entity that
// was LOADED while tracked. DbSet.Update, DbSet.Remove and Attach seed the
// original values from the caller's own instance, so a hand-built stub
// carrying another farm's primary key and THIS farm's AccountId passed both
// halves of the check and the UPDATE/DELETE keyed on the primary key alone.
//
// Since #562 AccountId is a concurrency token on every entity that carries one
// (AppDbContext.OnModelCreating), so the statement the database runs is
// "WHERE Id = @id AND AccountId = @original" — the stub's original is the
// tenant's, the row's is not, zero rows match, and EF throws
// DbUpdateConcurrencyException. The interceptor never sees a difference; the
// database does. Three shapes, each of which was observed WRITING THROUGH on
// the unmodified tree before the fix:
//
//   * Update(stub)  — B's row relabelled to A (theft, not a leak);
//   * Remove(stub)  — B's row deleted;
//   * Attach(stub) as Unchanged + edit ONLY the owned Money — B's row's cost
//     rewritten. The interceptor cannot see this one at all: the principal is
//     Unchanged and the owned entry has no AccountId of its own.
//
// Each test asserts the refusal AND that the row is untouched, because a
// refusal that still mutated the row would pass a throws-only assertion.
[Collection(IntegrationCollection.Name)]
public sealed class DetachedTenantWriteTests(CluckworkWebApplicationFactory factory)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    private static DailyEntry NewEntry(Guid id, Guid accountId) =>
        DailyEntry.Create(id, accountId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Today);

    private static InventoryItem NewItem(Guid id, Guid accountId, long costMinorUnits) =>
        InventoryItem.Create(id, accountId, Guid.NewGuid(), $"Item {id:N}", InventoryCategory.Feed, "kg",
            new Money(costMinorUnits, "USD", 2));

    private async Task<(Guid accountA, Guid accountB)> TwoFarmsAsync()
    {
        var a = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var b = await factory.SeedAccountWithUserAsync($"b-{Guid.NewGuid():N}@test.local");
        return (a, b);
    }

    private async Task<Guid> SeedEntryForAsync(Guid accountB)
    {
        return await factory.WithTenantScopeAsync(accountB, async db =>
        {
            var entry = NewEntry(Guid.NewGuid(), accountB);
            db.DailyEntries.Add(entry);
            await db.SaveChangesAsync();
            return entry.Id;
        });
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> write)
    {
        try { await write(); return null; }
        catch (Exception e) { return e; }
    }

    [Fact]
    public async Task DetachedUpdate_StubWithForeignKeyAndOwnAccountId_IsRefusedByTheDatabase()
    {
        var (accountA, accountB) = await TwoFarmsAsync();
        var rowId = await SeedEntryForAsync(accountB);

        // B's primary key, A's AccountId, never loaded.
        var stub = NewEntry(rowId, accountA);

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(accountA, async db =>
        {
            db.DailyEntries.Update(stub);
            await db.SaveChangesAsync();
        }));

        var after = await factory.WithTenantScopeAsync(accountA, db =>
            db.DailyEntries.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(e => e.Id == rowId));

        Assert.True(thrown is DbUpdateConcurrencyException,
            $"Update(stub) was not refused by the database: thrown={thrown?.GetType().Name ?? "none"}; " +
            $"row AccountId after={after?.AccountId.ToString() ?? "ROW GONE"} (was B={accountB}); tenant A={accountA}");
        Assert.NotNull(after);
        Assert.Equal(accountB, after.AccountId);
    }

    [Fact]
    public async Task DetachedRemove_StubWithForeignKeyAndOwnAccountId_IsRefusedByTheDatabase()
    {
        var (accountA, accountB) = await TwoFarmsAsync();
        var rowId = await SeedEntryForAsync(accountB);

        var stub = NewEntry(rowId, accountA);

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(accountA, async db =>
        {
            db.DailyEntries.Remove(stub);
            await db.SaveChangesAsync();
        }));

        var after = await factory.WithTenantScopeAsync(accountA, db =>
            db.DailyEntries.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(e => e.Id == rowId));

        Assert.True(thrown is DbUpdateConcurrencyException,
            $"Remove(stub) was not refused by the database: thrown={thrown?.GetType().Name ?? "none"}; " +
            $"row after={(after is null ? "DELETED" : "present")} (was B={accountB}); tenant A={accountA}");
        Assert.NotNull(after);
        Assert.Equal(accountB, after.AccountId);
    }

    // The shape the interceptor is blind to: nothing about the principal
    // changes, only its owned Money, and the owned entry carries no AccountId.
    [Fact]
    public async Task OwnedOnlyModification_OnAttachedForeignStub_IsRefusedByTheDatabase()
    {
        var (accountA, accountB) = await TwoFarmsAsync();
        var rowId = await factory.WithTenantScopeAsync(accountB, async db =>
        {
            var item = NewItem(Guid.NewGuid(), accountB, 100);
            db.InventoryItems.Add(item);
            await db.SaveChangesAsync();
            return item.Id;
        });

        var stub = NewItem(rowId, accountA, 100);
        var states = "";

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(accountA, async db =>
        {
            db.InventoryItems.Attach(stub);
            var owned = db.Entry(stub).Reference(nameof(InventoryItem.DefaultUnitCost)).TargetEntry!;
            owned.Property(nameof(Money.MinorUnits)).CurrentValue = 999L;
            states = $"principal={db.Entry(stub).State} owned={owned.State}";
            await db.SaveChangesAsync();
        }));

        var after = await factory.WithTenantScopeAsync(accountA, db =>
            db.InventoryItems.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(i => i.Id == rowId));

        Assert.Equal("principal=Unchanged owned=Modified", states);
        Assert.True(thrown is DbUpdateConcurrencyException,
            $"owned-only write was not refused by the database: thrown={thrown?.GetType().Name ?? "none"}; {states}; " +
            $"row cost after={after?.DefaultUnitCost?.MinorUnits.ToString() ?? "ROW GONE"} (was 100, B={accountB}); tenant A={accountA}");
        Assert.NotNull(after);
        Assert.Equal(accountB, after.AccountId);
        Assert.Equal(100, after.DefaultUnitCost!.MinorUnits);
    }
}
```
Create `tests/Cluckwork.Api.IntegrationTests/AccountIdConcurrencyTokenModelTests.cs` with EXACTLY this content (**PROTECTED**):
```csharp
namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Inventory;
using Cluckwork.Infrastructure.Identity;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// #562 — pins the model walk at the end of AppDbContext.OnModelCreating that
// makes AccountId a concurrency token on every entity carrying one.
//
// Model-only, no database (ApplicationUserIndexModelTests precedent): the
// property in question is metadata, and the database-side behaviour it buys
// is proved separately by DetachedTenantWriteTests. Discovery, not a list —
// a new AccountId-bearing entity is covered the moment it is mapped, and
// this test fails if the walk stops covering it.
public sealed class AccountIdConcurrencyTokenModelTests
{
    private static AppDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only;Username=none;Password=none")
            .Options;
        return new AppDbContext(options, new TenantContext(), new FlockScope());
    }

    [Fact]
    public void EveryNonKeyAccountId_IsAConcurrencyToken()
    {
        using var db = BuildContext();

        var carriers = db.Model.GetEntityTypes()
            .Select(t => (Type: t, AccountId: t.FindProperty("AccountId")))
            .Where(x => x.AccountId is not null && x.AccountId.ClrType == typeof(Guid) && !x.AccountId.IsPrimaryKey())
            .ToList();

        // Proves the walk walked: a discovery that finds nothing passes vacuously.
        Assert.True(carriers.Count >= 29,
            $"Expected at least 29 AccountId-bearing entity types, found {carriers.Count}: " +
            string.Join(", ", carriers.Select(c => c.Type.ShortName())));
        Assert.Contains(carriers, c => c.Type.ClrType == typeof(DailyEntry));
        Assert.Contains(carriers, c => c.Type.ClrType == typeof(InventoryItem));
        Assert.Contains(carriers, c => c.Type.ClrType == typeof(ApplicationUser));
        Assert.Contains(carriers, c => c.Type.ClrType == typeof(RefreshToken));

        var notTokens = carriers
            .Where(c => !c.AccountId!.IsConcurrencyToken)
            .Select(c => c.Type.ShortName() + ".AccountId")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(notTokens.Count == 0,
            "AccountId must be a concurrency token on every entity that carries it (#562) — the database-side " +
            "tenant check is the WHERE clause it produces. Not a token on:\n  " + string.Join("\n  ", notTokens));
    }

    // The one deliberate exclusion: a primary-key AccountId is already in every
    // WHERE clause, and a token on a key column buys nothing. Pinned so that the
    // exclusion survives as a decision rather than an accident of the walk.
    [Fact]
    public void PrimaryKeyAccountId_OnSimulationSeedState_IsNotAToken()
    {
        using var db = BuildContext();

        var accountId = db.Model.FindEntityType(typeof(SimulationSeedState))!.FindProperty("AccountId")!;

        Assert.True(accountId.IsPrimaryKey());
        Assert.False(accountId.IsConcurrencyToken);
    }
}
```
Run G3, then G1, then **G2 narrowed** to both classes — one command, the filter joined with `|`:
```bash
dotnet test tests/Cluckwork.Api.IntegrationTests --configuration Release --no-build --filter "FullyQualifiedName~DetachedTenantWriteTests|FullyQualifiedName~AccountIdConcurrencyTokenModelTests" --logger "console;verbosity=normal"
```
It MUST fail exactly like this — **4 failed, 1 passed, 5 total**:

| Gate row + narrowing | Named test | Assertion | Stable discriminator | Generated fragments | What the fixture already seeds | Which other guard returns the same failure | Negative-test proof |
|---|---|---|---|---|---|---|---|
| G2 narrowed, above | `DetachedUpdate_StubWithForeignKeyAndOwnAccountId_IsRefusedByTheDatabase` | `Assert.True(thrown is DbUpdateConcurrencyException, …)` | `Update(stub) was not refused by the database: thrown=none; row AccountId after=<A>` | the three GUIDs | B's row with B's AccountId | none | the message prints the row's AccountId AFTER the write and it equals tenant A — the theft happened, the row was there |
| G2 narrowed | `DetachedRemove_StubWithForeignKeyAndOwnAccountId_IsRefusedByTheDatabase` | same shape | `Remove(stub) was not refused by the database: thrown=none; row after=DELETED` | GUIDs | B's row | none | `after=DELETED` proves the row existed and is gone |
| G2 narrowed | `OwnedOnlyModification_OnAttachedForeignStub_IsRefusedByTheDatabase` | same shape | `owned-only write was not refused by the database: thrown=none; principal=Unchanged owned=Modified; row cost after=999 (was 100` | GUIDs | B's item at cost 100 | none | `cost after=999` proves the write reached B's row |
| G2 narrowed | `AccountIdConcurrencyTokenModelTests.EveryNonKeyAccountId_IsAConcurrencyToken` | `Assert.True(notTokens.Count == 0, …)` | `Not a token on:` followed by **29** lines from `Account.AccountId` to `WaterUsage.AccountId` | none | n/a (model only) | none | n/a — positive test |
| G2 narrowed | `AccountIdConcurrencyTokenModelTests.PrimaryKeyAccountId_OnSimulationSeedState_IsNotAToken` | — | **PASSES already** — it pins an exclusion that holds before the walk exists; the narrowing mutation row M3 is what proves it is not vacuous. This pass is expected and is NOT a STOP. | | | | |

**If the count is not 4 failed / 1 passed, or any failure is at a different assertion, STOP and report.**

## 1b. GREEN — the model walk (PROTECTED)
Edit `src/Cluckwork.Infrastructure/Persistence/AppDbContext.cs`.

First, the using. Find this exact block:
```csharp
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Eggs;
```
Replace with:
```csharp
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
```
Then the walk. Find this exact block (the last lines of `OnModelCreating` and of the class):
```csharp
        user.Property(u => u.AccountId).Metadata
            .SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}
```
Replace with (**PROTECTED**):
```csharp
        user.Property(u => u.AccountId).Metadata
            .SetAfterSaveBehavior(PropertySaveBehavior.Throw);

        // #562 — AccountId is a CONCURRENCY TOKEN on every entity that carries
        // one. EF then puts AccountId's ORIGINAL value into the WHERE clause of
        // every UPDATE and DELETE it emits, beside the primary key (and beside
        // Version where the aggregate has one), so the database itself refuses
        // to touch a row whose AccountId is not the value the writer claimed.
        //
        // This is what closes the gap TenantStampInterceptor cannot close on its
        // own: the interceptor compares AccountId's original value against the
        // resolved tenant, but for an entity that reached SaveChanges DETACHED
        // (DbSet.Update / DbSet.Remove / Attach on a hand-built stub) that
        // original value is the caller's own, not the database's. The
        // interceptor still requires original == tenant, so with the token the
        // statement carries "AND AccountId = <tenant>" — a stub naming another
        // farm's row matches nothing and EF throws DbUpdateConcurrencyException.
        // Observed writing through before this walk existed, for Update, for
        // Remove, and for an owned-only edit with the principal Unchanged (which
        // the interceptor never even sees): DetachedTenantWriteTests.
        //
        // Discovered from the model, matched by NAME and CLR type exactly as the
        // interceptor matches (Entity<TId> or not — RefreshToken,
        // IdempotencyRecord and ApplicationUser are in scope). A primary-key
        // AccountId (SimulationSeedState) is excluded: the key is already in the
        // WHERE. AccountIdConcurrencyTokenModelTests pins the walk.
        //
        // The snapshot records the flag but EF emits no schema for it, so the
        // migration that accompanies this walk (AccountIdConcurrencyToken) is
        // deliberately empty — it exists to keep the snapshot equal to the model.
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            var accountId = entityType.FindProperty(nameof(Entity<Guid>.AccountId));
            if (accountId is null || accountId.ClrType != typeof(Guid)) continue;
            if (accountId.IsPrimaryKey()) continue;
            accountId.IsConcurrencyToken = true;
        }
    }
}
```

## 1c. Build and re-run
Run **G1**, then **G2 narrowed** exactly as in 1a. Expect `Passed: 5`.

## 1d. Commit Increment 1
```bash
git add src/Cluckwork.Infrastructure/Persistence/AppDbContext.cs tests/Cluckwork.Api.IntegrationTests/DetachedTenantWriteTests.cs tests/Cluckwork.Api.IntegrationTests/AccountIdConcurrencyTokenModelTests.cs
git commit -m "fix(tenancy): AccountId is a concurrency token on every entity that carries one (#562)"
```

===================================================================================
# INCREMENT 2 — the refusal is logged as a security event (INV-5)
===================================================================================

## 2a. RED — the vocabulary entry and the logging tests
Edit `src/Cluckwork.Application/Common/SecurityEvents.cs`. Find this exact block (the end of the class):
```csharp
    public const string ReportConcurrencyOverCapacity = "ReportConcurrency.OverCapacity";
}
```
Replace with:
```csharp
    public const string ReportConcurrencyOverCapacity = "ReportConcurrency.OverCapacity";

    // Fires when the database refuses an UPDATE or DELETE under a resolved
    // tenant because zero rows matched. With AccountId a concurrency token
    // (#562) that is what a write aimed at another farm's row looks like from
    // inside the process — and it is also what an ordinary Version race looks
    // like. The interceptor cannot tell them apart without a second round trip
    // and does not try, so a deployment backend should alert on a RUN of these
    // for one tenant, never on a single one.
    public const string TenantWriteRefusedByDatabase = "Tenant.WriteRefusedByDatabase";
}
```
Check before you create:
```bash
git ls-files tests/Cluckwork.Api.IntegrationTests/TenantWriteRefusalLoggingTests.cs   # expect: empty
```
Create `tests/Cluckwork.Api.IntegrationTests/TenantWriteRefusalLoggingTests.cs` with EXACTLY this content (**PROTECTED**):
```csharp
namespace Cluckwork.Api.IntegrationTests;

using Cluckwork.Api.IntegrationTests.Infrastructure;
using Cluckwork.Application.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;

// #562 — the database-side refusal is logged as a security event.
//
// With AccountId a concurrency token, a write aimed at another farm's row
// fails as DbUpdateConcurrencyException — the same exception an ordinary
// Version race produces, and one that at least seven call sites already
// catch and retry. TenantStampInterceptor.SaveChangesFailed therefore logs
// every concurrency failure that happens under a RESOLVED tenant as
// Tenant.WriteRefusedByDatabase, naming the entity, its key and the tenant,
// and lets the exception propagate untouched. Owner decision 2026-09-02: a
// run of these for one tenant is the signal; a lone one is usually a race.
//
// Same CollectingSink tap as SecurityEventLoggingTests; events are selected
// by the fresh tenant id each test mints, never by clearing the shared sink.
[Collection(SecurityEventLoggingCollection.Name)]
public sealed class TenantWriteRefusalLoggingTests(SecurityEventLoggingFactory factory)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    private static DailyEntry NewEntry(Guid id, Guid accountId, Guid houseId, Guid flockId) =>
        NewEntry(id, accountId, Guid.NewGuid(), houseId, flockId);

    private static DailyEntry NewEntry(Guid id, Guid accountId, Guid farmId, Guid houseId, Guid flockId) =>
        DailyEntry.Create(id, accountId, farmId, houseId, flockId, Today);

    private static string? ScalarOf(LogEvent e, string name) =>
        e.Properties.TryGetValue(name, out var v) && v is ScalarValue s ? s.Value?.ToString() : null;

    private IReadOnlyList<LogEvent> RefusalsFor(Guid tenant) =>
        [.. factory.Sink.Events.Where(e =>
            ScalarOf(e, "SecurityEvent") == SecurityEvents.TenantWriteRefusedByDatabase
            && ScalarOf(e, "TenantAccountId") == tenant.ToString())];

    // Keyed on the ROW, not the tenant: an unresolved context has no tenant to
    // name, so a refusal logged from one would carry Guid.Empty and a tenant
    // filter would never see it — which is exactly the leak this selector
    // must catch. Every test mints a fresh row id, so the set is otherwise empty.
    private IReadOnlyList<LogEvent> RefusalsForRow(Guid rowId) =>
        [.. factory.Sink.Events.Where(e =>
            ScalarOf(e, "SecurityEvent") == SecurityEvents.TenantWriteRefusedByDatabase
            && ScalarOf(e, "KeyValues") == rowId.ToString())];

    private static async Task<Exception?> CaptureAsync(Func<Task> write)
    {
        try { await write(); return null; }
        catch (Exception e) { return e; }
    }

    [Fact]
    public async Task DetachedStubRefusedByTheDatabase_LogsOneSecurityEvent_NamingEntityKeyAndTenant()
    {
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var accountB = await factory.SeedAccountWithUserAsync($"b-{Guid.NewGuid():N}@test.local");
        var rowId = await factory.WithTenantScopeAsync(accountB, async db =>
        {
            var entry = NewEntry(Guid.NewGuid(), accountB, Guid.NewGuid(), Guid.NewGuid());
            db.DailyEntries.Add(entry);
            await db.SaveChangesAsync();
            return entry.Id;
        });

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(accountA, async db =>
        {
            db.DailyEntries.Update(NewEntry(rowId, accountA, Guid.NewGuid(), Guid.NewGuid()));
            await db.SaveChangesAsync();
        }));

        Assert.True(thrown is DbUpdateConcurrencyException,
            $"stub write was not refused by the database: thrown={thrown?.GetType().Name ?? "none"}");

        var refusal = Assert.Single(RefusalsFor(accountA));
        Assert.Equal(LogEventLevel.Warning, refusal.Level);
        Assert.Equal("DailyEntry", ScalarOf(refusal, "EntityType"));
        Assert.Equal(rowId.ToString(), ScalarOf(refusal, "KeyValues"));
        Assert.Equal(accountA.ToString(), ScalarOf(refusal, "TenantAccountId"));
    }

    // The unresolved path (CLI verbs, the design-time factory, the seeders'
    // pre-checks) has no tenant to name and is not a request: a concurrency
    // failure there is not a security event.
    [Fact]
    public async Task ConcurrencyFailure_UnderAnUnresolvedTenant_LogsNothing()
    {
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var rowId = await factory.WithTenantScopeAsync(accountA, async db =>
        {
            var entry = NewEntry(Guid.NewGuid(), accountA, Guid.NewGuid(), Guid.NewGuid());
            db.DailyEntries.Add(entry);
            await db.SaveChangesAsync();
            // Bump Version to 1 so a stub built at Version 0 misses the row.
            Assert.True(entry.RecordProduction(100, 1, 1, 0, 0).IsSuccess);
            await db.SaveChangesAsync();
            return entry.Id;
        });

        Exception? thrown;
        using (var scope = factory.Services.CreateScope())
        {
            Assert.False(scope.ServiceProvider.GetRequiredService<TenantContext>().IsResolved);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            thrown = await CaptureAsync(async () =>
            {
                db.DailyEntries.Update(NewEntry(rowId, accountA, Guid.NewGuid(), Guid.NewGuid()));
                await db.SaveChangesAsync();
            });
        }

        Assert.IsType<DbUpdateConcurrencyException>(thrown);
        Assert.Empty(RefusalsForRow(rowId));
    }

    // A failed save that is NOT a concurrency failure — here a duplicate of the
    // live farm/house/flock/day natural key (IX_DailyEntries_NaturalKey, a
    // DbUpdateException carrying 23505) — is not the database refusing a
    // tenant; it must not wear this event.
    [Fact]
    public async Task NonConcurrencyDbUpdateException_LogsNothing()
    {
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var farmId = Guid.NewGuid();
        var houseId = Guid.NewGuid();
        var flockId = Guid.NewGuid();
        await factory.WithTenantScopeAsync(accountA, async db =>
        {
            db.DailyEntries.Add(NewEntry(Guid.NewGuid(), accountA, farmId, houseId, flockId));
            await db.SaveChangesAsync();
        });

        var before = RefusalsFor(accountA).Count;

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(accountA, async db =>
        {
            db.DailyEntries.Add(NewEntry(Guid.NewGuid(), accountA, farmId, houseId, flockId));
            await db.SaveChangesAsync();
        }));

        Assert.NotNull(thrown);
        Assert.IsNotType<DbUpdateConcurrencyException>(thrown);
        Assert.IsAssignableFrom<DbUpdateException>(thrown);
        Assert.Equal(before, RefusalsFor(accountA).Count);
    }

    [Fact]
    public async Task SuccessfulTrackedWrite_LogsNothing()
    {
        var accountA = await factory.SeedAccountWithUserAsync($"a-{Guid.NewGuid():N}@test.local");
        var before = RefusalsFor(accountA).Count;

        await factory.WithTenantScopeAsync(accountA, async db =>
        {
            var entry = NewEntry(Guid.NewGuid(), accountA, Guid.NewGuid(), Guid.NewGuid());
            db.DailyEntries.Add(entry);
            await db.SaveChangesAsync();
            Assert.True(entry.RecordProduction(100, 1, 1, 0, 0).IsSuccess);
            await db.SaveChangesAsync();
        });

        Assert.Equal(before, RefusalsFor(accountA).Count);
    }
}
```
Run **G1**, then **G2 narrowed** to `TenantWriteRefusalLoggingTests`. It MUST fail exactly like this — **1 failed, 3 passed, 4 total**:

| Gate row + narrowing | Named test | Assertion | Stable discriminator | Generated fragments | What the fixture already seeds | Which other guard returns the same failure | Negative-test proof |
|---|---|---|---|---|---|---|---|
| G2 narrowed to `TenantWriteRefusalLoggingTests` | `DetachedStubRefusedByTheDatabase_LogsOneSecurityEvent_NamingEntityKeyAndTenant` | `Assert.Single(RefusalsFor(accountA))` | `Assert.Single() Failure: The collection was empty` | none | the stub write IS refused (Increment 1) — the first assertion passes; only the event is missing | none | n/a — positive test |
| G2 narrowed | the other three (`…UnresolvedTenant_LogsNothing`, `NonConcurrencyDbUpdateException_LogsNothing`, `SuccessfulTrackedWrite_LogsNothing`) | — | **PASS already** — negative controls that hold before the hook exists; mutation rows M5 and M4 are what prove they are not vacuous. Expected, not a STOP. | | | | |

## 2b. GREEN — the interceptor (PROTECTED, whole file)
Replace the ENTIRE contents of `src/Cluckwork.Infrastructure/Persistence/Interceptors/TenantStampInterceptor.cs` with EXACTLY this:
```csharp
namespace Cluckwork.Infrastructure.Persistence.Interceptors;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// Stamps AccountId on every newly inserted entity so writes can't be mis-tagged
// even if a handler forgets to pass it (tech spec §4.2, point 3), AND refuses
// any tracked write whose AccountId is not the resolved tenant's (#546).
//
// Before #546 this only FILLED an empty AccountId on Added entities: an
// explicitly WRONG non-empty value was written without complaint, and Modified
// and Deleted were never inspected at all. Reads have 27 fail-closed query
// filters; writes had convention. This is the write side's chokepoint.
//
// Matching is by property NAME rather than by base type, and that is
// load-bearing: RefreshToken, IdempotencyRecord and SimulationSeedState all
// carry AccountId WITHOUT inheriting Entity<TId>, so a type-based test would
// silently drop them out of scope — including RefreshToken, which is exactly
// the cross-tenant write /auth/login can attempt once #532 lands.
//
// An unresolved tenant disables checking entirely, deliberately: the CLI
// verbs, the seeders' pre-checks and AppDbContextDesignTimeFactory all run
// that way by design (the seeders themselves resolve the tenant before they
// write).
//
// This interceptor is ONE of two layers (#562). It can only judge the values
// the change tracker holds, and for an entity that reached SaveChanges
// detached — DbSet.Update, DbSet.Remove, Attach on a hand-built stub — those
// values are the caller's, not the database's. The second layer is the
// database: AccountId is a concurrency token on every entity that carries one
// (AppDbContext.OnModelCreating), so the UPDATE/DELETE the database runs
// carries "AND AccountId = <original>" and a stub naming another farm's row
// matches nothing. ThrowingConcurrencyException below is where that refusal
// is heard.
//
// The logger is optional because AppDbContextDesignTimeFactory and the
// migration tests construct this by hand with no logging in reach; the
// serving process gets it from DI.
public sealed class TenantStampInterceptor(
    TenantContext tenant,
    ILogger<TenantStampInterceptor>? logger = null) : SaveChangesInterceptor
{
    private readonly ILogger<TenantStampInterceptor> logger =
        logger ?? NullLogger<TenantStampInterceptor>.Instance;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken ct = default)
    {
        StampTenant(eventData.Context);
        return base.SavingChangesAsync(eventData, result, ct);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        StampTenant(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    // #562 — the database-side refusal, heard here. With AccountId a
    // concurrency token, a write whose row belongs to another farm fails as
    // DbUpdateConcurrencyException: zero rows matched the AccountId conjunct.
    // From inside the process that is indistinguishable from an ordinary
    // Version race, and telling them apart would take a second round trip on
    // the failure path — so this does not try. It logs every concurrency
    // failure that happens under a RESOLVED tenant as a security event naming
    // the entity, its key and the tenant, and returns the result UNCHANGED so
    // EF throws exactly as before (Program.cs maps it to 409). A run of these
    // for one tenant on rows it does not own is the signal the event exists
    // for; a lone one is usually a race. Owner decision, 2026-09-02.
    //
    // This is the ThrowingConcurrencyException hook, not SaveChangesFailed:
    // EF routes a concurrency conflict through this dedicated interception
    // point and never reaches SaveChangesFailed for it, which the preflight
    // of this change observed. TenantWriteRefusalLoggingTests pins the hook.
    public override InterceptionResult ThrowingConcurrencyException(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result)
    {
        LogDatabaseRefusal(eventData);
        return base.ThrowingConcurrencyException(eventData, result);
    }

    public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result,
        CancellationToken ct = default)
    {
        LogDatabaseRefusal(eventData);
        return base.ThrowingConcurrencyExceptionAsync(eventData, result, ct);
    }

    private void StampTenant(DbContext? context)
    {
        if (context is null || !tenant.IsResolved) return;

        // Only the three states EF actually emits SQL for. Unchanged and
        // Detached write nothing, so inspecting them would reject writes that
        // never happen.
        foreach (var entry in context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            // Any entity carrying an AccountId property, Entity<TId> or not.
            var prop = entry.Properties
                .FirstOrDefault(p => p.Metadata.Name == nameof(Entity<Guid>.AccountId));
            if (prop is null) continue;

            switch (entry.State)
            {
                case EntityState.Added:
                    StampOrVerifyAdded(entry, prop);
                    break;

                // BOTH the value being written and the value the row was loaded
                // with must be the tenant's. Checking only the current value
                // would let a row loaded under IgnoreQueryFilters be RELABELLED
                // into the current tenant and pass — theft, not a leak.
                //
                // OriginalValue is the database's only for an entity that was
                // LOADED while tracked; for a detached stub it is the caller's
                // own, and this check passes it. That is the case the concurrency
                // token closes (#562): the statement then carries
                // "AND AccountId = <original>", and because this check has
                // already required original == tenant, a row that is not the
                // tenant's matches nothing. DetachedTenantWriteTests pins it, and
                // TrackedMutationReadTests keeps the tracked-read precondition as
                // defence in depth.
                case EntityState.Modified:
                    Verify(entry, prop.OriginalValue);
                    Verify(entry, prop.CurrentValue);
                    break;

                // A delete writes no new value, so the loaded one is all there
                // is to check.
                case EntityState.Deleted:
                    Verify(entry, prop.OriginalValue);
                    break;
            }
        }
    }

    private void StampOrVerifyAdded(EntityEntry entry, PropertyEntry prop)
    {
        if (prop.CurrentValue is not Guid accountId) return;

        if (accountId == Guid.Empty)
        {
            prop.CurrentValue = tenant.AccountId;
            return;
        }

        if (accountId != tenant.AccountId)
            throw new TenantWriteMismatchException(
                entry.Metadata.ClrType.Name, nameof(EntityState.Added), tenant.AccountId, accountId);
    }

    private void Verify(EntityEntry entry, object? value)
    {
        if (value is not Guid accountId) return;

        if (accountId != tenant.AccountId)
            throw new TenantWriteMismatchException(
                entry.Metadata.ClrType.Name, entry.State.ToString(), tenant.AccountId, accountId);
    }

    private void LogDatabaseRefusal(ConcurrencyExceptionEventData eventData)
    {
        if (!tenant.IsResolved) return;

        foreach (var entry in eventData.Exception.Entries)
        {
            // An owned entry (a Money on a table-split aggregate) shares its
            // owner's key, so the key logged is the row's either way.
            var key = entry.Metadata.FindPrimaryKey();
            var keyValues = key is null
                ? "?"
                : string.Join(",", key.Properties.Select(p => entry.Property(p.Name).CurrentValue));

            logger.LogWarning(
                "{SecurityEvent} entity={EntityType} key={KeyValues} tenant={TenantAccountId}",
                SecurityEvents.TenantWriteRefusedByDatabase,
                entry.Metadata.DisplayName(),
                keyValues,
                tenant.AccountId);
        }
    }
}
```

## 2c. Build and re-run
Run **G1**, then **G2 narrowed** to `TenantWriteRefusalLoggingTests|DetachedTenantWriteTests|AccountIdConcurrencyTokenModelTests|TenantWriteGuardTests|TrackedMutationReadTests` (join with `|` as in 1a). Expect `Passed: 20`, `Failed: 0` (4 + 3 + 2 + 9 + 2).

## 2d. Commit Increment 2
```bash
git add src/Cluckwork.Application/Common/SecurityEvents.cs src/Cluckwork.Infrastructure/Persistence/Interceptors/TenantStampInterceptor.cs tests/Cluckwork.Api.IntegrationTests/TenantWriteRefusalLoggingTests.cs
git commit -m "fix(tenancy): log a database-side tenant write refusal as Tenant.WriteRefusedByDatabase (#562)"
```

===================================================================================
# INCREMENT 3 — generated artifacts: the empty migration and the snapshot
===================================================================================

No red phase. The concurrency token is model metadata, not schema: EF emits no DDL for it, but the
model snapshot records it, and the repo's rule is one migration per model change (#407). The migration
exists to keep `AppDbContextModelSnapshot.cs` equal to the model; its `Up` and `Down` MUST be empty.

**Shared fixture state at capture:** no shared state — the generator reads the model, not a database.

```bash
CLUCKWORK_MIGRATIONS_CONNECTION='Host=localhost;Port=5432;Database=cluckwork;Username=x;Password=x' \
CLUCKWORK_MIGRATIONS_ALLOW_INSECURE_LOOPBACK=true \
  dotnet ef migrations add AccountIdConcurrencyToken \
  -p src/Cluckwork.Infrastructure -s src/Cluckwork.Api
```
(The design-time connection is fail-closed (#318) and must be SET, but `migrations add` never connects —
the placeholder credentials are deliberate and never reach a database.)

Then verify all four, and STOP on any mismatch:
```bash
git status --porcelain src/Cluckwork.Infrastructure/Persistence/Migrations/
# expect exactly these three lines, in the order git prints them (tracked change first, then untracked, Designer before .cs):
#   " M …/AppDbContextModelSnapshot.cs"
#   "?? …/<ts>_AccountIdConcurrencyToken.Designer.cs"
#   "?? …/<ts>_AccountIdConcurrencyToken.cs"
cat src/Cluckwork.Infrastructure/Persistence/Migrations/*_AccountIdConcurrencyToken.cs
# expect: Up(...) { } and Down(...) { } both EMPTY bodies. A non-empty body is a STOP.
git diff --stat src/Cluckwork.Infrastructure/Persistence/Migrations/AppDbContextModelSnapshot.cs
# expect: "1 file changed, 29 insertions(+)" — every added line is ".IsConcurrencyToken()"
CLUCKWORK_MIGRATIONS_CONNECTION='Host=localhost;Port=5432;Database=cluckwork;Username=x;Password=x' CLUCKWORK_MIGRATIONS_ALLOW_INSECURE_LOOPBACK=true dotnet ef migrations has-pending-model-changes -p src/Cluckwork.Infrastructure -s src/Cluckwork.Api
# expect the last line: "No changes have been made to the model since the last migration."
```
Then run **G1** and **G4** (the migration runs inside G4's ephemeral Postgres; expect `docs/schema/ is up to date.` — a STALE report is a STOP, do not commit a regenerated docs/schema).

```bash
git add src/Cluckwork.Infrastructure/Persistence/Migrations/
git commit -m "chore(migrations): AccountIdConcurrencyToken — empty migration keeping the snapshot equal to the model (#562)"
```

===================================================================================
# INCREMENT 4 — docs and comments that named #562 as open, plus this runbook
===================================================================================

No red phase. Four edits and one `git add` of this file.

### 4a. `AGENTS.md` — the multi-tenancy bullet names the token
Find this exact text (inside the `**Multi-tenancy:**` bullet):
```
Several farms now coexist on one deployment: sign-in takes a farm code, and one email address can belong to a user in more than one farm. → [`530-multi-farm-tenancy.md`](docs/decisions/530-multi-farm-tenancy.md)
```
Replace with:
```
Several farms now coexist on one deployment: sign-in takes a farm code, and one email address can belong to a user in more than one farm. **`AccountId` is also an EF concurrency token on every entity that carries one (#562):** the `UPDATE`/`DELETE` the database runs carries `AccountId = <original>`, so a detached stub aimed at another farm's row matches nothing — the interceptor alone cannot see a detached write, and an owned-only edit on an attached stub was writing through until the token landed. The token comes from a model walk in `AppDbContext.OnModelCreating`, so a new entity is covered automatically; never remove it, and a database refusal under a resolved tenant is logged as `Tenant.WriteRefusedByDatabase`. → [`530-multi-farm-tenancy.md`](docs/decisions/530-multi-farm-tenancy.md)
```

### 4b. `docs/decisions/530-multi-farm-tenancy.md` §5 — the gap and its closure
Find this exact block:
```
**What this does NOT cover.** The guard reads EF's `OriginalValue` as DB provenance; a
detached `Update`/`Remove` can still bypass the theft check. That gap is tracked
separately in #562 and is **not** closed by this epic.
```
Replace with:
```
**What it did not cover, and what closed it (#562, 2026-09-02).** The guard reads EF's
`OriginalValue` as DB provenance, and that is only true for an entity that was *loaded* while
tracked: `DbSet.Update`, `DbSet.Remove` and `Attach` seed the original values from the
caller's own instance. Reproduced on a real Postgres in three shapes — a stub with another
farm's primary key and this farm's `AccountId` handed to `Update` relabelled the row (theft),
to `Remove` deleted it, and an `Attach` as `Unchanged` followed by an edit of only the owned
`Money` rewrote the row's cost with the interceptor never seeing an entry it could judge. The
third shape was **live**, not latent. Closed at the database rather than in C#: `AccountId` is
an EF **concurrency token** on every entity that carries one, set by a model walk at the end of
`AppDbContext.OnModelCreating`, so the statement the database runs carries
`AND AccountId = <original>`; the interceptor already requires original == tenant, so a row
that is not the tenant's matches nothing and EF throws `DbUpdateConcurrencyException`. No
schema changes (the accompanying `AccountIdConcurrencyToken` migration is deliberately empty and
exists to keep the snapshot equal to the model). The refusal is indistinguishable from a
`Version` race inside the process and is logged under a resolved tenant as
`Tenant.WriteRefusedByDatabase` (owner decision: a run of them is the signal, a lone one is a
race). Pinned by `DetachedTenantWriteTests`, `AccountIdConcurrencyTokenModelTests` and
`TenantWriteRefusalLoggingTests`. **Still outside both layers:** entity types with no
`AccountId` property — Identity's own six tables, of which `AspNetUserRoles` is live RBAC state
(#670) — and every `ExecuteUpdate`/`ExecuteDelete`/raw-SQL path, which #536's guard governs.
```

### 4c. `docs/security/log-redaction-policy.md` — the new event
Find this exact line (the last row of the "Operational (non-credential) security events" table):
```
| `ReportConcurrency.OverCapacity` | A running report's lease lapsed (a reachable backend rejected the renewal) and no free slot was available to re-count it — the account is over its per-instance report-concurrency ceiling with this report on top (#545). Bounded and self-healing as reports finish; a persistent rate means the shared store is dropping slots under load or during outage recovery. | `capability`. |
```
Directly AFTER it (keep that line), insert:
```

### Tenant-isolation events

One event, from the write side's second layer (#562): `AccountId` is an EF concurrency token
on every entity that carries one, so an `UPDATE`/`DELETE` aimed at a row another farm owns
matches zero rows and fails as `DbUpdateConcurrencyException`. Inside the process that is
indistinguishable from an ordinary optimistic-concurrency race, and the interceptor does not
spend a second round trip telling them apart — so a **single** event is usually a race, and a
**run** of them for one tenant is the signal. Carries the tenant's account id (never a user id,
email or row contents) and the row's key values.

| Event ID | Fires when | Fields |
|---|---|---|
| `Tenant.WriteRefusedByDatabase` | `SaveChanges` under a resolved tenant fails with `DbUpdateConcurrencyException`, once per failed entry. The `AccountId` conjunct (or `Version`) matched no row. | `EntityType` (EF display name), `KeyValues` (comma-joined primary key), `TenantAccountId`. |
```
Then find this exact block (in the "What the deployment/ops repo must provide" list):
```
  (`SharedState.RedisUnavailable`, `ReportConcurrency.OverCapacity`) for a
  degraded shared-state dependency or a breached per-account capacity ceiling.
```
Directly AFTER it (keep those lines), insert:
```
- **Alert on a run of `Tenant.WriteRefusedByDatabase` for one `TenantAccountId`** — a
  lone event is an optimistic-concurrency race; a burst on rows the tenant does not own is a
  write-side isolation probe the database refused (#562).
```

### 4d. `tests/Cluckwork.Api.IntegrationTests/TrackedMutationReadTests.cs` — precondition → defence in depth
Three edits, comments and one message only. Find:
```csharp
// Every repository mutation read is a TRACKED read behind the tenant query
// filter, which is what makes the snapshot trustworthy. That is a PRECONDITION
// of the guard, not an incidental detail, so it gets a test: flipping one of
// these reads to AsNoTracking is exactly the change that would void the theft
// protection, and it must fail here rather than pass quietly.
```
Replace with:
```csharp
// Every repository mutation read is a TRACKED read behind the tenant query
// filter, which is what makes the snapshot trustworthy. Until #562 that was
// the whole guarantee; since #562 AccountId is a concurrency token and the
// DATABASE refuses a detached stub's write (DetachedTenantWriteTests), so the
// tracked read is now defence in depth — the layer that keeps the
// interceptor's own check meaningful and a detached write from ever being
// attempted. Flipping one of these reads to AsNoTracking must still fail
// here rather than pass quietly.
```
Find:
```csharp
// Deliberately NOT a test that a detached write succeeds. That behaviour is a
// known gap tracked in #562; asserting it would turn "not yet fixed" into
// "specified".
```
Replace with:
```csharp
// The detached-write behaviour itself is asserted in DetachedTenantWriteTests
// (refused, since #562), not here.
```
Find:
```csharp
            "tenant, and a detached entity carries caller-seeded originals — so this voids the " +
            "cross-tenant theft check (see #562):\n  " + string.Join("\n  ", violations));
```
Replace with:
```csharp
            "tenant, and a detached entity carries caller-seeded originals — the database's AccountId " +
            "token (#562) still refuses the row, but this layer is what keeps that from being reached:\n  " +
            string.Join("\n  ", violations));
```

### 4e. Verify and commit
Run **G1**, then **G2 in full, in the FOREGROUND** (it takes ~3 minutes; the four suites print
`Test Run Successful.` blocks — report all four `Total tests` / `Passed` lines verbatim; IntegrationTests
must read `Total tests: 1654`, the others 365 / 234 / 10). `TenancyDocsFreshnessTests` (in
Application.Tests) is the guard on 4b's wording.

```bash
git add AGENTS.md docs/decisions/530-multi-farm-tenancy.md docs/security/log-redaction-policy.md tests/Cluckwork.Api.IntegrationTests/TrackedMutationReadTests.cs docs/plans/562-tenant-write-token/01-implementer-runbook.md
git commit -m "docs(tenancy): record the #562 closure, the token convention and the new security event"
```

===================================================================================
# MUTATION CHECKS — prove the guards bite
===================================================================================

For each row: apply the mutation → run **G1** → run the NAMED test (G2 narrowed to its class) → record
the observed failure → restore the original text → run **G1 again** → re-run the same narrowed test and
confirm green. **A reused build reports the previous mutant** — G1 between every step, no `--no-build`
shortcuts beyond the one inside G2's narrowed form, which always follows a fresh G1.

Mark every mutant in place with `// MUTANT M<n>: <what this breaks>` and delete the marker on restore.
At the end, `grep -rn MUTANT src tests` MUST return nothing.

Closed set for the walk (M2/M3): the INPUT is "every property named `AccountId` of CLR type `Guid` on a
mapped entity type", partitioned by one predicate — is it part of the primary key. Two members:
**non-key** (included; 29 today) and **key** (excluded; `SimulationSeedState`). One row narrows the
included side, one widens onto the excluded side. Everything else below is not a closed-set guard.

| ID | Member / side | Mutation (exact edit) | Members this edit also moves | Named test that must go RED | Expected mechanism + failure | Observed (you fill) | Rebuild run (you fill) |
|---|---|---|---|---|---|---|---|
| M1 | — (the whole walk) | In `AppDbContext.cs`, delete the line `            accountId.IsConcurrencyToken = true;` | all 29 | `DetachedTenantWriteTests.DetachedUpdate_StubWithForeignKeyAndOwnAccountId_IsRefusedByTheDatabase` | no token → the UPDATE keys on Id alone → the stub writes through → `Update(stub) was not refused by the database: thrown=none; row AccountId after=<A>`. **Also expected red**: the other two `DetachedTenantWriteTests`, `EveryNonKeyAccountId_IsAConcurrencyToken` (29 not tokens), and `DetachedStubRefusedByTheDatabase_LogsOneSecurityEvent…` (`stub write was not refused … thrown=none`). Five reds, in three classes. | | |
| M2 | non-key / narrow | In the walk, insert as the FIRST line inside the `foreach` body: `            if (entityType.ClrType == typeof(Customer)) continue; // MUTANT M2` | none — one member | `AccountIdConcurrencyTokenModelTests.EveryNonKeyAccountId_IsAConcurrencyToken` | `Customer.AccountId` is skipped → the offender list has exactly one line: `Not a token on:` / `  Customer.AccountId` | | |
| M3 | key / widen | In the walk, delete the line `            if (accountId.IsPrimaryKey()) continue;` | none — `SimulationSeedState` is the only key-side member | `AccountIdConcurrencyTokenModelTests.PrimaryKeyAccountId_OnSimulationSeedState_IsNotAToken` | the walk now flags the PK → `Assert.False() Failure` on `IsConcurrencyToken` | | |
| M4 | — | In `TenantStampInterceptor.LogDatabaseRefusal`, REPLACE the whole six-line `logger.LogWarning(` statement (from `logger.LogWarning(` through `tenant.AccountId);`) with the single line `            _ = keyValues; // MUTANT M4: nothing logged` — a plain deletion leaves `keyValues` unused, which is CS0219 and a build FAILURE under this repo's warnings-as-errors, not a red test | n/a | `TenantWriteRefusalLoggingTests.DetachedStubRefusedByTheDatabase_LogsOneSecurityEvent_NamingEntityKeyAndTenant` | the hook runs but emits nothing → `Assert.Single() Failure: The collection was empty` | | |
| M5 | — | In `TenantStampInterceptor.LogDatabaseRefusal`, delete the line `        if (!tenant.IsResolved) return;` | n/a | `TenantWriteRefusalLoggingTests.ConcurrencyFailure_UnderAnUnresolvedTenant_LogsNothing` | an unresolved-context race now logs (with `TenantAccountId` = `00000000-…`) and the test selects by the ROW's key, so it sees it → `Assert.Empty() Failure: Collection was not empty`. | | |
| M6 | — | In `TenantStampInterceptor.ThrowingConcurrencyExceptionAsync`, delete the line `        LogDatabaseRefusal(eventData);` | n/a | `TenantWriteRefusalLoggingTests.DetachedStubRefusedByTheDatabase_LogsOneSecurityEvent_NamingEntityKeyAndTenant` | the async hook is the one EF reaches for `SaveChangesAsync`; without the call nothing logs → `Assert.Single() Failure: The collection was empty`. Proves the hook is reached (the first draft of this change used `SaveChangesFailed` and was never reached). | | |
| M7 | — | In `TenantStampInterceptor.ThrowingConcurrencyException` (the SYNC override), delete the line `        LogDatabaseRefusal(eventData);` | n/a | same test as M6 | **expected GREEN**: no production or test code calls the synchronous `SaveChanges()` (`grep -rn "\.SaveChanges()" src` → 0 hits). The sync override is the mirror EF's guidance asks for, not a guarded path. Report GREEN; if RED, report that too — it would mean a sync caller exists. | | |

Report the result of every row, including any that did NOT apply cleanly.

===================================================================================
# FINISH — push + PR
===================================================================================
**First** write `docs/plans/562-tenant-write-token/PR-BODY.md` (NOT committed — delete it after the PR
exists) with this content, substituting the mutation results you observed and the suite lines:

```
Closes #562. Part of epic #530 (T8e).

## What

`AccountId` is now an EF **concurrency token** on every entity that carries one — 29 entity types, discovered by a model walk at the end of `AppDbContext.OnModelCreating`, excluding the one primary-key `AccountId` (`SimulationSeedState`). Every `UPDATE`/`DELETE` the database runs therefore carries `AND "AccountId" = @original`; the interceptor already requires that original to be the resolved tenant's, so a row that is not the tenant's matches nothing and EF throws `DbUpdateConcurrencyException`.

That refusal is logged under a resolved tenant as the new security event `Tenant.WriteRefusedByDatabase` (entity, key, tenant) from the interceptor's `ThrowingConcurrencyException` hook, and the exception propagates unchanged (409 via the global handler).

## Why — reproduced, not inferred

Serving farm A, a hand-built stub carrying farm B's row id and A's `AccountId`, never loaded, on the unmodified tree:

- `Update(stub)` → no exception, **B's row relabelled to A** (theft);
- `Remove(stub)` → no exception, **B's row deleted**;
- `Attach(stub)` as `Unchanged` + edit only the owned `Money` → no exception, **B's row's cost rewritten** — the interceptor never sees an entry it can judge (principal `Unchanged`, owned entry has no `AccountId`). This one was live, not latent, and was found by the seam's falsifying review.

All three are refused with the token in place, rows intact — `DetachedTenantWriteTests`.

## What did not change

- The interceptor's tracked-path checks (`TenantWriteGuardTests`, 9 tests, untouched).
- Every repository mutation read is still tracked (`TrackedMutationReadTests`) — now defence in depth rather than the guarantee.
- No schema: the `AccountIdConcurrencyToken` migration is deliberately empty and exists to keep the snapshot equal to the model (`has-pending-model-changes` → none; `docs/schema` unchanged and `generate.sh --check` green).
- No user-visible behaviour: no GLOSSARY or Help change. A stub-based cross-tenant write now surfaces as 409 instead of succeeding.

## Still outside both layers (recorded, not hidden)

- Entity types with no `AccountId` property — Identity's own six tables; `AspNetUserRoles` is live RBAC state → **#670**.
- `ExecuteUpdate` / `ExecuteDelete` / raw SQL — governed by #536's Roslyn guard.
- The stale "#176 xmin" comment at `IdentityProvider.cs:1762` (its token is `ConcurrencyStamp`) — noted, not touched here.

## Mutation checks (implementer-run; the driver re-runs every row before the merge ask)

| Row | Named test | Expected | Observed |
|---|---|---|---|
| M1 delete the token assignment | DetachedUpdate… (+4 more) | RED, `thrown=none` | <fill> |
| M2 skip Customer in the walk | EveryNonKeyAccountId… | RED, `Customer.AccountId` | <fill> |
| M3 drop the primary-key exclusion | PrimaryKeyAccountId_OnSimulationSeedState… | RED | <fill> |
| M4 replace the LogWarning with a no-op | DetachedStubRefused…LogsOneSecurityEvent | RED, collection empty | <fill> |
| M5 delete the IsResolved gate | ConcurrencyFailure_UnderAnUnresolvedTenant… | RED, collection not empty | <fill> |
| M6 delete the async hook's call | DetachedStubRefused…LogsOneSecurityEvent | RED, collection empty | <fill> |
| M7 delete the sync hook's call | same | GREEN (no sync caller) | <fill> |

## Suite

<paste the four `Total tests` / `Passed` lines from the foreground G2 run>
```

**Then**, and only then, run the fence — the `gh pr create` line reads the file you just wrote:
```bash
grep -rn "MUTANT\|DEBUG-562" src tests   # expect: nothing
git status --porcelain                    # expect: empty
git log --oneline main..HEAD              # expect: exactly 4 commits, in the order above
git push -u origin fix/562-account-id-concurrency-token
gh pr create --title "fix(tenancy): AccountId is a concurrency token, so the database refuses a detached cross-tenant write (#562)" --body-file docs/plans/562-tenant-write-token/PR-BODY.md
```

After the PR exists:
```bash
rm docs/plans/562-tenant-write-token/PR-BODY.md
git status --porcelain   # expect: empty
```

Open the PR **now**, before any review is requested. Not a draft: the work is complete.

## Report back
Branch name, PR number and URL, the exact G1 output tail, the four `Total tests`/`Passed` lines from the
foreground G2 run, the result of every mutation row (M1–M7) as observed, and — per increment — confirm
that **you** applied its code blocks from this runbook, naming any block that was already present or
that you did not apply yourself. If any step could not be completed as written, say which and stop.
