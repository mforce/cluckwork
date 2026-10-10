# AGENTS.md — Cluckwork

Poultry egg-farm management system. Backend: **.NET 10** (C#), layered DDD. Frontend: **React 19 + Vite** SPA in `web/`. Postgres via EF Core.

This file and the [scoped rule files](#scoped-rule-files) it indexes are the **canonical rule set** for every coding agent. For the shorter
human guide, start with:
[`CONTRIBUTING.md`](CONTRIBUTING.md) (develop, test, commit),
[`docs/`](docs/README.md) (runbooks, decision records, releasing),
[`SECURITY.md`](SECURITY.md).

**Every rule here is one paragraph.** A `→` link points to its history in
[`docs/decisions/`](docs/decisions/); **read that record before changing the rule.**
An **earned rule** follows a shipped defect. An **accepted-risk rule** records a
deliberate declination, says `No incident`, and is *load-bearing*: breaking it
restores the risk. The link alone does not identify which kind it is. An unlinked
rule is a convention; apply it consistently without archaeology.

**This file does not track GitHub issue state; `gh` is the authority.** Issue numbers point to decisions or changes, never status updates such as open, closed, shipped or remaining. Copied state rots silently: no test or CI run fails when an issue moves, so stale text keeps misdirecting agents. Here, `Current phase: 1.5` remained for eight days after epic #15 closed, and `Remaining:` named three closed issues. Run `gh issue view <n> --json state` or consult the milestone. Rewrite any claim that closing or reopening an issue could invalidate without a repo failure. Keep epic checklists current in GitHub; do not copy their state here.

- [Communicating](#communicating) · [Layout](#layout) · [Build / test / run](#build--test--run)
- [Browser tests, screenshots and verification](#browser-tests-screenshots-and-verification)
- [Secrets](#secrets--never-commit) · [Host-agnostic repo](#host-agnostic-repo-deployment-boundary)
- [Writing a guard](#writing-a-guard-a-test-that-asserts-an-invariant) · [Pre-commit hook](#pre-commit-hook-opt-in) · [CI security gates](#ci-security-gates-146)
- [Scoped rule files](#scoped-rule-files) · [Git / PR workflow](#git--pr-workflow) · [Phase context](#phase-context) · [graphify](#graphify)

## Scoped rule files

Some rules apply only to edits in a few paths. They live in a scoped `AGENTS.md` instead of here. **Before you edit any path listed below, read the linked file. This applies to every harness, whether or not it loads that file automatically.** A rule moves out of this file only when every path that can trigger it is listed with its file. Rules that apply across these trees stay here (#1034).

- `src/**`, `tests/**`, `tools/simulation/**`, `Dockerfile`, `deploy/**`, `.editorconfig`, `Directory.Build.props` → [`src/AGENTS.md`](src/AGENTS.md): read before changing backend code, its tests, the container image or the compose stack: application shape and the module ledger, data and tenancy, auth, boot guards and process roles, one-shot verbs and callers, and the single serving instance (#271).
- `web/**` → [`web/AGENTS.md`](web/AGENTS.md): read before changing SPA text, help or glossary prose, styles, or style guards (#662, #688, #824). Changing role-gated navigation also needs [`src/AGENTS.md`](src/AGENTS.md) (#729).
- `src/Cluckwork.Infrastructure/Persistence/**`, `docs/schema/**`, `tools/schema-docs/**` → [`src/Cluckwork.Infrastructure/Persistence/AGENTS.md`](src/Cluckwork.Infrastructure/Persistence/AGENTS.md): read before adding a migration, changing base reference data or the design-time factory, editing schema docs or their generator, or changing the audit table's physical layout (#407, #283, #417, #318, #505).
- `.github/**`, `docs/releasing.md`, `deploy/**` → [`.github/AGENTS.md`](.github/AGENTS.md): read before changing workflows, Action pins, runner labels, docs-only job gating, image publishing and promotion, or deployment and release instructions (#782, #351).

## Communicating

Write clearly, concisely, and naturally. Lead with the action or answer; skip preambles and recaps.

## Layout

```
src/
  Cluckwork.Domain          aggregates, value objects, domain events (no deps)
  Cluckwork.Application      feature handlers, repository interfaces, validators
  Cluckwork.Infrastructure   EF Core, Identity/JWT, repositories, seeding, jobs
  Cluckwork.Api             minimal-API endpoints, middleware, Program.cs
  Cluckwork.AppHost         .NET Aspire local orchestration — dev only, never a deploy path
  Cluckwork.Analyzers       module-edge analyzer (CW1000-CW1004): build and editor feedback, never shipped
web/                        React/Vite SPA (see web/README.md)
deploy/                     docker-compose (.yml prod, .dev.yml dev DB), .env.example
specs/                      product + technical specs, wireframes
tests/                      Domain.Tests, Application.Tests, Api.IntegrationTests, AppHost.Tests
```

Dependencies point inward: Api → Application/Infrastructure → Domain. Domain depends on nothing.
The **request pipeline order** and the **egg-loop state machine** are drawn in
[`docs/architecture.md`](docs/architecture.md) — read it before moving middleware
or adding an aggregate state.

## Build / test / run

```bash
dotnet build Cluckwork.slnx                 # warnings are errors — keep it clean
dotnet test  Cluckwork.slnx                 # 2887 tests as of 2026-09-21; integration needs Docker
```

- **Integration tests** use real Postgres through Testcontainers (`docker` required), never SQLite; EF SQL semantics differ.
- **Run full stack (prod-like):** `docker compose -f deploy/docker-compose.yml up --build` → SPA + API on http://localhost:8080 (single container; API serves the built SPA from `wwwroot`).
- **Run frontend dev:** `cd web && npm run dev` → http://localhost:5173 (proxies `/api` → :8080).
- **Debug API (no docker stack):** `docker compose -f deploy/docker-compose.dev.yml up -d` (Postgres on :5432), then run/debug `Cluckwork.Api` (Development env). Dev secrets live in **user-secrets**, not files.
- **Run the whole stack under Aspire:** `aspire run` from the repo root (Aspire CLI 13.5) starts Postgres, Redis, the API and Vite with a dashboard → [runbook](docs/runbooks/aspire-local-development.md). **Aspire uses a SECOND database (#565)**, not the Compose database. It has its own volume, username `postgres`, and generated password in the *AppHost's* user-secrets. Aspire injects its connection string only into the `api` resource **it launches**. A hand-run one-shot verb (`bootstrap-admin`, `seed`, `migrate`, `recover-admin`) instead uses the API's user-secrets and silently targets the **Compose** database; pass `ConnectionStrings__Default` explicitly ([form 4](docs/runbooks/first-admin-provisioning.md#4-aspire-apphost-stack)). Keep the committed `LocalPorts` defaults clear of Compose ports. Override them per machine; **never edit the committed file**. → [`565-aspire-local-orchestration.md`](docs/decisions/565-aspire-local-orchestration.md)
- **The four test projects are legs of one fail-fast CI matrix (#775).** `ci.yml`'s `tests` job runs `domain`, `application`, `apphost` and `integration` with `fail-fast: true`. A five-second domain failure cancels the nine-minute integration leg. Separate jobs cannot do this: GitHub does not cancel siblings; a matrix is the only built-in mechanism that stops this spend. `build-and-test`, named **Build, audit and schema docs**, runs no tests. It keeps the once-per-run work, #146's NuGet gate and #417's schema-docs check, and the solution build both reuse. That build enforces warnings-as-errors even in projects outside a test leg's graph. Every new test project needs a matrix leg. `SolutionTestProjectSplitTests` fails when the solution's test-project inventory differs from the set last reconciled with the matrix. Unlike `dotnet test Cluckwork.slnx`, a matrix can silently leave a project unrun. The guard deliberately does not read `ci.yml`. The former workflow-text guard passed despite an `if:`, deleted leg or `continue-on-error` stopping a leg, and failed when `fail-fast` was merely omitted. **An `if:`, `continue-on-error: true` and `fail-fast: false` on that job remain unguarded; only review catches them.** This shortens RED runs only. Green runs remain bounded by integration; container reuse and parallelism are separate optimization options discussed in #775. → [`775-ci-test-matrix.md`](docs/decisions/775-ci-test-matrix.md)
- **Backend coverage is measured, not gated (#776).** `tools/coverage/collect.sh` runs the four test projects under coverlet and writes per-project and combined reports plus `coverage-out/SUMMARY.md`. `.github/workflows/coverage.yml` runs it Mondays, on `workflow_dispatch`, and on `pull_request` changes to the tooling. **There is no threshold; adding one is a separate decision.** `Integration` measures executed code, not asserted behavior: real-Postgres tests touch nearly every assembly incidentally. All reports exclude generated EF migration code because #407 freezes it and every integration test executes it at container boot. Coverage also cannot show whether overlapping tests are redundant; that requires mutation testing. → [`776-backend-coverage.md`](docs/decisions/776-backend-coverage.md)

## Browser tests, screenshots and verification

- **SPA E2E lives in `tools/simulation/ui/` (#277/#385).** Playwright drives the real built SPA using k6's `seed --profile simulation` fixture; `web/` stays Vitest. The suite enforces three rules, each with past catches: never hardcode a credential, never hardcode English, respect the farm clock. Reasoning about `inert` or the accessibility tree must use CDP (`src/ax.ts`); Playwright's own APIs do not model `inert` (#501). The `@phone` tag partitions two projects (#814): `chromium` at 1280 and `chromium-phone` at 390. At 390, the sidebar is `display: none` and the `nav` fixture throws; phone tests use `phone`. → [`277-spa-e2e.md`](docs/decisions/277-spa-e2e.md)
- **Any PR that changes what a user sees attaches screenshots from a stack rebuilt at the head under review (#662).** This is not limited to visual work: a new dialog, new field, changed label or altered state all qualify because the rendered result cannot be checked from the diff. **Capture before and after wherever a before exists**, using the same viewport and scenario. Use after-only for net-new UI with nothing to compare. Screenshots are this repo's only check of the *rendered* result. On #661 they found the product defect missed by four review seats, three CI Playwright runs and the driver's verification. Capture at 1:1; downscaling hides 1px hairlines and 4–5% alpha shadows. Rebuild first; a long-running sim stack serves its original build, not the branch under review. Attach images with `gh pr comment --attach` or `gh pr create --attach`, **never** by committing them to a branch. Deleting a branch during cleanup silently breaks merged-PR images while the surrounding prose still claims they exist.
- **Runtime verification uses the repo's `verify` skill at [`tools/verify/`](tools/verify/SKILL.md); its tree is agent-agnostic.** It covers launching and doctoring `cluckwork-sim`, driving Playwright fixtures and catalog labels, proof standards, `capture.sh` for the four 1:1 frames required above, and the per-feature map in `tools/verify/features/`. `.claude/skills/verify`, `.codex/skills/verify` and `.agents/skills/verify` are tracked symlinks; every runtime using the Agent Skills layout reads the same file. Edit that tree. The skill enforces one shared sim stack: `reset.sh` runs `down -v` on the one compose project, so never reset while another agent drives it. A running stack serves its original build; the doctor checks that its image came from the checkout under review. Run `/maintain-verification-skill` after a screen slice lands.

## Secrets — never commit

- Put real values in gitignored `deploy/.env`; keep only placeholders in `deploy/.env.example`.
- Local API debug config uses `dotnet user-secrets` (keyed by `UserSecretsId` in `Cluckwork.Api.csproj`).
- No hardcoded passwords/keys in source — GitGuardian scans PRs. Generate test credentials at runtime.

## Host-agnostic repo (deployment boundary)

This repo is **host-portable**: it must build and run on any host without provider-specific configuration. The app reads every environment-specific value from configuration or environment variables and **never names or branches on a hosting provider**.

- **Stays here** (portable operational contract): the Dockerfile and its `HEALTHCHECK`, `deploy/` compose as a local/reference stack, the health probes (`/health/live`, `/health/ready`), the `migrate` / `seed` / `recover-admin` / `healthcheck` verbs, `.env.example`, and docs that state *requirements* ("needs tzdata + ICU", "needs a trusted-proxy list", "needs TLS to Postgres").
- **Does NOT belong here** (goes to a separate deployment/ops repo): provider deploy manifests (`railway.json`, `fly.toml`), IaC, CDN/DNS/edge config, secret-store wiring, provider-named runbooks, and the concrete environment *values* (proxy CIDRs, CA bundles, connection URLs).

Reviewers must flag a hardcoded provider name in code, configuration, or committed documentation like a missing test. Use a provider as a passing prose *example* only when no portable phrasing works; prefer the neutral term.

## Writing a guard (a test that asserts an invariant)

A guard must *fail* when a later change violates an invariant, such as the migration freeze, body-reading endpoint check, or simulation manifest counts. **A wrong guard is worse than none because it looks safe.** #407 spent five review rounds on one. → [`407-writing-a-guard.md`](docs/decisions/407-writing-a-guard.md); in brief:

- **Run a local adversarial pass before the first push** — mutation checks, or a second agent handed the diff and told to *refute* it.
- **Mutation first, claim second**. Never write "this catches X" until the mutation makes the guard red.
- **Two misses of the same shape mean the METHOD is wrong** — prefer "walk everything, exclude deliberately" over "list what I thought of".
- **For a pinned/golden value, prove portability** — repetition on one machine cannot detect environment leakage.
- **Prefer the boring guard** — complexity costs double when the complicated thing is the thing you are trusting.
- **Key a registry by what the code says about itself, never by where it sits (#632).** The tenant-bypass classification once keyed each approved filter-free query by `file:line`. That was precise and fail-closed but moved when SQL did not: #601 re-pinned eight rows, #609/#606 three, and #627 two (`457→469`, `500→512`). Re-pinning is where reviewers start pasting instead of reading. The key is now enclosing symbol + set + a hash of the query's Roslyn TOKENS. Comments are trivia, whitespace between tokens normalizes, and literals remain byte-for-byte. A comment above a query is invisible, but editing the query, including a filter value inside a string, requires re-review. Before adoption, two comment lines made three classifications both unclassified and stale. When a stable key can collide but a positional key could not, make the same guard assert uniqueness; otherwise one identity can excuse two queries. **The tenant-bypass allow-list follows the same rule (#1072).** Each `BypassAllowList` row also carries the same token hash, taken over the whole member its symbol names, because one row excuses every bypass in that member; a local function's row hashes its name and the method around it, whose locals it can capture. A statement scope would miss SQL held in a separate `const`. Editing that member un-excuses its bypasses until a reviewer pastes the new hash printed by the failure. No two rows may share a hash. → [`859-typed-rule-registries.md`](docs/decisions/859-typed-rule-registries.md#allow-list-rows-carry-a-token-hash-1072)
- **When adding a registry entry, find its guards by grepping the registry's READERS, never by recall.** A registry is any list other code walks, such as `CliDispatcher.Commands`, `AuditActions`, or an enum mirrored into `web/src/i18n/enums.ts`. For #534's two verbs, recall found `CliDispatcherTests` and `ProcessRoleRegistryTests` but missed `OneShotVerbMinimalConfigTests`, which rejects a `ProcessRoles.OneShotVerbs` entry without a minimal-config case. `grep -rn "CliDispatcher.Commands\|ProcessRoles.OneShotVerbs" tests/` finds all three. A remembered guard list is the hand-maintained list those guards exist to prevent.
- **Read a call-site SYNTAX guard before authoring the call site.** Its rule is not inferable from the guarded code, and a violation fails the build. `AuditVocabularyCoverageTests` accepts only `AuditActions.X` or a ternary of two such references as the action argument to `IAuditWriter.WriteAsync`. It fails closed otherwise and has one bespoke exemption, `IdentityProvider`'s forwarded parameter, held by three companion assertions. Forwarding the action through a shared private helper therefore fails; #534 caught that before dispatch and used the ternary.
- **A guard that walks every TRACKED file applies to a document as soon as you commit it (#508).** `SchemaDocsTests.PostgresImagePin_IsOneIdenticalStringAcrossEveryTrackedFile` failed when a plan described a probe with bare `postgres:<tag>`. The untracked draft was outside the guard; committing it changed the guard's scope and stopped the implementer near completion. Before committing documentation, run guards that walk tracked files. If such a guard rejects the CONTENT of a copied document, fix the source and copy it again. Never edit the committed copy, which breaks "verbatim," or allow-list it, which weakens a pin guard for a comment.

## Pre-commit hook (opt-in)

`git config core.hooksPath .githooks` enables a ~2s pre-commit hook: unit tests (domain + application) when `.cs`/`.csproj`/`.sln`/`.slnx` files are staged, `npm run typecheck` when `web/` files are staged. Integration tests are deliberately excluded (Docker, slow) — CI is the authority. Skip once with `--no-verify`.

## CI security gates (#146)

CI rejects a PR when a **production** dependency has a known **high+** advisory: NuGet (`dotnet list package --vulnerable`) or npm production dependencies (`npm audit --omit=dev`). Dev-only advisories are logged but do not block. Dependency review, advisory CodeQL, and a weekly audit also run. Both audit gates use `.github/scripts/vuln-gate.mjs` and **fail closed**. The only mute is a dated `.github/security-exceptions.json` entry with an exact GHSA id and required `expires`. → [`146-ci-security-gates.md`](docs/decisions/146-ci-security-gates.md)

- **Container image hardening (#267).** Runtime stage runs non-root (`USER $APP_UID`), and all three base images are digest-pinned. **CI does not scan the image, and no scan gates a PR, a publish or a release.** Trivy scans the *published* image weekly (`image-scan.yml`), files one issue per vulnerability and closes it when a scan no longer finds it; closing an issue by hand ignores that CVE. A newly published vulnerability can go unreported for up to a week, and a release can ship an image nobody has scanned yet; this is the accepted cost of #1006, where a blocking scan stalled every PR and all publishing on a vendor CVE no PR could fix. Keep the full glibc base per #264. → [`267-container-hardening.md`](docs/decisions/267-container-hardening.md)
- **NuGet versions live in `Directory.Packages.props` (#684).** With Central Package Management, every `.csproj` has bare `PackageReference` elements; the root `PackageVersion` list sets versions. A bump changes one file, and projects cannot silently disagree. `CentralPackageFloatingVersionsEnabled` is on for the existing ranges, `10.*` and `1.*`; committed lock files, not ranges, pin restores. `Directory.Build.props` holds `RestorePackagesWithLockFile` and the shared target framework, nullable, implicit-usings and warnings-as-errors properties, never versions. Do not merge the two files. The Dockerfile restore layer, CI drift guard and every path filter name the props file explicitly. Add every new restore input to all of them.
- **NuGet lock files.** Every project has a committed `packages.lock.json` and CI restores `--locked-mode`, so a package add or bump commits the regenerated lock files **in the same commit** or CI fails with `NU1004`. Dependabot NuGet PRs are auto-healed by `.github/workflows/dependabot-lockfix.yml`.

## Git / PR workflow

- `origin` = GitHub (`github.com/mforce/cluckwork`); `gitea` = backup mirror. Use `gh` for PRs.
- **`main` is protected** — work on a branch, push it, and open a PR; never commit to `main`. Use `feat/…`, `chore/…`, `docs/…`, or `spec/…` branch names. PRs squash-merge.
- **A hook refuses forbidden git commands from agents.** `tools/agent-guard/git_guard.py` runs before every Bash call in Claude Code (`.claude/settings.json`) and Codex (`.codex/hooks.json`). A command that mentions `git push`, `git commit` or a PR merge, or could spell one through `$`, a backtick or a glob, must be one simple command, optionally after `cd <dir> &&`; chains, pipes, subshells, `$(…)`, variables and wrappers are refused, not parsed. In that form it blocks `gh pr merge`, a non-GET `gh api` call to `pulls/<n>/merge`, a literal `mergePullRequest` GraphQL call, `gh alias set`, `src:dst` and `+` refspecs, `--force`/`-f`/`--mirror`/`--all`/`--prune`, pushes whose source or configured destination is `main`, and `git commit` on `main`. It does not see gh or git aliases defined earlier, a merge query read from a file, commits made by merge, rebase, cherry-pick or pull, or a Codex `workdir`. `git_guard_test.py` is its table test in CI. Codex skips a new or changed hook until it is trusted in `/hooks`.
- **The PR title is the release note.** It is always the squashed commit subject (`squash_merge_commit_title=PR_TITLE`), which release-please parses for the changelog. A typo or non-conventional prefix silently drops the entry.
- **The version comes from the branch, not from commit types (#351).** Every release from `main` bumps the middle digit (`"versioning": "always-bump-minor"` in `release-please-config.json`); every release from a hotfix line `release/vX.Y.x` bumps the last digit (`always-bump-patch` in that line's own copy). The first digit moves only by hand, with `Release-As: 2.0.0` added **at merge time** (`gh pr merge <N> --squash --body "Release-As: 2.0.0"` or the squash dialog's extended description): squash commits here carry the PR title and an empty body (`squash_merge_commit_message=BLANK`), so a footer in a branch commit or the PR description is dropped. Commit types choose only the changelog section. A `!` in the PR title marks a breaking change there and moves no digit; a `BREAKING CHANGE:` footer is dropped like any other body text.
- **A commit-body parse error drops the whole commit** (no changelog entry, green run). A body reaches `main` only as a description typed at merge time; in one, never start a line with `word(` that has another `(` inside it. `.githooks/commit-msg` checks branch commits, which no longer reach `main`, and no local hook sees a **PR title** or merge-time text.
- **Never hand-edit `.release-please-manifest.json` or `version.txt`** — release-please owns them.
- **A PR closes its issue from the BODY, never from the title.** Put `Closes #NNN` on its own line in the body. A trailing `(#NNN)` in the title is a reference and closes nothing. #742's title named `(#722)` without a body keyword; the owner caught it just before merge approval. Its close-out plan had accepted manual issue closure, the step this convention exists to remove. **Verify linkage against the API, never by re-reading the body**: `gh api graphql -f query='query { repository(owner:"mforce", name:"cluckwork") { pullRequest(number:N) { closingIssuesReferences(first:5) { nodes { number state } } } } }'` returns exactly what will close. `gh pr edit` works on `gh` 2.101.0, verified against this repository. If an older `gh` fails with a Projects-classic GraphQL deprecation error, check `gh --version` first, then patch the body with `gh api -X PATCH repos/<owner>/<repo>/pulls/<N> --input <json-file>`. Where a PR ships work an issue only partly covers, keep the keyword in the body and record the remainder on the issue under the amendment rule below.
- **A PR that changes the UI carries screenshots** — see the rule under [Browser tests, screenshots and verification](#browser-tests-screenshots-and-verification) for the trigger, the before/after expectation and the capture mechanics. Stated here because that is the section people read while opening a PR, and the rule is easy to miss from here.
- **Keep phase epics in sync**: when filing a slice issue, add it to the phase epic's checklist (epic #14 = Phase 1.1, #15 = Phase 1.5); when its PR merges, check it off. Milestone assignment alone is not enough — the epics are how work is navigated.
- **Keep documentation in sync** (owner directive, 2026-07-17): every PR that adds or changes user-visible behavior updates, in the same PR, (1) `specs/product/GLOSSARY.md` when a concept appears or changes meaning, and (2) the SPA Help page + in-app glossary. Treat a missing doc update like a missing test.
- **A PR that ships work another OPEN issue claims amends that issue in the same PR.** Future workers plan, size and route work from the issue body; it does not update itself. #532 shipped all of `AccountSuspensionService`, whose header named #534 as its future caller. #534 still claimed the service's revocation, epoch bump and stamp rotation, so months later it was picked up against mostly obsolete scope. **Leave the body as written for history**. Add a top amendment linking to a comment that names what shipped and where, what remains, and which acceptance criteria the shipped code deliberately does not meet. A criterion nobody will implement needs a follow-up issue or recorded won't-fix, never silent abandonment in a closed issue.

## Phase context

**The SPA egg loop runs end to end (#13):** daily entry (by grade) → submit → egg lots → stock → customer → sales order → FIFO allocation → stock decremented.

**Operational features (#14):** RBAC UI, product catalog / egg-grade management, inventory movement ledger, feed/water/mortality, expenses, payments, dashboard, reports, audit UI, exports and i18n infrastructure. Epic #15 records the follow-on scope.

**Multi-farm tenancy (#530):** several farms coexist on one deployment, sign-in takes a farm code, identity is per-account email, suspension takes effect immediately for use, and operators provision accounts with `provision-account`. The [single-instance section](src/AGENTS.md#deploy-invariant-exactly-one-serving-api-instance-271) explains the shared mechanisms replacing the four in-process blockers and their limits. Related changes: user email editing (#357), flock-scoped reads (#388), and documentation (#537). The decisions and their accepted costs are in [`530-multi-farm-tenancy.md`](docs/decisions/530-multi-farm-tenancy.md).

**Current priorities live in the tracker:** consult [milestones](https://github.com/mforce/cluckwork/milestones) and [open epics](https://github.com/mforce/cluckwork/issues?q=is%3Aissue%20is%3Aopen%20label%3Aepic). Product scope is in [`specs/product/specs.md`](specs/product/specs.md) §6; epic #15 records the egg-product-hardening scope, including legacy import, inventory reconciliation, alert center, packaging inventory, additives/supplements, vaccination records, native-speaker es/tl review, deployment readiness and Phase 1.1 carryover.

Domain terms (flock lifecycle, daily entry states, egg lots, grades, culls, FIFO allocation) are defined in [`specs/product/GLOSSARY.md`](specs/product/GLOSSARY.md) — read it before renaming or modeling anything, and [`docs/architecture.md`](docs/architecture.md) for how those states actually connect.

## graphify

`graphify-out/` contains the project's knowledge graph, including god nodes, community structure, and cross-file relationships. When the user types `/graphify`, follow the installed graphify skill or instructions first.

- For codebase questions, run `graphify query "<question>"` first when `graphify-out/graph.json` exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts — these return a scoped subgraph, usually much smaller than `GRAPH_REPORT.md` or raw grep output.
- Dirty `graphify-out/` files are expected after hooks or incremental updates and are not a reason to skip graphify. Only skip it if the task is about stale graph output, or the user says not to.
- If `graphify-out/wiki/index.md` exists, use it for broad navigation instead of raw source browsing. Read `GRAPH_REPORT.md` only for broad architecture review.
- Run `graphify update .` periodically (AST-only, no API cost) — but not as part of every code change: bundling it into each commit inflates PRs with unrelated changed lines.
