# Dependabot lockfix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Auto-regenerate every downstream `packages.lock.json` on a Dependabot NuGet PR so the PR's CI goes green without manual intervention, using an unabusable privileged workflow.

**Architecture:** A `workflow_run` workflow (runs from the trusted default-branch definition) fires after CI completes on a Dependabot NuGet PR. A **`compute`** job with **no credentials** runs `dotnet restore --force-evaluate` on the immutable PR head SHA and uploads the 7 regenerated lock files as an artifact. A **`commit`** job holding a short-lived **GitHub App** token runs **zero project code**: it validates the artifact against a trusted 7-path allowlist, classifies the diff with a unit-tested Node script (from the trusted default-branch checkout, never the PR), and, if only lock files changed, commits and pushes with the App token — which re-triggers CI (a `GITHUB_TOKEN` push would not).

**Tech Stack:** GitHub Actions (`workflow_run`, `actions/create-github-app-token`), .NET 10 (`dotnet restore --force-evaluate`), Node 22 (ESM classifier + `node:test`).

## Global Constraints

- Scope is `dependabot/nuget/**` branches only (npm/actions have no lock chain). Copy verbatim: gate prefix `dependabot/nuget/`.
- The 7 lock files (exact, repo-relative, forward-slash), in dependency order:
  `src/Cluckwork.Domain/packages.lock.json`, `src/Cluckwork.Application/packages.lock.json`, `src/Cluckwork.Infrastructure/packages.lock.json`, `src/Cluckwork.Api/packages.lock.json`, `tests/Cluckwork.Domain.Tests/packages.lock.json`, `tests/Cluckwork.Application.Tests/packages.lock.json`, `tests/Cluckwork.Api.IntegrationTests/packages.lock.json`.
- Restore command is exactly `dotnet restore Cluckwork.sln --force-evaluate` (never `--use-lock-file`, which is not a `dotnet` flag).
- Provenance gate (all required): `workflow_run.event == 'pull_request'`, `actor.login == 'dependabot[bot]'` **and** `triggering_actor.login == 'dependabot[bot]'`, `run_attempt == 1`, `startsWith(head_branch, 'dependabot/nuget/')`, `head_repository.id == repository.id`. **No** `conclusion == 'success'` gate.
- The `compute` job has `permissions: {}` and `persist-credentials: false`. The `commit` job has `permissions: contents: write`, runs no `dotnet`/MSBuild, and runs the classifier from the trusted default-branch checkout.
- Push is **non-force** (compare-and-swap); a moved branch aborts the job.
- Secrets consumed: `LOCKFIX_APP_ID`, `LOCKFIX_APP_PRIVATE_KEY` (one-time human setup; workflow fails closed until present).
- Action versions follow the repo's existing tag convention (`actions/checkout@v7`, `actions/setup-dotnet@v6`); all actions used are first-party `actions/*`. Dependabot's github-actions ecosystem keeps them bumped.
- Every `web/` or script change already ships with tests (standing repo rule); the classifier is TDD.

---

## File structure

- **Create** `.github/scripts/lockfix.mjs` — pure classifier of `git status --porcelain -z`. One responsibility: decide commit / abort / noop against the 7-path allowlist.
- **Create** `.github/scripts/lockfix.test.mjs` — `node:test` self-tests for the classifier.
- **Create** `.github/nuget.lockfix.config` — minimal restore config (nuget.org only + source mapping) pinned via `--configfile`.
- **Create** `.github/workflows/dependabot-lockfix.yml` — the two-job `workflow_run` workflow.
- **Modify** `.github/workflows/ci.yml` — add one step running the classifier self-tests (next to the existing vuln-gate self-test).
- **Modify** `AGENTS.md` — document the auto-heal + the one-time App setup.

---

## Task 1: Lock-file diff classifier (`lockfix.mjs`) + self-tests

**Files:**
- Create: `.github/scripts/lockfix.mjs`
- Test: `.github/scripts/lockfix.test.mjs`
- Modify: `.github/workflows/ci.yml` (add self-test step after line ~81, the vuln-gate self-test)

