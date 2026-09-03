# Fix increment 2 — #562 / PR #671, round-2 findings (comments and one assertion message only)

You are an autonomous coding agent with FULL tools in the `cluckwork` repo (cwd = the repo root of the git
worktree on branch `fix/562-account-id-concurrency-token`, at `c4f1f8c99592adf00ee042f7441f1deca37c251a`).
Execute this file top to bottom. Same rules as `docs/plans/562-tenant-write-token/01-implementer-runbook.md`
(read its Rules block first): transcribe VERBATIM; run commands EXACTLY; STOP on anything unexpected.

**What this answers (facts):** round 2 found no product defect. Two comments and one assertion message
introduced by fix increment 1 overstate or mis-state a mechanism:
- `AccountIdConcurrencyTokenModelTests.cs` said a nullable `AccountId` is "fail-open in both" layers. A
  populated `Guid?` boxes to `Guid` and the interceptor still checks it; only a **null** value (or a
  converted type) is fail-open in both, and a null on Added is inserted unstamped. The token walk skips
  the type unconditionally.
- `TenantWriteRefusalLoggingTests.cs` said the context is hand-built "exactly as the migration tests";
  those pass two separate `TenantContext` instances, this test (correctly) shares one, as DI does.

No executable behaviour changes. No RED phase (comments). No mutation row.

## Files you may edit (complete allow-list)
- `tests/Cluckwork.Api.IntegrationTests/AccountIdConcurrencyTokenModelTests.cs` (one comment block, one assertion message)
- `tests/Cluckwork.Api.IntegrationTests/TenantWriteRefusalLoggingTests.cs` (one comment block)
- `docs/plans/562-tenant-write-token/03-fix-increment-2.md` (this file — committed, repo convention)
Anything else — STOP and report.

## Verify prerequisites
```bash
git branch --show-current     # expect: fix/562-account-id-concurrency-token
git rev-parse HEAD            # expect: c4f1f8c99592adf00ee042f7441f1deca37c251a
git status --porcelain        # expect: only "?? docs/plans/562-tenant-write-token/03-fix-increment-2.md"
```

## Gates (same rows as the first runbook)
- **G1** `dotnet build Cluckwork.sln --configuration Release --no-restore` → `0 Warning(s)` / `0 Error(s)`.
- **G2 narrowed**: `dotnet test tests/Cluckwork.Api.IntegrationTests --configuration Release --no-build --filter "FullyQualifiedName~AccountIdConcurrencyTokenModelTests|FullyQualifiedName~TenantWriteRefusalLoggingTests" --logger "console;verbosity=normal"` → `Passed: 7` (2 + 5), `Failed: 0`.

===================================================================================
# INCREMENT 8 — the guard's comment and message say what the code does (Mechanical)
===================================================================================

### 8a. `tests/Cluckwork.Api.IntegrationTests/AccountIdConcurrencyTokenModelTests.cs` — the selector comment
Find this exact block:
```csharp
        // Selected by NAME and non-key only — deliberately NOT by CLR type.
        // Both layers key on AccountId being a non-nullable Guid (the walk skips
        // any other type, the interceptor skips any other value), so a nullable
        // or converted AccountId would be fail-open in both with every test
        // green if this selector mirrored the walk's own predicate. It names
        // the property instead (#673 tracks closing that by construction).
```
Replace with:
```csharp
        // Selected by NAME and non-key only — deliberately NOT by CLR type.
        // The token walk skips any AccountId whose CLR type is not exactly Guid
        // (so a Guid? or a converted id gets no token at all), and the
        // interceptor skips any VALUE that does not box to a Guid (a Guid? that
        // is null is neither stamped on Added nor checked on Modified/Deleted;
        // a populated Guid? boxes to Guid and is checked). If this selector
        // mirrored the walk's own predicate, such a property would vanish from
        // the guard with every test green; naming the property instead makes it
        // red the day one is mapped (#673 tracks closing that by construction).
```

### 8b. Same file — the assertion message
Find this exact block:
```csharp
        Assert.True(wrongType.Count == 0,
            "AccountId must be a non-nullable Guid on every entity that carries it — the write guard and the " +
            "concurrency token both key on exactly that type, and any other is fail-open in both (#673):\n  " +
            string.Join("\n  ", wrongType));
```
Replace with:
```csharp
        Assert.True(wrongType.Count == 0,
            "AccountId must be a non-nullable Guid on every entity that carries it — the token walk skips any " +
            "other CLR type, and the write guard skips a null value (an unstamped insert, an unchecked write) " +
            "(#673):\n  " + string.Join("\n  ", wrongType));
```

### 8c. `tests/Cluckwork.Api.IntegrationTests/TenantWriteRefusalLoggingTests.cs` — the new test's comment
Find this exact block:
```csharp
    // a sink that throws inside the hook would otherwise propagate in its
    // place as a 500. The interceptor is hand-built here with a logger that
    // throws on every call, exactly as the migration tests hand-build it, on
    // the factory's own database.
```
Replace with:
```csharp
    // a sink that throws inside the hook would otherwise propagate in its
    // place as a 500. The interceptor is hand-built here with a logger that
    // throws on every call, in the shape the migration tests use, on the
    // factory's own database — with ONE TenantContext shared by the
    // interceptor and the context, as DI wires it (the migration tests pass
    // two unresolved instances, which is fine there and would be wrong here).
```

### 8d. Build, test, commit, push
Run **G1**, then **G2 narrowed** (above) → `Passed: 7`. Then:
```bash
git add tests/Cluckwork.Api.IntegrationTests/AccountIdConcurrencyTokenModelTests.cs tests/Cluckwork.Api.IntegrationTests/TenantWriteRefusalLoggingTests.cs docs/plans/562-tenant-write-token/03-fix-increment-2.md
git commit -m "test(tenancy): the AccountId guard's comment and message name which layer is open for which shape (#562)"
git log --oneline main..HEAD   # expect: 8 commits
git push origin fix/562-account-id-concurrency-token
git status --porcelain         # expect: empty
```

## Report back
The commit SHA, the G1 tail, the G2-narrowed summary line, and confirmation that you applied the three
blocks verbatim. If any step could not be completed as written, say which and stop.
