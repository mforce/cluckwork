# Runbook — #670 fix increment 1: pin the tracked shape on AspNetUserRoles, and one stale tsv header line

You are the same implementer, in the same worktree (`/home/mforce/.herdr/worktrees/cluckwork/fix-670-user-roles-account-id`, branch `fix/670-user-roles-account-id`, PR #675 open at `4d4f73dec478bdf1b95b97a00e3987fca22f71e0`). Same rules as `docs/plans/670-user-roles-account-id/01-implementer-runbook.md` — this file is committed beside it as `02-fix-increment-1.md`. Execute top to bottom: edit, build, test, the two mutation rows, commit, push. Do NOT open a new PR; the push updates #675.

## What round 1 found

- **F-1-r1 (`tenant-isolation-adversary`, verdict defer):** the four `UserRoleTenantWriteTests` all build DETACHED stubs. The TRACKED shape — load another farm's `AspNetUserRoles` row (this table has no query filter, deliberately: `FirstRunStatusService` reads it anonymously, so loading farm B's grant under tenant A is a one-line query) and then relabel its shadow `AccountId` or `Remove` it — is refused today by `TenantStampInterceptor`'s ORIGINAL-value checks (`Modified` at `TenantStampInterceptor.cs:137`, `Deleted` at `:144`) but is pinned by no test on this table. Not a live defect; a coverage gap on the exact arm whose omission is the mistake #546 records. **Fix: two tests, Mechanical** (they pin behaviour that already holds — no RED phase; the proof they bite is mutation rows M8 and M9 below).
- **F-2-r1 (`repo-rules`, sub-bar observation, confirmed by the driver):** `filter-free-set-sites.tsv:67`, the NON-TENANT section header, still reads `(no AccountId column; …)` while the file's top header (lines 10–13) was rewritten to the CLR-shape rule. **Fix: one comment line, Mechanical.**

Nothing else was found: `repo-rules`, `false-green`, `caller-breakage` clean; the adversary found no exploitable path.

## Rules (unchanged, restated)

- Transcribe blocks VERBATIM. Blocks marked **PROTECTED** are correctness-critical: transcribe or stop.
- Files you may edit: `tests/Cluckwork.Api.IntegrationTests/UserRoleTenantWriteTests.cs`, `tests/Cluckwork.Application.Tests/TenantBypass/Data/filter-free-set-sites.tsv` (line 67 only), `docs/plans/670-user-roles-account-id/02-fix-increment-1.md` (this file, committed). Mutation rows touch `src/Cluckwork.Infrastructure/Persistence/Interceptors/TenantStampInterceptor.cs` transiently and RESTORE it — it is never committed changed. Anything else, STOP and report.
- Gate rows G1–G4 are the ones in `01-implementer-runbook.md`; cite, never retype.

## Verify prerequisites

```bash
git branch --show-current   # expect: fix/670-user-roles-account-id
git status --porcelain      # expect: only "?? docs/plans/670-user-roles-account-id/02-fix-increment-1.md"
git rev-parse HEAD          # expect: 4d4f73dec478bdf1b95b97a00e3987fca22f71e0
```

===================================================================================
# INCREMENT 3 — the tracked shape is pinned per table (F-1-r1); the tsv section header (F-2-r1)
===================================================================================

## 3a. The two tests (PROTECTED) — no RED phase: they pin behaviour that already holds

Edit `tests/Cluckwork.Api.IntegrationTests/UserRoleTenantWriteTests.cs`. Find this exact block (the end of the file):

```csharp
        Assert.True(IsForeignKeyRefusal(thrown),
            $"Add(own farm's user, no resolved tenant) was not refused by the foreign key: thrown={Describe(thrown)}; " +
            $"row exists after={exists}; tenant A={f.AccountA} userA2={userA2}");
        Assert.False(exists, "an unowned role row was written under no resolved tenant");
    }
}
```

Replace with (**PROTECTED**):