**Interfaces:**
- Produces (consumed by Task 2's workflow and by the tests):
  - `export const LOCK_FILES: readonly string[]` — the 7 exact paths.
  - `export function changedPaths(porcelainZ: string): string[]` — every path named by a `git status --porcelain -z` stream (target + rename/copy source).
  - `export function classify(porcelainZ: string): { action: "commit" | "abort" | "noop", foreign: string[], locks: string[] }`.
  - CLI: `git status --porcelain -z | node .github/scripts/lockfix.mjs` → exit `0` commit / `2` abort / `3` noop; any other exit = crash = caller aborts.

- [ ] **Step 1: Write the failing tests**

Create `.github/scripts/lockfix.test.mjs`:

```js
// Self-tests for the Dependabot lock-file classifier (#203). Run with
// `node --test .github/scripts/lockfix.test.mjs`.
//
// The load-bearing cases are the ones that MUST refuse to commit: any non-lock
// path in the diff, a NUL-delimited path with a space, and a rename that drags
// in a foreign source. Each asserts the fail-closed verdict, not just absence.

import test from "node:test";
import assert from "node:assert/strict";
import { changedPaths, classify, LOCK_FILES } from "./lockfix.mjs";

// Build a `git status --porcelain -z` stream: each record is `XY <path>` and
// the stream is NUL-terminated per record.
const z = (...records) => records.map((r) => r + "\0").join("");

test("the allowlist is exactly the 7 solution lock files", () => {
  assert.equal(LOCK_FILES.length, 7);
  assert.ok(LOCK_FILES.every((p) => p.endsWith("/packages.lock.json")));
});

test("a diff of only lock files -> commit", () => {
  const r = classify(
    z(" M src/Cluckwork.Domain/packages.lock.json",
      " M tests/Cluckwork.Api.IntegrationTests/packages.lock.json"),
  );
  assert.equal(r.action, "commit");
  assert.equal(r.locks.length, 2);
  assert.deepEqual(r.foreign, []);
});

test("no changes -> noop", () => {
  assert.equal(classify("").action, "noop");
});

test("a foreign path mixed in -> abort (fail closed)", () => {
  const r = classify(
    z(" M src/Cluckwork.Api/packages.lock.json",
      " M src/Cluckwork.Api/Cluckwork.Api.csproj"),
  );
  assert.equal(r.action, "abort");
  assert.deepEqual(r.foreign, ["src/Cluckwork.Api/Cluckwork.Api.csproj"]);
});

test("a planted nuget.config -> abort", () => {
  assert.equal(classify(z(" M nuget.config")).action, "abort");
});

test("NUL-delimited: a path containing a space is ONE field", () => {
  // porcelain -z does not quote; a naive whitespace split would mis-read this.
  const r = classify(z(" M weird dir/packages.lock.json"));
  assert.equal(r.action, "abort");
  assert.deepEqual(r.foreign, ["weird dir/packages.lock.json"]);
});

test("a rename that drags in a non-lock SOURCE is caught", () => {
  // Rename record: `R  <new>\0<old>\0` — both paths must be inspected.
  const r = classify("R  src/Cluckwork.Api/packages.lock.json\0evil/secrets.txt\0");
  assert.equal(r.action, "abort");
  assert.ok(r.foreign.includes("evil/secrets.txt"));
});

test("changedPaths returns both target and source of a rename", () => {
  assert.deepEqual(
    changedPaths("R  a/packages.lock.json\0b/old.json\0"),
    ["a/packages.lock.json", "b/old.json"],
  );
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `node --test .github/scripts/lockfix.test.mjs`
Expected: FAIL — `Cannot find module './lockfix.mjs'`.

- [ ] **Step 3: Write the classifier**

Create `.github/scripts/lockfix.mjs`:

```js
#!/usr/bin/env node
// Dependabot lock-file classifier for CI (#203).
//
// The dependabot-lockfix workflow regenerates every project's
// packages.lock.json with `dotnet restore --force-evaluate` and must decide
// whether the result is safe to commit back to the PR branch. This is that
// decision, kept out of the YAML so it can be unit-tested (mirrors
// vuln-gate.mjs).
//
// It reads `git status --porcelain -z` on stdin — NUL-delimited, so a path with
// a space or newline is a single field, not several — and answers with an exit
// code:
//   0  every changed path is one of the known lock files -> commit
//   2  a path OUTSIDE that set is dirty -> abort, do not commit (fail closed)
//   3  nothing changed -> no-op
// Any other exit (a crash) the caller also treats as abort: a classifier that
// cannot tell must never green-light a commit.
//
// Usage: git status --porcelain -z | node .github/scripts/lockfix.mjs

import { pathToFileURL } from "node:url";

// The exact, closed set of lock files in the solution's reference chain. An
// EXACT allowlist, not a `**/packages.lock.json` glob: if restore ever writes a
// lock somewhere unexpected (a new project, a path typo, a planted file) that
// must surface as "foreign" and abort, not be waved through.
export const LOCK_FILES = Object.freeze([
  "src/Cluckwork.Domain/packages.lock.json",
  "src/Cluckwork.Application/packages.lock.json",
  "src/Cluckwork.Infrastructure/packages.lock.json",
  "src/Cluckwork.Api/packages.lock.json",
  "tests/Cluckwork.Domain.Tests/packages.lock.json",
  "tests/Cluckwork.Application.Tests/packages.lock.json",
  "tests/Cluckwork.Api.IntegrationTests/packages.lock.json",
]);

const ALLOW = new Set(LOCK_FILES);

// Parse `git status --porcelain -z`. Records are `XY <path>`; a rename/copy
// (X or Y is 'R'/'C') is followed by the source path as its own NUL field. We
// take EVERY path a record names — target and any source — so a rename whose
// target is a lock file can't smuggle a foreign source past the check.
export function changedPaths(porcelainZ) {
  const parts = String(porcelainZ).split("\0");
  const paths = [];
  for (let i = 0; i < parts.length; i++) {
    const rec = parts[i];
    if (!rec) continue; // trailing empty field after the final NUL
    const xy = rec.slice(0, 2);
    paths.push(rec.slice(3)); // drop the 2-char status + separating space
    if (xy[0] === "R" || xy[0] === "C" || xy[1] === "R" || xy[1] === "C") {
      const source = parts[++i]; // the rename/copy source is the next field
      if (source) paths.push(source);
    }
  }
  return paths;
}

// commit | abort | noop, with the paths behind the verdict for the log.
export function classify(porcelainZ) {
  const paths = changedPaths(porcelainZ);
  if (paths.length === 0) return { action: "noop", foreign: [], locks: [] };
  const foreign = paths.filter((p) => !ALLOW.has(p));
  if (foreign.length > 0) {
    return { action: "abort", foreign, locks: paths.filter((p) => ALLOW.has(p)) };
  }
  return { action: "commit", foreign: [], locks: paths };
}

const EXIT = { commit: 0, abort: 2, noop: 3 };

async function readStdin() {
  const chunks = [];
  for await (const chunk of process.stdin) chunks.push(chunk);
  return Buffer.concat(chunks).toString("utf8");
}

async function main() {
  const { action, foreign, locks } = classify(await readStdin());
  if (action === "commit") {
    console.log(`lockfix: ${locks.length} lock file(s) to commit:\n  ${locks.join("\n  ")}`);
  } else if (action === "noop") {
    console.log("lockfix: no changes after restore — nothing to commit.");
  } else {
    console.error(
      `::error::lockfix: refusing to commit — ${foreign.length} non-lock path(s) changed:\n  ${foreign.join("\n  ")}`,
    );
  }
  process.exitCode = EXIT[action];
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `node --test .github/scripts/lockfix.test.mjs`
Expected: PASS — 8 tests, 0 failures.

- [ ] **Step 5: Verify the CLI exit codes by hand**

Run:
```bash
printf ' M src/Cluckwork.Domain/packages.lock.json\0' | node .github/scripts/lockfix.mjs; echo "exit=$?"   # expect exit=0
printf '' | node .github/scripts/lockfix.mjs; echo "exit=$?"                                                # expect exit=3
printf ' M README.md\0' | node .github/scripts/lockfix.mjs; echo "exit=$?"                                  # expect exit=2
```
Expected: `exit=0`, `exit=3`, `exit=2` respectively.

- [ ] **Step 6: Wire the self-test into CI**

In `.github/workflows/ci.yml`, immediately after the existing step `Test the vulnerability gate` (which runs `node --test ../.github/scripts/vuln-gate.test.mjs`), add — same `web` job, same `../` prefix because that job's working-directory is `web/`:

```yaml
      - name: Test the lockfix classifier
        run: node --test ../.github/scripts/lockfix.test.mjs
```

- [ ] **Step 7: Commit**

```bash
git add .github/scripts/lockfix.mjs .github/scripts/lockfix.test.mjs .github/workflows/ci.yml
git commit -m "feat(ci): add the Dependabot lock-file diff classifier (#203)"
```

---

## Task 2: Restore config + the `dependabot-lockfix` workflow

**Files:**
- Create: `.github/nuget.lockfix.config`
- Create: `.github/workflows/dependabot-lockfix.yml`

**Interfaces:**
- Consumes: `.github/scripts/lockfix.mjs` (Task 1) via the trusted default-branch checkout; secrets `LOCKFIX_APP_ID` / `LOCKFIX_APP_PRIVATE_KEY`.
- Produces: no code interface; the deliverable is the workflow. Its correctness is proven by the classifier tests (Task 1), the restore fan-out check (Step 3 below), and a YAML/actionlint pass.

- [ ] **Step 1: Write the pinned restore config**

Create `.github/nuget.lockfix.config` — nuget.org as the only source, with package-source mapping, so a branch-controlled `nuget.config` cannot introduce a source or a credential provider during restore:

```xml
<?xml version="1.0" encoding="utf-8"?>
<!-- Trusted restore config for the dependabot-lockfix workflow (#203). Passed
     via `dotnet restore --configfile` so restore ignores any nuget.config on the
     PR branch: exactly one source (nuget.org), locked in by source mapping. -->
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

- [ ] **Step 2: Write the workflow**

Create `.github/workflows/dependabot-lockfix.yml`:

```yaml
# Auto-regenerate downstream packages.lock.json on Dependabot NuGet PRs (#203).
#
# Dependabot bumps a package in ONE project and regenerates only THAT project's
# lock file; every downstream project's lock then fails CI's `--locked-mode`
# restore with NU1004. This heals them automatically.
#
# SECURITY MODEL (see docs/superpowers/specs/2026-07-25-dependabot-lockfix-design.md):
#   - Runs `on: workflow_run`, so the job definition comes from the DEFAULT
#     branch, never the PR — the PR author cannot change what executes.
#   - `compute` runs the untrusted restore with NO credentials; `commit` holds
#     the write token and runs ZERO project code. A restore-time exploit thus
#     has no token to steal and no push rights.
#   - The push uses a GitHub App token, not GITHUB_TOKEN: a GITHUB_TOKEN push
#     would NOT re-trigger CI (recursion prevention), so the PR would never go
#     green. The App identity != dependabot[bot], so the re-triggered CI is
#     gated out below (no loop).
#
# INERT UNTIL MERGED: workflow_run always runs the default-branch copy, so this
# does nothing until it lands on main; and it fails closed until the two
# LOCKFIX_APP_* secrets exist.

name: Dependabot lockfix

on:
  workflow_run:
    workflows: ["CI"]
    types: [completed]

# Least privilege by default; each job widens only what it needs.
permissions: {}

concurrency:
  # Serialize per branch; never cancel — a cancel mid-push could leave no fix.
  group: lockfix-${{ github.event.workflow_run.head_branch }}
  cancel-in-progress: false

jobs:
  compute:
    name: Regenerate lock files (no credentials)
    runs-on: ubuntu-latest
    # Provenance gate. Requires BOTH actor and triggering_actor to be Dependabot
    # (a re-run keeps actor but changes triggering_actor), run_attempt == 1, the
    # nuget branch prefix, and a numeric repo-id match (never a fork).
    if: >-
      github.event.workflow_run.event == 'pull_request' &&
      github.event.workflow_run.actor.login == 'dependabot[bot]' &&
      github.event.workflow_run.triggering_actor.login == 'dependabot[bot]' &&
      github.event.workflow_run.run_attempt == 1 &&
      startsWith(github.event.workflow_run.head_branch, 'dependabot/nuget/') &&
      github.event.workflow_run.head_repository.id == github.event.repository.id
    permissions: {}
    steps:
      - name: Check out the exact commit CI ran on
        uses: actions/checkout@v7
        with:
          # The immutable SHA the gate validated — NOT head_branch (a moving ref
          # an attacker could advance after the gate passed: TOCTOU).
          ref: ${{ github.event.workflow_run.head_sha }}
          persist-credentials: false

      - name: Setup .NET SDK
        uses: actions/setup-dotnet@v6
        with:
          dotnet-version: 10.0.x
          # No cache: a privileged workflow_run reading a PR-populated cache is a
          # documented poisoning surface, and this job restores fresh anyway.
          cache: false

      - name: Regenerate every lock file
        env:
          # Restore into a throwaway dir, not the shared global cache.
          NUGET_PACKAGES: ${{ runner.temp }}/nuget-packages
        run: >-
          dotnet restore Cluckwork.sln --force-evaluate
          --configfile .github/nuget.lockfix.config

      - name: Upload the regenerated lock files
        uses: actions/upload-artifact@v4
        with:
          name: lockfiles
          if-no-files-found: error
          retention-days: 1
          path: |
            src/Cluckwork.Domain/packages.lock.json
            src/Cluckwork.Application/packages.lock.json
            src/Cluckwork.Infrastructure/packages.lock.json
            src/Cluckwork.Api/packages.lock.json
            tests/Cluckwork.Domain.Tests/packages.lock.json
            tests/Cluckwork.Application.Tests/packages.lock.json
            tests/Cluckwork.Api.IntegrationTests/packages.lock.json

  commit:
    name: Commit and push the refreshed locks
    needs: compute
    runs-on: ubuntu-latest
    permissions:
      contents: write
    steps:
      # The classifier and the path allowlist must come from TRUSTED code, never
      # the PR checkout (which a PR could have replaced). Default branch only.
      - name: Check out trusted tooling (default branch)
        uses: actions/checkout@v7
        with:
          ref: ${{ github.event.repository.default_branch }}
          persist-credentials: false
          path: trusted

      - name: Mint a short-lived GitHub App token
        id: app
        uses: actions/create-github-app-token@v2
        with:
          app-id: ${{ secrets.LOCKFIX_APP_ID }}
          private-key: ${{ secrets.LOCKFIX_APP_PRIVATE_KEY }}

      - name: Check out the PR head for pushing
        uses: actions/checkout@v7
        with:
          # Same immutable SHA as compute. Pushing a commit built on this SHA is
          # a fast-forward ONLY if the branch tip still equals it — a built-in
          # compare-and-swap (see the non-force push below).
          ref: ${{ github.event.workflow_run.head_sha }}
          token: ${{ steps.app.outputs.token }}
          path: pr

      - name: Download the regenerated lock files
        uses: actions/download-artifact@v4
        with:
          name: lockfiles
          path: ${{ runner.temp }}/locks

      - name: Apply, classify, and push
        env:
          ARTIFACT_DIR: ${{ runner.temp }}/locks
          HEAD_SHA: ${{ github.event.workflow_run.head_sha }}
          HEAD_BRANCH: ${{ github.event.workflow_run.head_branch }}
        run: bash trusted/.github/scripts/lockfix-apply.sh
```

- [ ] **Step 3: Write the apply/push script**

The `commit` job runs one trusted script (kept in the repo, executed from the `trusted/` checkout). Create `.github/scripts/lockfix-apply.sh`:

```bash
#!/usr/bin/env bash
# Trusted applicator for the dependabot-lockfix workflow (#203). Runs in the
# `commit` job (write token present) and executes NO project code — only git and
# file ops. Reads the regenerated locks from $ARTIFACT_DIR, applies them onto the
# PR checkout under `pr/`, and commits+pushes iff ONLY lock files changed.
set -euo pipefail

# The classifier prints the allowlist; keep the 7 paths here in lockstep with
# LOCK_FILES in lockfix.mjs (the classifier is the enforcing check).
LOCKS=(
  "src/Cluckwork.Domain/packages.lock.json"
  "src/Cluckwork.Application/packages.lock.json"
  "src/Cluckwork.Infrastructure/packages.lock.json"
  "src/Cluckwork.Api/packages.lock.json"
  "tests/Cluckwork.Domain.Tests/packages.lock.json"
  "tests/Cluckwork.Application.Tests/packages.lock.json"
  "tests/Cluckwork.Api.IntegrationTests/packages.lock.json"
)

# Apply each artifact file onto its exact allowlisted path, after checking it is
# a regular file and parses as JSON (reject a poisoned artifact).
for rel in "${LOCKS[@]}"; do
  src="$ARTIFACT_DIR/$rel"
  [ -f "$src" ] || { echo "missing regenerated lock: $rel"; exit 1; }
  node -e "JSON.parse(require('fs').readFileSync(process.argv[1],'utf8'))" "$src" \
    || { echo "regenerated lock is not valid JSON: $rel"; exit 1; }
  cp "$src" "pr/$rel"
done

cd pr

# Classify with the TRUSTED classifier (../trusted/...), not any copy on the PR.
set +e
git status --porcelain -z | node ../trusted/.github/scripts/lockfix.mjs
verdict=$?
set -e

case "$verdict" in
  3) echo "No lock changes after restore — nothing to do."; exit 0 ;;
  0) : ;;  # commit below
  *) echo "Classifier refused (exit $verdict) — not committing."; exit 1 ;;
