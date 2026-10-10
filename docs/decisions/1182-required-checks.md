# Require eight CI checks on `main`, tested against the latest `main` (#1182)

> **Rule** — the one-paragraph version lives in [`.github/AGENTS.md`](../../.github/AGENTS.md);
> this file is the relocated rationale (what shipped, why the short version was
> insufficient, what not to break).

**Status:** accepted
**Date:** 2026-10-10

## What happened

#1162 merged with green CI and turned `main` red. Its CI had run against a
`main` from before #1155, which enabled the react-hooks ESLint rules. After the
merge, `main` at `3e879b0c` had two `react-hooks/exhaustive-deps` errors in
`web/src/components/ExpandedLayRate.tsx`, and every open PR's
"Web typecheck, test, and build" job failed with it until #1176 fixed them.
GitHub reported #1162 as mergeable, but that only meant it had no conflicts.
`main` had no required status checks, so nothing required the PR to be tested
against the `main` it landed on.

## The rule

The ruleset "Main required checks" (id 24852475) applies to the default branch
and requires these eight checks, each pinned to the GitHub Actions app
(`integration_id` 15368), with "require branches to be up to date before
merging" on:

- `Build, audit and schema docs`
- `Tests (domain)`, `Tests (application)`, `Tests (apphost)`, `Tests (integration)`
- `Web typecheck, test, and build`
- `Image build + smoke test (amd64)`, `Image build + smoke test (arm64)`

All eight are `ci.yml` jobs. The names live in the ruleset, not in this
repository. Renaming a job's `name:` or a matrix value one of them matches
(`matrix.group`, `matrix.arch`), or adding an `on.pull_request.paths` filter to
`ci.yml`, leaves a required check stuck at "Expected", and then no PR can
merge. Such a change MUST update the ruleset in the same PR. Read it with
`gh api repos/mforce/cluckwork/rulesets/24852475`.

Strict mode is the point. A PR must merge `main` again whenever `main` moves,
so its checks always ran against the `main` it lands on. Update the branch with
a merge commit from `origin/main`, never a force-push.

## Why not the obvious alternative

The obvious move is to require every check CI reports. Four kinds must stay
out:

- **A path-filtered workflow's checks**, such as `e2e-smoke.yml`'s Playwright
  shards. A PR outside the filter never runs them, so a required one waits at
  "Expected" forever. This is the hazard [#782](782-ci-job-gating.md) records
  for `on.pull_request.paths`.
- **`Classify the changed paths`.** It is `continue-on-error`, so its reported
  result says nothing; requiring it guards nothing.
- **CodeQL.** It is advisory here.
- **Anything a docs-only PR cannot produce.** `web` and `image` are skipped by
  `if:` on documentation-only PRs (#782). A skipped job satisfies a required
  check, so requiring them is safe. A filtered-out workflow reports nothing,
  which does not.

Requiring the checks without pinning `integration_id` would accept a commit
status of the same name from any source. Any token with status-write access
could post a fake green. The pin accepts only check runs from GitHub Actions.

A merge queue would also test each PR against the `main` it lands on, without
the manual merge of `main`. It was not chosen for now; strict mode needs no
workflow change.

## What this does NOT cover

- **Reviews.** `main` still requires zero approving reviews. The release
  token's ability to approve a PR stays inert: an approval cannot satisfy a
  status check ([#351](351-releases.md)).
- **Hotfix lines.** The ruleset targets only the default branch. A
  `release/vX.Y.x` branch keeps the two release rulesets and requires no
  checks.
- **The release PR.** It is opened with the App token, so it gets CI and can
  satisfy the checks (#351). Like any PR, it must be up to date with `main`.

## How it is enforced

GitHub enforces the ruleset at merge time. **Nothing in this repository can see
the ruleset**, so no test fails when a `ci.yml` change breaks a required name;
it relies on review. The symptom is a PR whose checks all pass but whose merge
box still waits on a check named in the ruleset.