```csharp
        Assert.True(IsForeignKeyRefusal(thrown),
            $"Add(own farm's user, no resolved tenant) was not refused by the foreign key: thrown={Describe(thrown)}; " +
            $"row exists after={exists}; tenant A={f.AccountA} userA2={userA2}");
        Assert.False(exists, "an unowned role row was written under no resolved tenant");
    }

    // The TRACKED shape, which the detached tests above do not cover, and the
    // cheapest precondition on this table: IdentityUserRole has no query
    // filter (FirstRunStatusService reads it anonymously), so loading another
    // farm's grant tracked is a one-line query under any tenant. For a tracked
    // row the shadow AccountId's ORIGINAL value is the database's, and the
    // interceptor verifies that original on Modified and on Deleted — the arm
    // whose omission is the mistake #546 records — so both writes are refused
    // before any SQL. Pinned per table because the precondition is what makes
    // this table different from every filtered one.
    [Fact]
    public async Task TrackedRelabel_OfAnotherFarmsRow_IsRefusedByTheInterceptor()
    {
        var f = await SeedAsync();

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(f.AccountA, async db =>
        {
            var row = await db.UserRoles.SingleAsync(ur => ur.UserId == f.UserB && ur.RoleId == f.OwnerRoleId);
            db.Entry(row).Property("AccountId").CurrentValue = f.AccountA;
            await db.SaveChangesAsync();
        }));

        var after = await factory.WithTenantScopeAsync(f.AccountA, db =>
            db.UserRoles.AsNoTracking()
                .Where(ur => ur.UserId == f.UserB && ur.RoleId == f.OwnerRoleId)
                .Select(ur => EF.Property<Guid>(ur, "AccountId"))
                .SingleOrDefaultAsync());

        Assert.True(thrown is TenantWriteMismatchException,
            $"tracked relabel of B's Owner row was not refused by the interceptor: thrown={Describe(thrown)}; " +
            $"row AccountId after={after} (was B={f.AccountB}); tenant A={f.AccountA} userB={f.UserB}");
        Assert.Equal(f.AccountB, after);
    }

    [Fact]
    public async Task TrackedRemove_OfAnotherFarmsRow_IsRefusedByTheInterceptor()
    {
        var f = await SeedAsync();

        var thrown = await CaptureAsync(() => factory.WithTenantScopeAsync(f.AccountA, async db =>
        {
            var row = await db.UserRoles.SingleAsync(ur => ur.UserId == f.UserB && ur.RoleId == f.OwnerRoleId);
            db.UserRoles.Remove(row);
            await db.SaveChangesAsync();
        }));

        var exists = await RowExistsAsync(f.AccountA, f.UserB, f.OwnerRoleId);

        Assert.True(thrown is TenantWriteMismatchException,
            $"tracked Remove of B's Owner row was not refused by the interceptor: thrown={Describe(thrown)}; " +
            $"B's Owner row exists after={exists}; tenant A={f.AccountA} userB={f.UserB}");
        Assert.True(exists, "B's Owner role row was deleted");
    }
}
```

## 3b. The tsv section header

Edit `tests/Cluckwork.Application.Tests/TenantBypass/Data/filter-free-set-sites.tsv`. Find this exact line (67):

```text
# --- NON-TENANT track (no AccountId column; every db.<Table> access is a
```

Replace with:

```text
# --- NON-TENANT track (no AccountId CLR property — see the header above for the #670 shadow column; every db.<Table> access is a
```

## 3c. Build and test

Run **G1**. Then **G2 narrowed** to `UserRoleTenantWriteTests` — expect `Total tests: 6 / Passed: 6` (the two new tests pass at once: they pin behaviour that already holds). Then **G2 narrowed** (Application form) to `TenantBypass` — expect 34 passed (the tsv ROWS are untouched; a header comment is not a candidate).

===================================================================================
# MUTATION CHECKS — the two new tests bite (the proof that replaces a RED phase)

Both rows were run by the driver on this head before dispatch (`670-fix-preflight-1.log`); M8's expected mechanism below is the OBSERVED one — the composite FK, not the token, is the layer that absorbs a relabel once the interceptor is blind.
===================================================================================