esac

git config user.name "cluckwork-lockfix[bot]"
git config user.email "cluckwork-lockfix[bot]@users.noreply.github.com"
git add -- "${LOCKS[@]}"
git commit -m "chore(deps): regenerate downstream packages.lock.json (#203)"

# Compare-and-swap: only fast-forward the branch if its tip is still the SHA we
# built on. A moved branch -> non-fast-forward -> rejected (no --force); the next
# CI cycle re-fixes. Never force-push.
git push origin "HEAD:refs/heads/${HEAD_BRANCH}"
```

Update the workflow's last step to call it (already referenced above as `bash trusted/.github/scripts/lockfix-apply.sh`). Note the script `cd pr` then references `../trusted/...`, matching the two checkout paths.

- [ ] **Step 4: Lint the workflow**

Run (install actionlint if available; otherwise a YAML parse is the floor):
```bash
actionlint .github/workflows/dependabot-lockfix.yml || \
  python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/dependabot-lockfix.yml')); print('yaml ok')"
```
Expected: no actionlint errors (or `yaml ok`). Fix any reported issue.

- [ ] **Step 5: Prove the restore fan-out (multi-file)**

Prove a single solution-wide `--force-evaluate` heals **every** downstream lock at once and touches **only** lock files — the exact behaviour the `compute` job relies on. Package-agnostic: pick a package that appears in several locks (`FluentValidation` spans Application, Infrastructure, Api, and the test locks), stale its resolved version in **all** of them, then heal. In a throwaway worktree of the current branch:

```bash
tmp=$(mktemp -d)
git worktree add --detach "$tmp" HEAD
cd "$tmp"
PKG=FluentValidation
# Stale $PKG's resolved version in EVERY lock that pins it (simulates the
# downstream drift Dependabot leaves behind).
for f in $(grep -rl "\"$PKG\"" --include=packages.lock.json src tests); do
  python3 - "$f" "$PKG" <<'PY'
