# CI security gates, lock-file healing, Dependabot, action pinning (#146)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md); this file is the relocated rationale (what shipped, why the short version was insufficient, what not to break).


CI fails a PR when a dependency carries a known **high+** advisory:

- **NuGet** — `dotnet list package --vulnerable` (parsed; the CLI always exits 0).
- **npm, production deps** — `npm audit --omit=dev`. Dev-only advisories (vite,
  vitest, eslint…) are **advisory only** — logged, never blocking, since they
  don't ship to users. Promote to blocking, or bump the dep, when one appears.
- **Dependency review** — PR-only; fails when the diff *introduces* a vulnerable
  dep. Needs the repo's **Dependency graph** (Settings → Advanced Security); while
  it's off the step self-skips with a loud CI warning and activates automatically
  once enabled.
- **CodeQL** (`.github/workflows/codeql.yml`) — SAST, **advisory** (reports to the
  Security tab; not a required check). To gate on it, enable code-scanning merge
  protection in a branch ruleset ("Require code scanning results").
- **Scheduled audit** (`.github/workflows/security-audit.yml`) — the same two
  audit gates on a weekly cron against `main`, plus `workflow_dispatch`. The CI
  gates only fire on a PR or a push, so without this an advisory published
  against a dependency nobody is touching goes unnoticed until the next PR.

**NuGet lock files.** `Directory.Build.props` sets `RestorePackagesWithLockFile`
(and, since #684, `Directory.Packages.props` beside it holds every package
version under Central Package Management — the lock files are format version 2
from then on, which is the CPM lock format, not a resolution change),
so every project has a committed `packages.lock.json` and CI restores with
`--locked-mode` — restores are **deterministic**, and a dependency can't float to
a different resolved version between a green local run and CI. **When you add or
bump a package, run `dotnet restore` and commit the changed lock files in the
same commit** — otherwise CI fails the restore with `NU1004`.

**How the graph learns about transitive NuGet.** Not from the lock files. GitHub
parses `.csproj`/`.vbproj`/`.nuspec`/`.fsproj`/`packages.config` for NuGet, never
`packages.lock.json`, and doesn't derive NuGet transitives statically — on
manifests alone it sees only the direct `PackageReference`s, a fraction of the
resolved closure (the counts drift; `Directory.Packages.props` lists the
versions and the lock files the resolved set). It sees even fewer than that:
a `PackageReference` held in a `Directory.Build.props` (the three xunit
references shared by `tests/Directory.Build.props`) is not in any manifest the
graph parses, so PR-scoped dependency review can omit such a reference
entirely. The tree-scoped `dotnet list package --vulnerable
--include-transitive` gate in `ci.yml` runs on the restored graph and is what
covers those at PR time. All of this leaves
dependency-review blind to a transitively-introduced vulnerable package.
`.github/workflows/dependency-submission.yml` closes that: on a push to `main`
touching the dependency set, Microsoft's component-detection reads the restore
output and submits the resolved graph via the Dependency Submission API. npm needs
none of this — the graph reads `web/package-lock.json` and already has the full tree.

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
Actions secrets `LOCKFIX_APP_CLIENT_ID` and `LOCKFIX_APP_PRIVATE_KEY`. Until both exist
this workflow fails closed (no push) — **and so does the Release workflow (#351),
which shares the same App**: without them its mint step fails on every push to
`main`, so no release is cut.

The **Release** workflow needs **Pull requests: Read and write** and **Issues:
Read and write** on top. Changing an App's permissions does **not** apply to an
existing installation until the installation owner **approves** the request
(GitHub holds it pending), so adding the permissions in the App settings is only
half the job — approve it on the repo's installation too, or the mint keeps
failing with the old grant.

That widens the *installation*; each mint then downscopes with `permission-*`, and
the lockfix job pins `permission-contents: write` so the extra grants never reach
the token that pushes to a Dependabot branch. **Keep that pin when adding
consumers** — and understand its limit: `permission-*` caps the token the action
returns, not the private key, which can always mint the App's full grant. The cap
makes a wider token a deliberate act rather than the default; it is not a
boundary. The lockfix job stays genuinely narrow because it executes no
PR-controlled code, not because of the cap alone.

**Dependabot** (`.github/dependabot.yml`) covers the other half: the gates
*enforce* (a vulnerable dep fails the build), Dependabot *proposes* (it opens the
bump PR, and — with Dependabot alerts enabled in repo settings — flags a new
advisory the day it publishes). Neither replaces the other. Weekly grouped
version updates for `github-actions`, `npm` (`web/`) and `nuget`; security fixes
arrive ungrouped so they can be read and merged on their own.

Both audit gates run through `.github/scripts/vuln-gate.mjs` (self-tested with
`node --test`), which shares one **escape hatch**: `.github/security-exceptions.json`.
Add a `{ id: GHSA-…, ecosystem, reason, expires }` entry to mute one advisory
until a **required** calendar date — past it, the advisory blocks again and CI
warns the entry is stale. The gate **fails closed**: a malformed report (e.g. an
`npm audit` registry error), an unknown severity, or a malformed exception
(missing scope/reason, impossible date, non-GHSA id) all block rather than pass.
The `id` must be an exact GHSA, so an advisory GitHub only knows by CVE can't be
excepted — bump or pin the package instead. Reach for an exception only when
there's no fixed version to move to; prefer bumping or pinning a patched
transitive version (npm `overrides` / direct NuGet reference) first. The same
file feeds dependency-review's allowlist, so the gates never disagree.

### Pin third-party Actions to a commit SHA

Third-party GitHub Actions (anything **not** `actions/*` or `github/*`) are pinned
to a full commit SHA with a trailing `# vX.Y.Z` comment — **never** a mutable
version tag. A compromised action can retarget a "trusted" tag to malicious code
that exfiltrates CI secrets; both the 2026-03 `aquasecurity/trivy-action` and the
2025-03 `tj-actions/changed-files` incidents did exactly that. Dependabot's
`github-actions` ecosystem reads the trailing comment and bumps **both** the SHA
and the comment on a new release, so a SHA pin stays current. GitHub-owned
`actions/*` and `github/*` may keep major-version tags (GitHub-controlled, lower
risk). Currently SHA-pinned: `actions/create-github-app-token`,
`aquasecurity/trivy-action`, and
`advanced-security/component-detection-dependency-submission-action`.
