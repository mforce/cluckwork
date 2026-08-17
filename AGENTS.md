# AGENTS.md — Cluckwork

Poultry egg-farm management system. Backend: **.NET 10** (C#), layered DDD. Frontend: **React 19 + Vite** SPA in `web/`. Postgres via EF Core.

This file is the shared brief for any coding agent (Claude Code, Codex, etc.) and
the **canonical rule set** for the repo. Humans usually want the short path first:
[`CONTRIBUTING.md`](CONTRIBUTING.md) (develop, test, commit),
[`docs/`](docs/README.md) (runbooks, decision records, releasing),
[`SECURITY.md`](SECURITY.md).

**Every rule here is one paragraph.** A rule that carries a `→` link was earned
by a defect that shipped, and the narrative — what broke, which review round
found it, what the wrong fix was — is behind that link in
[`docs/decisions/`](docs/decisions/): **follow it before changing the rule.** A
rule with no link is a plain convention that has not yet cost anything; it needs
consistency, not archaeology.

- [Communicating](#communicating) · [Layout](#layout) · [Build / test / run](#build--test--run)
- [Conventions](#conventions-follow-these) — the rules that break things when ignored
- [Secrets](#secrets--never-commit) · [Host-agnostic repo](#host-agnostic-repo-deployment-boundary) · [One serving instance](#deploy-invariant-exactly-one-serving-api-instance-271-338)
- [Writing a guard](#writing-a-guard-a-test-that-asserts-an-invariant) · [Pre-commit hook](#pre-commit-hook-opt-in) · [CI security gates](#ci-security-gates-146)
- [Releases](#releases-and-image-publishing-351) · [Git / PR workflow](#git--pr-workflow) · [Phase context](#phase-context) · [graphify](#graphify)

## Communicating

Keep explanations clear and human-sounding. Provide concise, focused responses. Skip preambles and recaps — lead with the action or answer.

## Layout

```
src/
  Cluckwork.Domain          aggregates, value objects, domain events (no deps)
  Cluckwork.Application      feature handlers, repository interfaces, validators
  Cluckwork.Infrastructure   EF Core, Identity/JWT, repositories, seeding, jobs
  Cluckwork.Api             minimal-API endpoints, middleware, Program.cs
web/                        React/Vite SPA (see web/README.md)
deploy/                     docker-compose (.yml prod, .dev.yml dev DB), .env.example
specs/                      product + technical specs, wireframes
tests/                      Domain.Tests, Application.Tests, Api.IntegrationTests
```

Dependencies point inward: Api → Application/Infrastructure → Domain. Domain depends on nothing.
The **request pipeline order** and the **egg-loop state machine** are drawn in
[`docs/architecture.md`](docs/architecture.md) — read it before moving middleware
or adding an aggregate state.

## Build / test / run

```bash
dotnet build Cluckwork.sln                 # warnings are errors — keep it clean
dotnet test  Cluckwork.sln                 # 688 tests as of 2026-07; integration needs Docker
```

- **Integration tests** spin up a real Postgres via Testcontainers (`docker` required). No SQLite — EF SQL semantics differ.
- **Run full stack (prod-like):** `docker compose -f deploy/docker-compose.yml up --build` → SPA + API on http://localhost:8080 (single container; API serves the built SPA from `wwwroot`).
- **Run frontend dev:** `cd web && npm run dev` → http://localhost:5173 (proxies `/api` → :8080).
- **Debug API (no docker stack):** `docker compose -f deploy/docker-compose.dev.yml up -d` (Postgres on :5432), then run/debug `Cluckwork.Api` (Development env). Dev secrets live in **user-secrets**, not files.

## Conventions (follow these)

### Application shape

- **Result pattern:** domain/handlers return `Result` / `Result<T>` (see `Domain/Common`). Don't throw for expected failures; throw only for invariant violations (e.g. `Flock.Create` guards).
- **Handler per feature**, invoked directly from endpoints — **no MediatR**. Register handlers/validators/repos in `Program.cs`.
- **Validation:** FluentValidation validators (`*Validator`), one per command; endpoints call `ValidateAsync` and return `ValidationProblem`.
- **Endpoints:** minimal APIs grouped under `/api/v1/...` via `Map<Feature>Endpoints`; writes require auth + an `Idempotency-Key` (middleware).
- **Nullable enabled**, no unused usings — both are build-breaking.

### Data and correctness

- **Every aggregate mutation must bump `Version`.** `Version` is an EF concurrency token (`IsConcurrencyToken`): EF puts the *original* value in the UPDATE's `WHERE` but never auto-increments it, so a mutation without `Version++` silently loses concurrent races — both writers match `WHERE Version = N` — instead of 409ing. This shipped three times; each fix carries a parallel-race integration test, and so must any new mutation.
- **Multi-tenancy:** every tenant-owned entity has `AccountId`, enforced by an EF **global query filter** plus a **`TenantStampInterceptor`** (stamps on insert). `TenantContext` resolves per-request from the JWT `account_id` claim; at startup it is unresolved, so seeders use `IgnoreQueryFilters()`.
- **Transient-DB retry stops at unreplayable work (#269).** `EnableRetryOnFailure` covers self-contained EF units only; an automatic replay above a stateful detector (a counter, a CAS stamp, a single-use claim) cannot tell "this request racing itself" from the signal the detector exists to catch. Two cures, and picking wrong ships the bug: `SingleAttemptExecution` when the replay is itself observable, a durability probe on a self-minted token when the replay writes nothing. → [`269-transient-db-retry-boundary.md`](docs/decisions/269-transient-db-retry-boundary.md)
- **`AuditEvents` is not time-partitioned, on purpose (#505).** The dominant read filters on `AccountId`+`EntityType`+`EntityId` with no date predicate, so monthly partitions would turn one index lookup into one per partition for no pruning benefit. If it is ever needed, partition by `AccountId`. → [`505-audit-events-no-time-partition.md`](docs/decisions/505-audit-events-no-time-partition.md)

### Migrations and schema

- **`InitialCreate` is frozen; one migration per change (#407).** EF never re-runs an applied migration, so a column hand-folded into `InitialCreate` **silently does not exist** on any booted database — it surfaces as broken behaviour, not as a migration error. `InitialCreate` also carries un-regenerable expression indexes and the base-reference SQL; regenerating desynchronises `__EFMigrationsHistory` everywhere. Pre-#407 dev databases cannot migrate forward — drop and recreate. → [`407-migration-freeze.md`](docs/decisions/407-migration-freeze.md)
- **Base reference data ships as guarded raw-SQL migrations (#283).** The default account, four assignable roles, default egg grades and packed-unit conversions are `migrationBuilder.Sql` with `WHERE NOT EXISTS` guards — **never `HasData`/`InsertData`**, which key on the PK and emit `UpdateData`/`DeleteData` that silently reverts the farm's own edits. Grades guard whole-set (they are user-renamable); roles, conversions and account guard per-key. → [`283-migrations-base-provisioning.md`](docs/decisions/283-migrations-base-provisioning.md)
- **Schema docs are generated, committed, and regenerated with every migration (#417).** `docs/schema/` comes from `tools/schema-docs/generate.sh`; CI's `build-and-test` runs `generate.sh --check` and fails a stale PR. Never hand-edit them, and resolve a post-rebase conflict there by regenerating. → [`417-schema-docs.md`](docs/decisions/417-schema-docs.md)
- **The design-time connection is fail-closed (#318).** `AppDbContextDesignTimeFactory` has no default: an unset `CLUCKWORK_MIGRATIONS_CONNECTION` throws, and every target meets the same TLS floor as a Production boot, except an explicitly acknowledged loopback via `CLUCKWORK_MIGRATIONS_ALLOW_INSECURE_LOOPBACK=true`. → [`318-design-time-migration-connection.md`](docs/decisions/318-design-time-migration-connection.md)

### Auth and credentials

- **Auth:** asymmetric JWT + rotating refresh tokens. PEM keys come from config with escaped `\n`; normalize via `PemKey.Normalize` before `ImportFromPem`. Integration tests generate an ephemeral RSA pair at test-process startup (`TestJwtKeys` in `CluckworkWebApplicationFactory.cs`) — no real key material is ever committed.
- **Both JWT keys are checked at boot, and the check is serving-only (#510/#347).** `AddCluckworkIdentity` requires both keys non-blank **and importable**; use `IsNullOrWhiteSpace`, never `??` (the shipped `appsettings.json` carries `""`, which `??` does not catch), and import at boot rather than inside the `AddJwtBearer` delegate (which makes a corrupt key a per-request 500 behind a green health check). → [`510-jwt-key-boot-check.md`](docs/decisions/510-jwt-key-boot-check.md)
- **Credential epoch revocation (#364).** Every access/refresh token is bound to the user's `CredentialEpoch` and every password-reset path bumps it. Epoch `0` is permanently retired, a missing or malformed claim is **always** a mismatch, and `CredentialEpochMiddleware` does a fresh DB read per authenticated request — the round trip *is* the fail-closed guarantee, so do not cache it. **Break it and a revoked credential keeps working.** → [`364-credential-epoch-revocation.md`](docs/decisions/364-credential-epoch-revocation.md)
- **First-run admin: `bootstrap-admin` (#283).** Creates an Owner with a generated password (stdout only, never the logger/OTLP) and `MustChangePassword=true`, **only** if the default account has no Owner; a re-run is a silent no-op. While the flag is set, `MustChangePasswordMiddleware` 403s everything except `auth/change-password` and `auth/logout`. → [`283-first-run-admin-provisioning.md`](docs/decisions/283-first-run-admin-provisioning.md)
- **Break-glass: `recover-admin` (#265).** Same run-then-exit shape as `seed`, but deliberately **not** environment-gated — it must work against a real Production database. One transaction: freshly generated temp password (never one passed on the command line), rotated security stamp, every refresh token revoked, and a `User.BreakGlassReset` audit row carrying `--reason`. → [`265-break-glass-recovery.md`](docs/decisions/265-break-glass-recovery.md) · [runbook](docs/runbooks/break-glass-account-recovery.md)
- **Nothing writes an audit event without an actor (#500).** `AuditWriter` throws on an unresolved `ICurrentUser`; system callers declare a `SystemActors` identity and both seeders require an Owner. `ICurrentUser` is an **authorization input, not a label** — `FlockScopeGuard` reads its roles, so an actor built from a literal rather than `UserManager.GetRolesAsync` can fail an entire seed. → [`500-audit-actor.md`](docs/decisions/500-audit-actor.md)

### Boot guards and process roles

- **Process role, not statement order (#347).** A boot guard's scope is declared, never positional: `ProcessRoles.From(args)` computes `Serving | OneShot` once, before the host is built, and every role-scoped guard takes it. Gating on where a statement sits is what #331 was — a validation running at service registration killed `recover-admin` with SIGABRT 134. **Scope the whole subsystem, not the one setting that bit you**, and give every serving-only guard a row in `ProcessRoleGuardTests.ServingOnlyGuards` — one per *violation*, not per subsystem. → [`347-process-role.md`](docs/decisions/347-process-role.md)
- **Proxy-trust boot guard (#260).** HSTS (#144) and the per-IP login limiter (#143) only work if the app trusts the proxy's `X-Forwarded-*`, which it does only for networks in `RateLimiting:TrustedProxies` — so an empty list in Production fails the boot rather than running with inert HSTS and a one-bucket limiter. Opt out with `RateLimiting:AllowNoTrustedProxies=true` only for a direct-TLS deploy with no fronting proxy. → [`260-proxy-trust.md`](docs/decisions/260-proxy-trust.md)
- **Production Postgres TLS floor (#261/#262).** In Production the effective `sslmode` is a fail-closed allow-list: `VerifyCA`/`VerifyFull` silent, `Require` warns, **everything else — including unset — fails the boot**. `Database:AllowInsecureConnection=true` is the explicit opt-out for the co-located plaintext compose stack. When mapping a libpq param, a missing keyword usually means it is spelled differently in Npgsql (`keepalives`→`Tcp Keepalive`), not that it is unmappable. → [`261-postgres-tls-floor.md`](docs/decisions/261-postgres-tls-floor.md)
- **GSS/Kerberos negotiation off by default (#332).** `PostgresConnectionString` appends `GSS Encryption Mode=Disable` unless the operator set it (detected by **presence, not value**), textually, after the TLS floor runs — a round-trip through the Npgsql builder would reorder the operator's string and throw on any keyword this version does not know. → [`332-gss-kerberos.md`](docs/decisions/332-gss-kerberos.md)
- **Farm timezone + tzdata/ICU (#264).** A farm sets its IANA zone in Settings after first login, not at provisioning time. The clock resolves zones via `TimeZoneInfo.FindSystemTimeZoneById` and fails closed, so the runtime image **must** carry tzdata + ICU — never an Alpine/chiseled base, never `InvariantGlobalization=true`. `TimeZoneAvailability.EnsureResolvable` asserts a canary at boot for **both** process roles. → [`264-farm-timezone.md`](docs/decisions/264-farm-timezone.md)
- **A new Production boot guard must be taught to the sim harness (#370).** Every guard that fails the boot on missing config, and every config-key add/rename/retire, updates `tools/simulation/bootstrap.sh`, `docker-compose.sim.yml` and `verify-harness.sh` **in the same PR** — that harness runs Production config on purpose, is deliberately not in CI, and nothing tells you when you break it. Satisfy guards properly, never by disabling one. → [`370-sim-harness-boot-guards.md`](docs/decisions/370-sim-harness-boot-guards.md)

### Operations and callers

- **Migrate command + prod migration split (#263).** `dotnet Cluckwork.Api.dll migrate` applies migrations then exits — the pre-deploy-job entrypoint. Production sets `Database:MigrateOnStartup=false`, so the serving process never runs DDL; the guarantee is **ordering**, and `DatabaseReadyHealthCheck` is the backstop (`/health/ready` 503s while any migration is pending). → [`263-migrate-command.md`](docs/decisions/263-migrate-command.md)
- **Container health probe: the `healthcheck` verb (#266).** Dispatched before host build (no DI, DB or config), it GETs `/health/ready` over loopback and exits `0` only on a 2xx — the hardened image ships no curl/wget, and the probe must never report a false green. → [`266-container-health-probe.md`](docs/decisions/266-container-health-probe.md)
- **Container image hardening (#267).** Runtime stage runs non-root (`USER $APP_UID`), all three base images are digest-pinned, and Trivy fails the build on a fixable HIGH/CRITICAL. Keep the full glibc base per #264. → [`267-container-hardening.md`](docs/decisions/267-container-hardening.md)
- **Seed / simulation data is never boot-seeded (#280, #284, #279).** Run it explicitly against an already-base-seeded, non-Production database: `ASPNETCORE_ENVIRONMENT=Development dotnet Cluckwork.Api.dll seed --profile demo|simulation` (unset env → Production → blocked). The verb migrates, seeds, exits — authoritative and fail-loud. Both profiles require an Owner (#500). → [`280-seed-and-simulation.md`](docs/decisions/280-seed-and-simulation.md)
- **A write-contract change must update its non-CI callers (#394).** Coverage is not uniform: the seeders are covered only to the handler layer, so a **validator-only** tightening is invisible to every seeder test, and `tools/simulation/k6/` plus the Playwright specs are uncovered while a green baseline hides it. **Verify the request/status contract by reading the callers, not by running.** → [`394-write-contract-callers.md`](docs/decisions/394-write-contract-callers.md)
- **SPA E2E lives in `tools/simulation/ui/` (#277/#385).** Playwright drives the real built SPA over the same `seed --profile simulation` fixture k6 uses; `web/` stays Vitest. Three enforced rules, each of which has caught something: never hardcode a credential, never hardcode English, respect the farm clock. Anything reasoning about `inert` or the accessibility tree must go through CDP (`src/ax.ts`) — Playwright's own APIs do not model `inert` (#501). → [`277-spa-e2e.md`](docs/decisions/277-spa-e2e.md)
- **Production logs: compact JSON on stdout; base layer argument-free (#404).** The base `appsettings.json` Console entry carries `Name` only — a template left beside the formatter is **silently** bound instead of it. Trace fields differ by format (`@tr`/`@sp` in compact JSON, `{TraceId}`/`{SpanId}` in the Dev template), and compact JSON serializes **every** property, so anything pushed via `BeginScope`/`LogContext` reaches the collector. → [`404-production-logs.md`](docs/decisions/404-production-logs.md)

## Secrets — never commit

- `deploy/.env` is gitignored (real values). `deploy/.env.example` holds placeholders only.
- Local API debug config uses `dotnet user-secrets` (keyed by `UserSecretsId` in `Cluckwork.Api.csproj`).
- No hardcoded passwords/keys in source — GitGuardian scans PRs. Generate test credentials at runtime.

## Host-agnostic repo (deployment boundary)

This repo is **host-portable** — it must build and run against any host without carrying provider-specific config. The app reads every environment specific from config/env and **never names or branches on a hosting provider**.

- **Stays here** (portable operational contract): the Dockerfile and its `HEALTHCHECK`, `deploy/` compose as a local/reference stack, the health probes (`/health/live`, `/health/ready`), the `migrate` / `seed` / `recover-admin` / `healthcheck` verbs, `.env.example`, and docs that state *requirements* ("needs tzdata + ICU", "needs a trusted-proxy list", "needs TLS to Postgres").
- **Does NOT belong here** (goes to a separate deployment/ops repo): provider deploy manifests (`railway.json`, `fly.toml`), IaC, CDN/DNS/edge config, secret-store wiring, provider-named runbooks, and the concrete environment *values* (proxy CIDRs, CA bundles, connection URLs).

Reviewers: treat a hardcoded provider name in code, config, or a committed doc like a missing test — flag it. Naming a provider as a passing *example* in prose is tolerable only when no portable phrasing works; prefer the neutral term.

### Deploy invariant: exactly ONE serving API instance (#271, #338)

**Run one serving instance.** Every instance runs `DurableJobWorker`, and its poll claims nothing — no `FOR UPDATE SKIP LOCKED`, no lease, no advisory lock. Four independent blockers must close before scaling, and they are not all in #271: background work has no single-runner guarantee (**#271**); `IStepUpGrantRegistry` is process-local, so a single-use step-up grant becomes usable once *per replica* (**#338** — the blocker with teeth); the IP-keyed auth limiters (#143) are in-process; and the per-account report concurrency cap (#311) is in-process. **#307 is closed and does not license scaling.**

**Do not extend that list from memory** — it was twice derived wrongly, both times a process-local limiter. Re-derive it by walking every `AddSingleton`/`AddHostedService` under `src/` plus every in-memory state primitive, then excluding deliberately. The run-then-exit verbs are unaffected: they never start hosted services. → [`271-single-serving-instance.md`](docs/decisions/271-single-serving-instance.md)

## Writing a guard (a test that asserts an invariant)

A guard is a test whose job is to *fail* when someone later does the wrong thing — the migration freeze, the body-reading endpoint check, the simulation manifest's exact counts. **A wrong guard is worse than no guard, because it reads as safety.** #407 spent five review rounds on one. → [`407-writing-a-guard.md`](docs/decisions/407-writing-a-guard.md); in brief:

- **Run a local adversarial pass before the first push** — mutation checks, or a second agent handed the diff and told to *refute* it.
- **Mutation first, claim second** — never write "this catches X" before running the mutation that makes the guard go red.
- **Two misses of the same shape mean the METHOD is wrong** — prefer "walk everything, exclude deliberately" over "list what I thought of".
- **For a pinned/golden value, prove portability** — repetition on one machine cannot detect environment leakage.
- **Prefer the boring guard** — complexity costs double when the complicated thing is the thing you are trusting.

## Pre-commit hook (opt-in)

`git config core.hooksPath .githooks` enables a ~2s pre-commit hook: unit tests (domain + application) when `.cs`/`.csproj`/`.sln` files are staged, `npm run typecheck` when `web/` files are staged. Integration tests are deliberately excluded (Docker, slow) — CI is the authority. Skip once with `--no-verify`.

## CI security gates (#146)

CI fails a PR when a **production** dependency carries a known **high+** advisory — NuGet (`dotnet list package --vulnerable`) and npm prod deps (`npm audit --omit=dev`; dev-only advisories are logged, not blocking). Plus dependency-review, CodeQL (advisory), and a weekly scheduled audit. Both audit gates run through `.github/scripts/vuln-gate.mjs` and **fail closed**; the only mute is a dated `.github/security-exceptions.json` entry (exact GHSA id, required `expires`). → [`146-ci-security-gates.md`](docs/decisions/146-ci-security-gates.md)

- **NuGet lock files.** Every project has a committed `packages.lock.json` and CI restores `--locked-mode`, so a package add or bump commits the regenerated lock files **in the same commit** or CI fails with `NU1004`. Dependabot NuGet PRs are auto-healed by `.github/workflows/dependabot-lockfix.yml`.
- **Pin third-party Actions to a full commit SHA** with a trailing `# vX.Y.Z` comment — never a mutable tag (the 2026-03 `aquasecurity/trivy-action` and 2025-03 `tj-actions/changed-files` compromises both retargeted tags). `actions/*` and `github/*` may keep major-version tags.

## Releases and image publishing (#351)

Two stages, deliberately separate: **CI publishes an image per merge; the release PR turns one into a version.** [`docs/releasing.md`](docs/releasing.md) is the **how-to**; this section is the **invariants**; the full mechanism is in [`351-releases.md`](docs/decisions/351-releases.md).

- **Every merge to `main`** publishes `ghcr.io/<owner>/<repo>:sha-<commit>` from the `publish` job. **Merging the "Release vX.Y.Z" PR** drafts the release, **promotes** that commit's image to `:vX.Y.Z`, then publishes.
- **Promotion is a server-side retag of the existing digest** (`--prefer-index=false` is load-bearing; the default wraps a new top-level digest), **never a rebuild** — a rebuild yields different bytes no scan ever examined.
- **Promotion reads the digest from CI's own run artifact**, never by resolving `:sha-<commit>`, which is mutable in the public window between merge and CI's push. Adding a CI job that should gate a release? **Add it to `publish.needs`** — that list is exactly what the digest artifact proves.
- **The release stays a draft until its image is promoted**; GitHub withholds the git tag for a draft, so a failed promotion leaves no version pointing at nothing.
- **The version comes from conventional commits, damped below 1.0.0.** A *breaking* commit bumps the **minor**, and breaking means **either** form — a `!` after the type (`feat!:`, `fix!:`) **or** a `BREAKING CHANGE` footer. Everything else, `feat:` included, is a **patch**. The damping is `bump-minor-pre-major` + `bump-patch-for-minor-pre-major` in `release-please-config.json`, so the mapping flips **silently at 1.0.0** — reach it deliberately with a `Release-As:` footer.
- **A commit-body parse error drops the whole commit** (no changelog entry, no bump, green run): never start a line with `word(` that has another `(` inside it. `.githooks/commit-msg` catches the body, but no local hook sees a **PR title** — and on a multi-commit PR the title *is* the release note.
- **The release PR is opened with a GitHub App token, not `GITHUB_TOKEN`.** Every App consumer **must** keep `permission-*` downscoping — omitting it mints the union of every grant the App holds, silently.
- **Never hand-edit `.release-please-manifest.json` or `version.txt`** — release-please owns them.
- **Deploy by digest, never by tag.** *Obtaining* the digest and *verifying* its origin are two separate problems: get it from the release's `image.json` asset, verify with `gh attestation verify` (all three of `--bundle-from-oci`, `--signer-workflow`, `--source-ref` are load-bearing and none is the default), then confirm the tag still resolves to the digest you verified — comparing against `reference`, **never** the asset's separate `digest` field. Full commands: [`docs/releasing.md`](docs/releasing.md#deploying).

  Net, stated at exactly the strength the argument supports: the internal gate
  fails closed for a leaked **registry** credential. The external gate also
  stops a **branch push** substituting its own bytes. Neither stops a branch
  writer swapping in *other* attested bytes — the tag/digest comparison above
  raises the cost, but that actor holds registry write too, so nothing in this
  repo closes it; branch/dispatch permissions and immutable tags do.
  **And neither survives a merge to `main`.** Once a backdoored `ci.yml` is
  the definition on `main`, its attestation is genuinely valid — right signer
  workflow, right source ref — because `--source-ref` records *which ref built
  this*, not *whether that ref's content is trustworthy*. This repo allows a
  self-merge (`main` requires a PR but **zero** approving reviews), so that path
  is open today and no flag on the verify command closes it; review of changes to
  `main` is the only control that does.

  **The paragraph directly above is the canonical statement of the boundary.** [`docs/releasing.md`](docs/releasing.md) and the `ci.yml` comment carry a summary and point here rather than restating it, because successive corrections to this claim repeatedly updated one copy and left the others contradicting it. If you correct it, correct it in all three and check they agree.
- Package visibility and the host's pull credential are **deploy-side** concerns (cluckwork-deploy#6), not this repo's.

## Git / PR workflow

- `origin` = GitHub (`github.com/mforce/cluckwork`); `gitea` = backup mirror. Use `gh` for PRs.
- **`main` is protected** — branch, push, open a PR; don't commit to `main`. Branch names: `feat/…`, `chore/…`, `docs/…`, `spec/…`. PRs squash-merge.
- **The PR title is the release note.** It becomes the squashed commit subject, which release-please parses for both the changelog and the version bump — so a typo'd or non-conventional prefix silently costs a bump.
- Only commit/push when the human asks.
- **Keep phase epics in sync**: when filing a slice issue, add it to the phase epic's checklist (epic #14 = Phase 1.1, #15 = Phase 1.5); when its PR merges, check it off. Milestone assignment alone is not enough — the epics are how work is navigated.
- **Keep documentation in sync** (owner directive, 2026-07-17): every PR that adds or changes user-visible behavior updates, in the same PR, (1) `specs/product/GLOSSARY.md` when a concept appears or changes meaning, and (2) the SPA Help page + in-app glossary. Treat a missing doc update like a missing test.

## Phase context

**Phase 1.0 (MVP) is shipped** — epic #13 closed. The egg loop runs end-to-end from the SPA: daily entry (by grade) → submit → egg lots → stock → customer → sales order → FIFO allocation → stock decremented.

**Phase 1.1 (Operational fill) is shipped** — epic #14 closed 2026-08-11: RBAC UI, product catalog / egg-grade management, inventory movement ledger, feed/water/mortality, expenses, payments, dashboard, reports, audit UI, exports, i18n infrastructure. Follow-on work discovered while shipping it moved to epic #15.

**Current phase: 1.5** (epic #15, `specs/product/specs.md` §6) — egg product hardening: legacy import, inventory reconciliation, alert center, packaging inventory, additives/supplements, vaccination records, native-speaker es/tl review, deployment readiness, and the Phase 1.1 carryover items on the epic.

Domain terms (flock lifecycle, daily entry states, egg lots, grades, culls, FIFO allocation) are defined in [`specs/product/GLOSSARY.md`](specs/product/GLOSSARY.md) — read it before renaming or modeling anything, and [`docs/architecture.md`](docs/architecture.md) for how those states actually connect.

## graphify

This project has a knowledge graph at `graphify-out/` with god nodes, community structure, and cross-file relationships. When the user types `/graphify`, use the installed graphify skill or instructions before doing anything else.

- For codebase questions, run `graphify query "<question>"` first when `graphify-out/graph.json` exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts — these return a scoped subgraph, usually much smaller than `GRAPH_REPORT.md` or raw grep output.
- Dirty `graphify-out/` files are expected after hooks or incremental updates and are not a reason to skip graphify. Only skip it if the task is about stale graph output, or the user says not to.
- If `graphify-out/wiki/index.md` exists, use it for broad navigation instead of raw source browsing. Read `GRAPH_REPORT.md` only for broad architecture review.
- Run `graphify update .` periodically (AST-only, no API cost) — but not as part of every code change: bundling it into each commit inflates PRs with unrelated changed lines.