import json,sys
f,pkg=sys.argv[1],sys.argv[2]
d=json.load(open(f))
for tfm,deps in d["dependencies"].items():
    if pkg in deps and "resolved" in deps[pkg]:
        deps[pkg]["resolved"]="0.0.1-stale"
json.dump(d,open(f,"w"),indent=2); print("staled",f)
PY
done
echo "=== staled locks (should be several) ==="; git status --porcelain
echo "=== THE FIX: solution-wide force-evaluate ==="
dotnet restore Cluckwork.sln --force-evaluate
echo "=== changed after force-evaluate (expect ONLY *.lock.json, and >1 of them) ==="
git status --porcelain
git status --porcelain | grep -qv 'packages\.lock\.json' && echo "FAIL: a non-lock path changed" || echo "OK: lock-only diff"
cd - >/dev/null && git worktree remove --force "$tmp"
```
Expected: the staling dirties several locks; after `--force-evaluate` every one is rewritten back to a consistent resolved version and the final diff is **empty** (healed to the committed state) — proving force-evaluate re-syncs the whole chain and never dirties a `.csproj`/`obj/`. Capture the output as evidence. (Empty final diff = the committed locks were already correct, which is the healthy case; the staling→heal round-trip is what demonstrates the rewrite.)

- [ ] **Step 6: Commit**

```bash
git add .github/nuget.lockfix.config .github/workflows/dependabot-lockfix.yml .github/scripts/lockfix-apply.sh
git commit -m "feat(ci): auto-regenerate downstream lock files on Dependabot NuGet PRs (#203)"
```

---

## Task 3: Document the auto-heal + App setup

**Files:**
- Modify: `AGENTS.md`

- [ ] **Step 1: Add the documentation**

In `AGENTS.md`, under the section that covers NuGet lock files / CI dependency handling (search for `packages.lock.json` or `--locked-mode`; add a new subsection there), insert:

```markdown
### Dependabot NuGet PRs: automatic lock-file healing

