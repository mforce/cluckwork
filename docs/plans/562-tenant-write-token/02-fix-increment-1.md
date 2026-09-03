# Fix increment 1 — #562 / PR #671, round-1 findings

You are an autonomous coding agent with FULL tools in the `cluckwork` repo (.NET 10 / EF Core 10; cwd = the
repo root of the git worktree on branch `fix/562-account-id-concurrency-token`, which already carries the
four commits of PR #671). Execute this file top to bottom. You edit, build, test, commit, push. You do NOT
open a new PR — the branch is PR #671 and the push updates it.

**Same rules as `docs/plans/562-tenant-write-token/01-implementer-runbook.md`** (read its Rules block
first): transcribe blocks VERBATIM; blocks marked **PROTECTED** are correctness-critical (tenant
isolation) and are never repaired locally — on any failure in one, STOP and report the exact error;
run commands EXACTLY as given; an expected RED is a clean result only when it is the named test failing
at the named assertion with the stable discriminator; anything else is a STOP; never widen an assertion.

**Why this increment exists (the findings it answers — facts, not opinions):**
- R1-F3 (tenant-isolation adversary): `TenantStampInterceptor.LogDatabaseRefusal` ran with no exception
  guard before the base `ThrowingConcurrencyException` call. Observed with a logger that throws: the
  caller receives `DbUpdateException (An error occurred while saving the entity changes. See the inner
  exception for details.)` instead of `DbUpdateConcurrencyException` — so `Program.cs`'s 409 mapping and
  the `catch (DbUpdateConcurrencyException)` sites in `IdentityProvider` and `IdempotencyMiddleware`
  would see a 500-class failure. Invariant INV-5a: the logging path never changes the exception the
  caller sees.
- R1-F2 (adversary): `AccountIdConcurrencyTokenModelTests.EveryNonKeyAccountId_IsAConcurrencyToken`
  selected carriers with the walk's own `ClrType == typeof(Guid)` predicate, so a future nullable or
  converted `AccountId` would be skipped by the walk AND the guard with every test green (#673 tracks
  closing that by construction; this increment makes the guard NAME such a property).
- R1-F1 (repo-rules + CodeRabbit inline comment 3920317720): the header of
  `TenantWriteRefusalLoggingTests.cs` still said the logging lives in `SaveChangesFailed`; the shipped
  hook is `ThrowingConcurrencyException`/`Async`.

## Files you may edit (complete allow-list)
- `src/Cluckwork.Infrastructure/Persistence/Interceptors/TenantStampInterceptor.cs` (one method body — PROTECTED)
- `tests/Cluckwork.Api.IntegrationTests/TenantWriteRefusalLoggingTests.cs` (usings, one new test + one nested class, one header comment — PROTECTED)
- `tests/Cluckwork.Api.IntegrationTests/AccountIdConcurrencyTokenModelTests.cs` (one selector + one assertion — PROTECTED)
- `docs/plans/562-tenant-write-token/02-fix-increment-1.md` (this file — committed with Increment 7, repo convention)
Anything else — including `01-implementer-runbook.md`, which stays as the historical record even though its embedded copy of the test header carries the old sentence — STOP and report.

## Verify prerequisites
```bash
git branch --show-current     # expect: fix/562-account-id-concurrency-token
git rev-parse HEAD            # expect: 667a4242b63c2ce3ae94ae51d6fe16d9fdc92e25
git status --porcelain        # expect: only "?? docs/plans/562-tenant-write-token/02-fix-increment-1.md"
```

## Gates — the SAME rows as the first runbook, cited by ID
- **G1** `dotnet build Cluckwork.sln --configuration Release --no-restore` → `0 Warning(s)` / `0 Error(s)`.
- **G2** `dotnet test Cluckwork.sln --configuration Release --no-build --verbosity normal` → four `Test Run Successful.` blocks, IntegrationTests **`Total tests: 1655`** (1654 + the one test this increment adds), Domain 365, Application 234, AppHost 10. Narrowed form: `dotnet test tests/Cluckwork.Api.IntegrationTests --configuration Release --no-build --filter "FullyQualifiedName~<TestClass>" --logger "console;verbosity=normal"` — always after a fresh G1.
- G3/G4/G5: unchanged and unaffected (no package, no model, no migration change in this increment). Do not run G4.

===================================================================================
# INCREMENT 5 — the logging path never changes the exception (INV-5a) — Design
===================================================================================

## 5a. RED — the fault-injected logger test (PROTECTED)
Edit `tests/Cluckwork.Api.IntegrationTests/TenantWriteRefusalLoggingTests.cs`.

Find this exact block (the usings):
```csharp
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;
```
Replace with:
```csharp
using Cluckwork.Infrastructure.Persistence;
using Cluckwork.Infrastructure.Persistence.Interceptors;
using Cluckwork.Infrastructure.Providers;
using Cluckwork.Infrastructure.Providers.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Events;
```
Find this exact block (the attribute + signature of the LAST test in the file):
```csharp
    [Fact]
    public async Task SuccessfulTrackedWrite_LogsNothing()
```
Replace with (the new test and its double go BEFORE that test; the last three lines are the original, kept):
```csharp
    // INV-5a (#671 review, round 1): the logging path must never change the
    // exception the caller sees. Program.cs maps DbUpdateConcurrencyException
    // to 409, and IdentityProvider and IdempotencyMiddleware catch it by type;
    // a sink that throws inside the hook would otherwise propagate in its
    // place as a 500. The interceptor is hand-built here with a logger that
    // throws on every call, exactly as the migration tests hand-build it, on
    // the factory's own database.
    [Fact]
    public async Task LoggerFailure_DoesNotChangeTheException()
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

        var tenant = new TenantContext();
        tenant.Resolve(accountA);
        var sink = new ThrowingLogger();
        var options = new DbContextOptionsBuilder<AppDbContext>();
        new PostgresDbContextConfigurator().Configure(options, factory.ConnectionString, new DatabaseResilienceOptions());
        options.AddInterceptors(new TenantStampInterceptor(tenant, sink));
        await using var db = new AppDbContext(options.Options, tenant, new FlockScope());

        var thrown = await CaptureAsync(async () =>
        {
            db.DailyEntries.Update(NewEntry(rowId, accountA, Guid.NewGuid(), Guid.NewGuid()));
            await db.SaveChangesAsync();
        });

        Assert.True(sink.Calls > 0, "the throwing logger was never invoked — the write never reached the log path");
        Assert.True(thrown is DbUpdateConcurrencyException,
            $"the logger's failure replaced the concurrency exception: thrown={thrown?.GetType().Name ?? "none"} ({thrown?.Message})");
    }

    private sealed class ThrowingLogger : ILogger<TenantStampInterceptor>
    {
        public int Calls { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Calls++;
            throw new InvalidOperationException("log sink exploded on purpose (test double)");
        }
    }

    [Fact]
    public async Task SuccessfulTrackedWrite_LogsNothing()
```
Run **G1**, then **G2 narrowed** to `TenantWriteRefusalLoggingTests`. It MUST fail exactly like this — **1 failed, 4 passed, 5 total**:

| Gate row + narrowing | Named test | Assertion | Stable discriminator | Generated fragments | Fixture note | Same failure from another guard? | Negative-test proof |
|---|---|---|---|---|---|---|---|
| G2 narrowed to `TenantWriteRefusalLoggingTests` | `LoggerFailure_DoesNotChangeTheException` | `Assert.True(thrown is DbUpdateConcurrencyException, …)` | `the logger's failure replaced the concurrency exception: thrown=DbUpdateException (An error occurred while saving the entity changes. See the inner exception for details.)` | none | the stub write IS refused by the database; only the exception TYPE is wrong | none | the preceding `Assert.True(sink.Calls > 0, …)` passes first — the throwing logger was invoked, so the failure is at the type assertion, not before the log path |

**If the RED is at `sink.Calls > 0` ("never invoked"), a compile error, or a different count, STOP and report.**

## 5b. GREEN — the guarded logging path (PROTECTED)
Edit `src/Cluckwork.Infrastructure/Persistence/Interceptors/TenantStampInterceptor.cs`. Find this exact block (the whole method):
```csharp
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
```
Replace with:
```csharp
    private void LogDatabaseRefusal(ConcurrencyExceptionEventData eventData)
    {
        if (!tenant.IsResolved) return;

        // This method must never change the exception the caller sees.
        // Program.cs maps DbUpdateConcurrencyException to 409, and
        // IdentityProvider and IdempotencyMiddleware catch it by type; a sink
        // or entry-shape failure in here would otherwise propagate in its
        // place as a 500. Anything thrown while logging is dropped — the
        // refusal itself is still thrown by EF the moment this returns, and
        // the only channel this method has is the one that just failed. Pinned
        // by TenantWriteRefusalLoggingTests.LoggerFailure_DoesNotChangeTheException.
        try
        {
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
        catch (Exception)
        {
            // Deliberately silent — see above.
        }
    }
```
Run **G1**, then **G2 narrowed** to `TenantWriteRefusalLoggingTests`. Expect `Passed: 5`, `Failed: 0`.

## 5c. Commit
```bash
git add src/Cluckwork.Infrastructure/Persistence/Interceptors/TenantStampInterceptor.cs tests/Cluckwork.Api.IntegrationTests/TenantWriteRefusalLoggingTests.cs
git commit -m "fix(tenancy): a failing log sink never replaces the concurrency refusal the caller sees (#562)"
```

===================================================================================
# INCREMENT 6 — the model guard names a non-Guid AccountId (Mechanical, test only)
===================================================================================

No red phase: no entity carries a non-`Guid` `AccountId` today, so the new assertion passes before and
after (that is the point — it would go RED on the day one is mapped; #673 owns the by-construction
close). This is an included-member guard row with no reddenable mutant in this harness; it is recorded
as such, not claimed.

Edit `tests/Cluckwork.Api.IntegrationTests/AccountIdConcurrencyTokenModelTests.cs`. Find this exact block:
```csharp
        var carriers = db.Model.GetEntityTypes()
            .Select(t => (Type: t, AccountId: t.FindProperty("AccountId")))
            .Where(x => x.AccountId is not null && x.AccountId.ClrType == typeof(Guid) && !x.AccountId.IsPrimaryKey())
            .ToList();
```
Replace with (**PROTECTED**):
```csharp
        // Selected by NAME and non-key only — deliberately NOT by CLR type.
        // Both layers key on AccountId being a non-nullable Guid (the walk skips
        // any other type, the interceptor skips any other value), so a nullable
        // or converted AccountId would be fail-open in both with every test
        // green if this selector mirrored the walk's own predicate. It names
        // the property instead (#673 tracks closing that by construction).
        var carriers = db.Model.GetEntityTypes()
            .Select(t => (Type: t, AccountId: t.FindProperty("AccountId")))
            .Where(x => x.AccountId is not null && !x.AccountId.IsPrimaryKey())
            .ToList();

        var wrongType = carriers
            .Where(c => c.AccountId!.ClrType != typeof(Guid))
            .Select(c => $"{c.Type.ShortName()}.AccountId ({c.AccountId!.ClrType.Name})")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(wrongType.Count == 0,
            "AccountId must be a non-nullable Guid on every entity that carries it — the write guard and the " +
            "concurrency token both key on exactly that type, and any other is fail-open in both (#673):\n  " +
            string.Join("\n  ", wrongType));
```
Run **G1**, then **G2 narrowed** to `AccountIdConcurrencyTokenModelTests`. Expect `Passed: 2`.
```bash
git add tests/Cluckwork.Api.IntegrationTests/AccountIdConcurrencyTokenModelTests.cs
git commit -m "test(tenancy): the AccountId token guard selects by name, so a non-Guid AccountId is named rather than skipped (#562)"
```

===================================================================================
# INCREMENT 7 — the test header names the shipped hook (Mechanical, comment only)
===================================================================================

Edit `tests/Cluckwork.Api.IntegrationTests/TenantWriteRefusalLoggingTests.cs`. Find this exact block:
```csharp
// Version race produces, and one that at least seven call sites already
// catch and retry. TenantStampInterceptor.SaveChangesFailed therefore logs
// every concurrency failure that happens under a RESOLVED tenant as
```
Replace with:
```csharp
// Version race produces, and one that at least seven call sites already
// catch and retry. TenantStampInterceptor's ThrowingConcurrencyException and
// ThrowingConcurrencyExceptionAsync hooks (EF's interception point for that
// exception — it never reaches SaveChangesFailed) therefore log
// every concurrency failure that happens under a RESOLVED tenant as
```
Then run **G1** and **G2 in full, in the FOREGROUND**; report the four `Total tests` / `Passed` lines
verbatim (IntegrationTests must read `Total tests: 1655`). If a test outside this slice's files fails
(the known flake is `OtlpSubprocessExporterTests` / `FakeOtlpCollectorTests`, #672), re-run G2 once and
report BOTH results.
```bash
git add tests/Cluckwork.Api.IntegrationTests/TenantWriteRefusalLoggingTests.cs docs/plans/562-tenant-write-token/02-fix-increment-1.md
git commit -m "docs(tenancy): the logging test header names the ThrowingConcurrencyException hook (#562)"
```

===================================================================================
# MUTATION CHECK — M8
===================================================================================

| ID | Mutation (exact edit) | Named test that must go RED | Expected failure | Observed (you fill) | Rebuild run (you fill) |
|---|---|---|---|---|---|
| M8 | In `TenantStampInterceptor.LogDatabaseRefusal`, replace the line `        try` with `        // MUTANT M8: guard removed` and delete the four lines `        catch (Exception)` / `        {` / `            // Deliberately silent — see above.` / `        }` (the braces of the former try block stay, as a plain block) | `TenantWriteRefusalLoggingTests.LoggerFailure_DoesNotChangeTheException` | the sink's exception escapes → `the logger's failure replaced the concurrency exception: thrown=DbUpdateException (An error occurred while saving the entity changes. …)`; the other 4 tests still pass | | |

Apply → **G1** → G2 narrowed to `TenantWriteRefusalLoggingTests` → record → restore the exact original text → `touch` the file → **G1** → G2 narrowed again → `Passed: 5`. Then `grep -rn MUTANT src tests` → nothing, `git status --porcelain` → empty.

===================================================================================
# FINISH — push (no new PR)
===================================================================================
```bash
git log --oneline main..HEAD          # expect: 7 commits — the 4 of PR #671 plus the 3 above
git push origin fix/562-account-id-concurrency-token
```

## Report back
The three commit SHAs, the G1 tail, the four `Total tests`/`Passed` lines from the foreground full run
(both runs if you re-ran), the M8 observation, and — per increment — confirmation that YOU applied its
blocks. If any step could not be completed as written, say which and stop.
