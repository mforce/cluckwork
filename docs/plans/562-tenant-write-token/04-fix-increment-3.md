# Fix increment 3 — #562 / PR #671, round-3 findings (one heading level, one message string)

You are an autonomous coding agent with FULL tools in the `cluckwork` repo (cwd = the repo root of the git
worktree on branch `fix/562-account-id-concurrency-token`, at `3388992ae1c5b0d455ca9009058b6bb266d417b0`).
Execute this file top to bottom. Same rules as `docs/plans/562-tenant-write-token/01-implementer-runbook.md`
(read its Rules block first): transcribe VERBATIM; run commands EXACTLY; STOP on anything unexpected.

**What this answers (facts):** round 3 found no product defect. Two wording items:
- CodeRabbit (inline 3920481590): `docs/plans/562-tenant-write-token/03-fix-increment-2.md:37` uses a
  level-1 heading `# INCREMENT 8 …` directly followed by `### 8a` (markdownlint MD001, skipped level).
- The adversary seat: the assertion message in `AccountIdConcurrencyTokenModelTests.cs` says the write
  guard "skips a null value"; it skips any value that does not box to a `Guid` (a converter-backed
  non-null id too). The comment above the selector already says so; the message's parenthetical does not.

No executable behaviour changes. No RED phase. No mutation row.

## Files you may edit (complete allow-list)
- `docs/plans/562-tenant-write-token/03-fix-increment-2.md` (one heading line)
- `tests/Cluckwork.Api.IntegrationTests/AccountIdConcurrencyTokenModelTests.cs` (one assertion message string)
- `docs/plans/562-tenant-write-token/04-fix-increment-3.md` (this file — committed, repo convention)
Anything else — STOP and report.

## Verify prerequisites
```bash
git branch --show-current     # expect: fix/562-account-id-concurrency-token
git rev-parse HEAD            # expect: 3388992ae1c5b0d455ca9009058b6bb266d417b0
git status --porcelain        # expect: only "?? docs/plans/562-tenant-write-token/04-fix-increment-3.md"
```

## Gates (same rows as the first runbook)
- **G1** `dotnet build Cluckwork.sln --configuration Release --no-restore` → `0 Warning(s)` / `0 Error(s)`.
- **G2 narrowed**: `dotnet test tests/Cluckwork.Api.IntegrationTests --configuration Release --no-build --filter "FullyQualifiedName~AccountIdConcurrencyTokenModelTests" --logger "console;verbosity=normal"` → `Passed: 2`, `Failed: 0`.

## INCREMENT 9 — two wording fixes (Mechanical)

### 9a. `docs/plans/562-tenant-write-token/03-fix-increment-2.md` — heading level
Find this exact line:
```
# INCREMENT 8 — the guard's comment and message say what the code does (Mechanical)
```
Replace with:
```
## INCREMENT 8 — the guard's comment and message say what the code does (Mechanical)
```

### 9b. `tests/Cluckwork.Api.IntegrationTests/AccountIdConcurrencyTokenModelTests.cs` — the message
Find this exact block:
```csharp
        Assert.True(wrongType.Count == 0,
            "AccountId must be a non-nullable Guid on every entity that carries it — the token walk skips any " +
            "other CLR type, and the write guard skips a null value (an unstamped insert, an unchecked write) " +
            "(#673):\n  " + string.Join("\n  ", wrongType));
```
Replace with:
```csharp
        Assert.True(wrongType.Count == 0,
            "AccountId must be a non-nullable Guid on every entity that carries it — the token walk skips any " +
            "other CLR type, and the write guard skips any value that is not a Guid, a null Guid? or a " +
            "converted id alike (an unstamped insert, an unchecked write) (#673):\n  " +
            string.Join("\n  ", wrongType));
```

### 9c. Build, test, commit, push
Run **G1**, then **G2 narrowed** (above) → `Passed: 2`. Then:
```bash
git add docs/plans/562-tenant-write-token/03-fix-increment-2.md tests/Cluckwork.Api.IntegrationTests/AccountIdConcurrencyTokenModelTests.cs docs/plans/562-tenant-write-token/04-fix-increment-3.md
git commit -m "docs(tenancy): heading level in the fix runbook, and the guard message names every non-Guid value (#562)"
git log --oneline main..HEAD   # expect: 9 commits
git push origin fix/562-account-id-concurrency-token
git status --porcelain         # expect: empty
```

## Report back
The commit SHA, the G1 tail, the G2-narrowed summary line, and confirmation that you applied both blocks
verbatim. If any step could not be completed as written, say which and stop.
