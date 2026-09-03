# Runbook — #670 fix increment 3: three runbook fences expected an empty porcelain right after `git add`

You are the same implementer, in the same worktree (`/home/mforce/.herdr/worktrees/cluckwork/fix-670-user-roles-account-id`, branch `fix/670-user-roles-account-id`, PR #675 open at `2878fb0bc0ca9253f179089fef9d1013aae09b54`). This is a RE-DISPATCH: the first run of this runbook was stopped by the driver after 54 s because step 5b's anchor was ambiguous (the porcelain line occurs twice in `01-…`); the tree was reset to HEAD, nothing of that run survives, and 5b below is the corrected two-line form. Same rules as `01-implementer-runbook.md`; this file is committed beside it as `04-fix-increment-3.md`. Docs only — three lines in two committed runbooks. Execute top to bottom: edit, build, commit, push.

## What was found (CodeRabbit, inline on `02-fix-increment-1.md:146`, `CHANGES_REQUESTED` on `6586d19b`)

After `git add`, `git status --porcelain` lists the staged files (`A  path` / `M  path`); it is empty only after the commit. Three fences in the committed runbooks say `# expect: empty` (or "only the untracked runbook") right after a `git add` — a wrong expectation that a literal reader would STOP on. Verified by the driver in a scratch repository. **Fix: replace each with a staged-list check.** The runbooks are historical records of what was dispatched; the correction is annotated as such, not silently rewritten.

## Rules (unchanged)

- Transcribe VERBATIM. Files you may edit: `docs/plans/670-user-roles-account-id/01-implementer-runbook.md` (two lines), `docs/plans/670-user-roles-account-id/02-fix-increment-1.md` (one line), `docs/plans/670-user-roles-account-id/04-fix-increment-3.md` (this file, committed). Anything else, STOP and report.

## Verify prerequisites

```bash
git branch --show-current   # expect: fix/670-user-roles-account-id
git status --porcelain      # expect: only "?? docs/plans/670-user-roles-account-id/04-fix-increment-3.md"
git rev-parse HEAD          # expect: 2878fb0bc0ca9253f179089fef9d1013aae09b54
```

===================================================================================
# INCREMENT 5 — the three fences
===================================================================================

## 5a. `01-implementer-runbook.md`, Increment 1f

Find this exact line:

```text
git status --porcelain   # expect: only "?? docs/plans/670-user-roles-account-id/01-implementer-runbook.md"
```

Replace with:

```text
git diff --cached --name-only   # expect: exactly the paths given to git add above (the runbook itself stays untracked until Increment 2). [Corrected in fix increment 3: the dispatched text expected a porcelain showing only the untracked runbook, but a porcelain after git add also lists the staged files.]
```

## 5b. `01-implementer-runbook.md`, Increment 2e

Find this exact TWO-line block (the `git add` line of Increment 2e followed by its porcelain check — the same porcelain line also appears, correctly, in FINISH after the PR-body cleanup; that one is NOT to be touched):

```text
git add tests/Cluckwork.Application.Tests/TenantBypass/TenantBypassRealTreeTests.cs tests/Cluckwork.Application.Tests/TenantBypass/Data/filter-free-set-sites.tsv AGENTS.md docs/decisions/530-multi-farm-tenancy.md docs/plans/670-user-roles-account-id/01-implementer-runbook.md
git status --porcelain   # expect: empty
```

It occurs ONCE in this file as a pair. Replace with:

```text
git add tests/Cluckwork.Application.Tests/TenantBypass/TenantBypassRealTreeTests.cs tests/Cluckwork.Application.Tests/TenantBypass/Data/filter-free-set-sites.tsv AGENTS.md docs/decisions/530-multi-farm-tenancy.md docs/plans/670-user-roles-account-id/01-implementer-runbook.md
git diff --cached --name-only   # expect: exactly the paths given to git add above. [Corrected in fix increment 3: the dispatched text expected an empty porcelain, which is true only after the commit.]
```

## 5c. `02-fix-increment-1.md`, COMMIT and PUSH

Find this exact line (it occurs ONCE in this file — verified by the driver):

```text
git status --porcelain   # expect: empty
```

Replace with:

```text
git diff --cached --name-only   # expect: exactly the three paths given to git add above. [Corrected in fix increment 3: the dispatched text expected an empty porcelain, which is true only after the commit — CodeRabbit's finding on PR #675.]
```

## 5d. Build, commit, push

Run **G1** (docs cannot break it; the gate row is cited, not skipped). Then:

```bash
git add docs/plans/670-user-roles-account-id/01-implementer-runbook.md docs/plans/670-user-roles-account-id/02-fix-increment-1.md docs/plans/670-user-roles-account-id/04-fix-increment-3.md
git diff --cached --name-only   # expect: exactly the three paths above
git commit -m "docs(plans): the runbook fences check the staged list after git add, not an empty porcelain (#670)"
git status --porcelain          # expect: empty (after the commit)
git push origin fix/670-user-roles-account-id
```

## Report back

The commit SHA, the G1 tail, the three-path staged list, the empty porcelain after the commit, and confirmation that YOU applied the three lines verbatim.