Dependabot bumps a package in one project and regenerates only that project's
`packages.lock.json`; every downstream project in the reference chain then fails
CI's `--locked-mode` restore with NU1004. The `.github/workflows/dependabot-lockfix.yml`
workflow heals this automatically: after CI completes on a `dependabot/nuget/**`
PR, it re-runs `dotnet restore Cluckwork.sln --force-evaluate` (in a no-credential
job), then commits and pushes the refreshed lock files (in a separate job that
runs no project code and holds a short-lived GitHub App token). The App-token push
re-triggers CI, which then goes green. See
`docs/superpowers/specs/2026-07-25-dependabot-lockfix-design.md` for the security
model.

**One-time setup (required for the push to work):** create a GitHub App with
Repository → Contents: Read and write, install it on this repo, and add the repo
Actions secrets `LOCKFIX_APP_ID` and `LOCKFIX_APP_PRIVATE_KEY`. Until both exist
the workflow fails closed (no push); nothing else breaks.
```

- [ ] **Step 2: Commit**

```bash
git add AGENTS.md
git commit -m "docs(203): document Dependabot lock-file auto-heal and App setup"
```

---

## Post-implementation (not tasks — do after the plan is built)

- Run the full local suite (dotnet build/test + `web` typecheck/test/build) to confirm nothing regressed; the new workflow doesn't run locally, so its "test" is Task 1's classifier tests + Task 2 Step 5's fan-out proof.
- Open the PR; run the usual 4-way review (codex + 2 own agents + pi).
- The user completes the one-time GitHub App setup (Task 3 doc) before or shortly after merge.
- **First real smoke test is post-merge:** the next Dependabot NuGet PR that bumps a shared package should auto-go-green. Watch the first one.
- Empirically confirm the F5 caveat once (a non-Dependabot push to a `dependabot/nuget/*`-named branch yields a non-`dependabot[bot]` `actor.login`), then this can't be re-verified without another such push — note the result on the PR.
- Tick #203 in epic #15 after merge.

## Self-review notes

- **Spec coverage:** workflow_run + App token (Task 2), two-job split (Task 2), provenance gate incl. both actors / run_attempt / numeric repo-id (Task 2 `if:`), head_sha + non-force CAS push (Task 2 script), pinned `--configfile` (Task 2 Step 1), trusted-copy classifier + fail-closed + NUL parse + exact 7-path allowlist (Task 1), self-tests wired to CI (Task 1 Step 6), Domain fan-out verification (Task 2 Step 5), AGENTS.md + App setup (Task 3), floating-version/loop + sln-membership notes (spec Risks). All covered.
- **Cache disabled / clean NuGet dir** in the compute job (Task 2 Step 2). Actions use repo tag convention (justified deviation from SHA-pin rec, noted in Global Constraints).
- **Type consistency:** `LOCK_FILES` / `changedPaths` / `classify` names identical across Task 1 code, tests, and the Task 2 script's references.
