# Auto-regenerate downstream `packages.lock.json` on Dependabot NuGet PRs

Issue: [#203](https://github.com/mforce/cluckwork/issues/203) · Epic: #15

## Problem

Every project in `Cluckwork.sln` carries its own `packages.lock.json` (introduced in
#146, 4 src + 3 test = 7 files) and CI restores with `dotnet restore Cluckwork.sln
--locked-mode`, so a stale lock fails the job with **NU1004**.

The projects form a reference chain:

```
Domain <- Application <- Infrastructure <- Api <- {Domain.Tests, Application.Tests, Api.IntegrationTests}
```

Dependabot bumps a package in **one** project and regenerates **only that project's**
lock file (upstream limitation: dependabot-core#5461). Every downstream project's lock
still pins the old transitive graph, so `--locked-mode` fails on the downstream projects.
This is intrinsic to keeping lock files on projects in a reference chain — the whole point
of #146 — so "just drop the lock files" is not an acceptable fix (it would gut the
reproducibility guarantee for all but the leaf project).

Evidence this is live and recurring:
- 2026-07-24 it broke PRs #192/#194/#195/#196 at once; each needed a manual
  `dotnet restore --force-evaluate` + commit.
- `main` HEAD as of writing is `c104d88 fix(deps): sync integration tests lock file with
  Microsoft.OpenApi 2.11.0 (#213)` — another manual instance.

## Goals (from the issue's "Done means")

1. A Dependabot NuGet PR bumping a src project goes green **without manual intervention**.
2. The workflow **cannot be abused to execute PR-author-controlled build code with an
   elevated token**.

## Non-goals

- npm and github-actions Dependabot PRs (no cross-project lock chain — npm has a single
  `web/package-lock.json`; actions have no lock files). Scope is `dependabot/nuget/**`.
- Fixing lock drift outside Dependabot (a human bumping a package regenerates locks locally;
  CI's existing `--locked-mode` gate already catches a stale commit).

## Chosen architecture

A dedicated workflow `.github/workflows/dependabot-lockfix.yml` triggered
`on: workflow_run` off the **CI** workflow, `types: [completed]`.

Rationale for `workflow_run` over the alternatives:
- A `workflow_run` job runs from the **default branch's** trusted workflow definition, so the
  PR author never controls *what executes* — GitHub's recommended pattern for privileged
  processing of Dependabot/fork PRs.
- It runs with a normal (writable-per-`permissions`) token and full access to Actions
  secrets, **not** the read-only token + Dependabot-secret sandbox that a Dependabot-
  triggered `pull_request`/`pull_request_target` workflow is confined to. That distinction is
  what makes reading the GitHub App private key possible here.

### The load-bearing constraint: re-triggering CI

A push authenticated with the default `GITHUB_TOKEN` **does not trigger another workflow
run** (GitHub recursion-prevention; documented, and confirmed by all four reviewers). So a
`GITHUB_TOKEN` push of the fixed locks would leave the PR's required "Build and test" check
stuck at *"Expected — waiting for status"* (blocked, not even red) — failing Goal #1.

Therefore the fix commit is pushed with a **GitHub App installation token**
(`actions/create-github-app-token`): short-lived (~1h), repository-scoped, `contents: write`
only, not tied to a personal account. A push by the App identity re-triggers CI normally, and
because the App bot's login is **not** `dependabot[bot]`, the actor gate no-ops the resulting
run (loop-safe). A fine-grained PAT would also work but is a long-lived, account-bound secret
requiring manual rotation — rejected.

**One-time human setup (cannot be automated):** create a GitHub App with Repository
Contents: Read and write, install it on `mforce/cluckwork`, and add repo Actions secrets
`LOCKFIX_APP_ID` and `LOCKFIX_APP_PRIVATE_KEY`. Until then the workflow's mint step fails
closed (no push happens); everything else is testable without it.

### Privilege separation: two jobs

`dotnet restore` is `msbuild -t:Restore` — it fully evaluates the project graph, so
repo-controlled MSBuild (`Directory.Build.props/.targets`, `InitialTargets`,
`Before/AfterTargets="Restore"`, `UsingTask`/`Exec`), a repo `nuget.config` (credential
providers), and `global.json` (custom SDK resolvers) can all execute code **during restore**,
with no build step. `actions/checkout` writes the token into `.git/config` by default, so
restore-time code in the same job could exfiltrate it. "PackageReference runs no install.ps1"
is true but **irrelevant** to this — it only rules out legacy `packages.config` scripts.

So restore and the write credential never coexist in one job:

- **Job `compute`** — `permissions: {}` (no token). Checks out the **immutable PR head SHA**
  with `persist-credentials: false`, runs `dotnet restore` against a **workflow-pinned
  `nuget.config`** (nuget.org only, package-source-mapping; the repo's own nuget.config is
  ignored) with a **clean temp package dir** and **cache disabled**, then uploads exactly the
  7 `packages.lock.json` files as an artifact. Runs untrusted MSBuild but has **no credential
  to steal and no write access**.
- **Job `commit`** — `needs: compute`, `permissions: contents: write`. Runs **zero** project
  code. Obtains `lockfix.mjs` and the path allowlist from the **trusted default-branch**
  checkout (never from the PR), downloads the artifact, validates each entry (exact path from
  the 7-file allowlist, regular file, parses as JSON), applies them onto a checkout of the
  same immutable PR head SHA, classifies the working tree, and on a clean lock-only diff
  commits and pushes.

This is the actual control for Goal #2; the post-restore file check is only a secondary
data-hygiene guard, not a security boundary (restore-time code runs before it and could hide
its tracks).

### Provenance gate (job-level `if:`, before a runner/token is issued)

All of these must hold or the job no-ops:
- `github.event.workflow_run.event == 'pull_request'`
- `github.event.workflow_run.actor.login == 'dependabot[bot]'` **and**
  `github.event.workflow_run.triggering_actor.login == 'dependabot[bot]'`
  (a re-run keeps the original `actor` but changes `triggering_actor`; requiring both closes
  the re-run gap)
- `github.event.workflow_run.run_attempt == 1`
- head branch starts with `dependabot/nuget/`
- head repository is this repo, compared by **numeric id**
  (`github.event.workflow_run.head_repository.id == github.event.repository.id`), not name

Deliberately **no** `conclusion == 'success'` gate: CI is *failing* with NU1004 when we need
to act.

Inside the `commit` job, before pushing, resolve the single open PR for the head branch via
the API and re-verify author = `dependabot[bot]`, head repo id, and that the branch tip still
equals `head_sha` (compare-and-swap). The push is **non-force**, so a moved branch is rejected
rather than clobbered, and the job aborts loudly (the newer CI cycle re-fixes).

### Concurrency

```yaml
concurrency:
  group: lockfix-${{ github.event.workflow_run.head_branch }}
  cancel-in-progress: false
```

`cancel-in-progress: false` (never `true` — cancelling mid-push could leave no fix pushed). A
queued second run re-checks out the now-fixed branch, restore comes back clean → no-op.

## Components

### `.github/scripts/lockfix.mjs` (new, tested)

Pure classifier, mirroring the existing `vuln-gate.mjs` pattern. Reads
`git status --porcelain -z` (NUL-delimited — safe for unusual paths) on stdin; the exact
7-path lock-file allowlist is built in. Exit codes:
- `0` — every changed path is one of the 7 known `packages.lock.json` files → caller commits
- `2` — a path outside the allowlist is dirty → caller aborts (do not commit)
- `3` — nothing changed → caller no-ops
- any other exit (crash/exception) → caller treats as **abort** (fail closed)

`node:test` self-tests wired into CI (a step in `ci.yml`, like the vuln-gate self-test). The
`commit` job runs this copy from the trusted default-branch checkout, never the PR's.

### `.github/nuget.lockfix.config` (new)

Minimal restore config: nuget.org as the only source, with package-source-mapping, passed via
`--configfile` so a branch-controlled `nuget.config` cannot introduce sources or credential
providers.

### `.github/workflows/dependabot-lockfix.yml` (new)

The two-job workflow above. Third-party actions pinned to full commit SHAs. Must live on the
default branch to fire at all (`workflow_run` always uses the default-branch copy) — so it is
inert until merged; note this in a header comment.

### `.github/workflows/ci.yml` (edit)

Add one step to the existing `web`/tooling job (or a small dedicated step) running
`node --test .github/scripts/lockfix.test.mjs`, matching how `vuln-gate.test.mjs` is run.

### `AGENTS.md` (edit)

Document that downstream lock drift on Dependabot NuGet PRs is auto-healed by this workflow,
and record the one-time GitHub App setup + the two secrets.

## Verification

1. **Fan-out proof (strong):** on a scratch checkout, bump a **Domain** dependency in
   `Cluckwork.Domain.csproj`, regenerate only Domain's lock (simulating Dependabot), confirm
   `--locked-mode` fails downstream, then `dotnet restore Cluckwork.sln --force-evaluate` and
   assert the **exact expected set** of changed lock files across the whole
   Domain→Application→Infrastructure→Api→tests chain, and that **only** lock files changed.
   (An Api-level bump was already shown to heal in one shot but only exercises the leaf end.)
2. **`lockfix.mjs` self-tests** (`node --test`) cover: all-7-lock diff → 0; a foreign path
   (e.g. a `.csproj` or `nuget.config`) mixed in → 2; empty → 3; malformed input → abort;
   NUL-delimited parsing of an unusual filename.
3. **Provenance gate:** empirically confirm on a throwaway PR that a **non-Dependabot** push
   to a `dependabot/nuget/*`-named branch yields a non-`dependabot[bot]` `actor.login` in the
   `workflow_run` event (the F5 caveat), so the copy-cat-branch path really is gated out.
4. Full local suite green (dotnet + web) before PR.

## Risks / notes

- **Floating versions** (`FluentValidation 12.*`, etc.) mean a `--force-evaluate` on a later
  re-run could resolve a newer patch and produce a diff even absent a Dependabot change. The
  actor gate still breaks any loop (App-bot ≠ dependabot[bot]); worth stating so "clean
  restore ⇒ loop-safe" isn't over-claimed.
- **Dependabot rebase:** adding a commit makes Dependabot stop auto-rebasing that PR (it only
  force-pushes over extra commits given a skip marker). Desired here, but it means such a PR
  can drift behind base and need manual conflict handling — accepted.
- **Solution-membership invariant:** `--force-evaluate` only refreshes projects discovered in
  `Cluckwork.sln`. If a project is ever dropped from the solution its lock won't refresh.
  Standing invariant to protect; the 7-path allowlist makes an unexpected set visible.
- Do not "fix" `--force-evaluate` to `--use-lock-file` (not a `dotnet` flag).