Same protocol as the first runbook: apply → **G1** → the NAMED test (G2 narrowed) → record → restore → **G1** → confirm green. Mark with `// MUTANT M<n>` and remove on restore; `git grep -n MUTANT -- src tests` MUST be empty at the end, and `TenantStampInterceptor.cs` MUST be byte-identical to HEAD afterwards (`git diff --exit-code src/Cluckwork.Infrastructure/Persistence/Interceptors/TenantStampInterceptor.cs`).

| ID | Kind | Mutate | Supplied elsewhere? | Named test that must go RED | Expected mechanism + failure | Observed (you fill) | Rebuild run (you fill) |
|---|---|---|---|---|---|---|---|
| M8 | single-layer | `TenantStampInterceptor.cs`, `case EntityState.Modified:` — replace the line `                    Verify(entry, prop.OriginalValue);` (the FIRST of the two `Verify` lines under `Modified`) with `                    _ = prop.OriginalValue; // MUTANT M8: original not verified on Modified` | n/a — a replacement, not a deletion | `UserRoleTenantWriteTests.TrackedRelabel_OfAnotherFarmsRow_IsRefusedByTheInterceptor` | only the CURRENT value (A == tenant) is checked → the UPDATE reaches the database with `SET "AccountId" = A WHERE … "AccountId" = B`; the WHERE matches (B is the tracked original), but the composite FK refuses the new pair `(B's user, A)` — **the second layer for a relabel is the FK, not the token** → `thrown=DbUpdateException / inner=PostgresException`, `row AccountId after=<B> (was B=<B>)`. The test pins the interceptor BY TYPE, so it goes RED on `was not refused by the interceptor: thrown=DbUpdateException / inner=PostgresException` with the row unchanged. Observed exactly so by the driver's preflight. **Also expected red**: `TenantWriteGuardTests.Modified_AccountIdRewrittenToCurrentTenant_Throws` (1 of 9). The other five tests in the class stay green. | | |
| M9 | guard | `TenantStampInterceptor.cs`, `case EntityState.Deleted:` — replace the line `                    Verify(entry, prop.OriginalValue);` under `Deleted` with `                    _ = prop.OriginalValue; // MUTANT M9: original not verified on Deleted` | n/a — a replacement | `UserRoleTenantWriteTests.TrackedRemove_OfAnotherFarmsRow_IsRefusedByTheInterceptor` | nothing inspects the delete → `DELETE … WHERE … "AccountId" = B` matches → B's Owner row deleted → `was not refused by the interceptor: thrown=none; B's Owner row exists after=False`. **Also expected red**: `TenantWriteGuardTests.Deleted_ForeignAccountId_Throws` (1 of 9), and `DetachedRemove_StubOfAnotherFarmsRow_IsRefusedByTheInterceptor` now reads `thrown=DbUpdateConcurrencyException` (the token absorbs the detached stub — its original is `Guid.Empty`, so the WHERE matches nothing) — that is the second layer holding, report it. | | |

===================================================================================
# COMMIT and PUSH
===================================================================================

```bash
git grep -n -e MUTANT -e DEBUG-670 -- src tests   # expect: nothing
git diff --exit-code src/Cluckwork.Infrastructure/Persistence/Interceptors/TenantStampInterceptor.cs   # expect: exit 0 (byte-identical)
git add tests/Cluckwork.Api.IntegrationTests/UserRoleTenantWriteTests.cs tests/Cluckwork.Application.Tests/TenantBypass/Data/filter-free-set-sites.tsv docs/plans/670-user-roles-account-id/02-fix-increment-1.md
git diff --cached --name-only   # expect: exactly the three paths given to git add above. [Corrected in fix increment 3: the dispatched text expected an empty porcelain, which is true only after the commit — CodeRabbit's finding on PR #675.]
git commit -m "test(tenancy): pin the tracked relabel and remove of another farm's AspNetUserRoles row (#670)"
git push origin fix/670-user-roles-account-id
```

## Report back

The commit SHA, the G1 tail, `Total tests: 6 / Passed: 6` for the class and the TenantBypass line, the observed result of M8 and M9 (every test that reddened, with its message), the two restore checks, and confirmation that YOU applied both blocks.
