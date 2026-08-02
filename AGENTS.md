# AGENTS.md — Cluckwork

Poultry egg-farm management system. Backend: **.NET 10** (C#), layered DDD. Frontend: **React 19 + Vite** SPA in `web/`. Postgres via EF Core.

This file is the shared brief for any coding agent (Claude Code, Codex, etc.).

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

- **Result pattern:** domain/handlers return `Result` / `Result<T>` (see `Domain/Common`). Don't throw for expected failures; throw only for invariant violations (e.g. `Flock.Create` guards).
- **Every aggregate mutation must bump `Version`.** `Version` is an EF concurrency token (`IsConcurrencyToken`) — EF puts the *original* value in the UPDATE's WHERE but never auto-increments it. A mutation without `Version++` silently loses concurrent races (both writers match `WHERE Version = N`) instead of 409ing. This bug shipped three times (`RecordProduction` was correct; `Submit`/`Lock` and `SalesOrder.AddItem` weren't) — each now has a parallel-race integration test; add one for any new mutation.
- **Handler per feature**, invoked directly from endpoints — **no MediatR**. Register handlers/validators/repos in `Program.cs`.
- **Validation:** FluentValidation validators (`*Validator`), one per command; endpoints call `ValidateAsync` and return `ValidationProblem`.
- **Endpoints:** minimal APIs grouped under `/api/v1/...` via `Map<Feature>Endpoints`; writes require auth + an `Idempotency-Key` (middleware).
- **Multi-tenancy:** every tenant-owned entity has `AccountId`; enforced by an EF **global query filter** + a **`TenantStampInterceptor`** (stamps on insert). `TenantContext` is resolved per-request from the JWT `account_id` claim. At startup it's unresolved → use `IgnoreQueryFilters()` in seeders.
- **Auth:** asymmetric JWT + rotating refresh tokens. PEM keys come from config with escaped `\n`; normalize via `PemKey.Normalize` before `ImportFromPem`. Integration tests generate an ephemeral RSA pair at test-process startup (`TestJwtKeys` in `CluckworkWebApplicationFactory.cs`) — no real key material is ever committed to source (`deploy/.env.example` carries PEM armor with a `replace-me` body, not a usable key).
- **Migrations + base provisioning (#283):** EF migrations auto-apply on startup (`Database:MigrateOnStartup`, default true — but **Production sets it `false`**; the `migrate` command runs DDL out-of-band, see the #263 bullet below). The default account, the four assignable roles (`Roles.Assignable`), the default egg grades, and the default packed-unit conversions are **static reference data baked into the migrations themselves** — deterministic and multi-instance-safe by construction, **no runtime seeder, no `Seed:*` config**. They ship as **hand-written `migrationBuilder.Sql` with `WHERE NOT EXISTS` guards, deliberately NOT EF's `HasData`/`InsertData`** — `HasData` keys on the PRIMARY KEY, which assumes a virgin schema, but every install that ever booted pre-#283 already has these rows under *different* ids (the old `DatabaseSeeder` minted roles/grades/conversions with `Guid.NewGuid()`), so `InsertData` either collides on `PK_Accounts` or silently violates the real natural-key unique indexes. **Do not "regenerate" these into `HasData`** — that reintroduces the collision. The guard's scope differs per batch, and the rule is *whether the user can change the natural key or remove a set member*: the **egg grades** batch is gated on the account's grade catalog being **entirely empty** (whole-set), because grades are user-managed — `PUT /api/v1/egg-grades/{id}` renames one, and a per-name guard cannot see a `Small` the farm renamed to `Pullet`, so it would resurrect an active, saleable duplicate (the old seeder skipped the whole set for exactly this reason); **roles, unit conversions, and the default account** stay **per-key**, because their keys are not user-mutable (no role CRUD exists at all; `EggUnitConversion.Update` cannot change `UnitCode` and there is no create/delete; the account's key is a fixed id), and per-key additionally lets a later-added role or unit code back-fill into an older install. `MigrationUpgradePathTests` pins all of this — including that a farm which renamed a seeded grade gains no duplicate. `MigrationSecurityReviewTests` asserts no migration ever inserts a credential-shaped row (`AspNetUsers`, `PasswordHash`, `SecurityStamp`, …) — the schema itself is never a place a credential can hide. The first admin is a **separate, deliberately NOT migration-baked** concern — see the #283 bullet below.
- **Seed command (#280, #284):** demo sample data is NOT boot-seeded — there is no `Seed__Demo` flag anymore. Run it explicitly, against a database that has **already been booted once** so the base seed above exists (default account, `Admin` role, default egg grades — the demo seed needs all three and fails fast with a clear message if they're missing): `ASPNETCORE_ENVIRONMENT=Development dotnet Cluckwork.Api.dll seed --profile demo` (an unset `ASPNETCORE_ENVIRONMENT` defaults to Production, which the guard below blocks — always set it for a local/dev/CI run). The command migrates the schema, runs `DemoDataSeeder`, then exits (Kestrel never starts); it is **authoritative** (seeds regardless of any config) and **fail-loud** (a real exit code: `0` only when data was actually seeded or was already present, non-zero with an stderr message otherwise — never a silent no-op). `DemoDataSeeder` is registered only when `!IsProduction()` (defense-in-depth): running the command against a Production-env process fails with a clear message instead of writing fake data. Intended flow: the serving process for a real farm stays Production; a non-Production process (dev box, CI, or a sim/load-test harness) runs `seed --profile demo` against its own already-base-seeded database. The **`simulation` profile** (#279) sits in the same `switch` with the same authoritative + fail-loud + Production-guarded contract (`SimulationDataSeeder`); it additionally records its date anchor + a completion marker in a durable `SimulationSeedState` row (anchor written before the first fixture mutation; completion stamped only after the manifest succeeds). A clean re-run — even one straddling a UTC-midnight rollover — reuses that anchor and converges to `AlreadySeeded` with an unchanged manifest fingerprint instead of writing a shifted duplicate set; `AlreadySeeded` is reported only when the counts are unchanged, so a `SimulationOptions` change that adds fixtures re-runs as `Seeded`. The durable anchor can't be poisoned by a mid-seed crash or by foreign daily entries a load test writes into the account (a data-derived anchor could); but a polluted account still fails the exact count validation **closed** (`Failed`) rather than certifying a fixture that no longer matches the definition.
- **Break-glass recovery (#265):** `recover-admin` is a second one-off CLI verb on the same binary, same run-then-exit shape as `seed` — but deliberately **NOT** environment-gated (it must work against a real Production database): `dotnet Cluckwork.Api.dll recover-admin --email <e> [--account <guid>] [--reason <text>]`. For a locked-out account (the sole-Owner-lost-password case, no email/SMTP reset path) it, in one transaction, resets to a **freshly generated** temporary password (never one passed on the command line), rotates the security stamp, revokes every refresh token, and writes a conspicuous `User.BreakGlassReset` audit row carrying `--reason`. The temp password is printed to **stdout only** (never the logger/OTLP) and exit `0`; failures go to stderr with exit `1` and change nothing. `AdminRecoveryService` orchestrates; `IdentityProvider.BreakGlassResetAsync` shares the reset/revoke core with `SetUserPasswordAsync`. Full procedure + verification drill: `docs/runbooks/break-glass-account-recovery.md`.
- **First-run admin provisioning (#283):** `bootstrap-admin` is a fourth one-off CLI verb, same run-then-exit shape as `seed`/`migrate`, always available in Production (same posture as `recover-admin`): `dotnet Cluckwork.Api.dll bootstrap-admin --email <e>`. It migrates the schema (idempotent, like the other verbs), then — **only if the default account has no Owner yet** — creates one with a **freshly generated** password and `ApplicationUser.MustChangePassword = true`, printed to **stdout only** (never the logger/OTLP, identical rule to `recover-admin`). A re-run against an already-provisioned account is a silent no-op: no second Owner, no password reprinted. `FirstRunAdminService` orchestrates (mirrors `AdminRecoveryService`'s CLI-wrapper-calls-a-service shape); `IIdentityProvider.CreateUserAsync` gained an optional `mustChangePassword` parameter (default `false` — an ordinary Users-page-created user is never gated) that this is the only caller to pass `true`. While the flag is set: the JWT carries a `must_change_password` claim (`JwtTokenService`), `MustChangePasswordMiddleware` refuses **every** endpoint except `auth/change-password` and `auth/logout` with 403 (placed before `UseAuthorization`, so it applies uniformly regardless of an endpoint's `AuthPolicies` tier, and before the idempotency middleware, so a blocked write burns no key), and the SPA's `ProtectedRoute` renders a **"Set your password"** screen (`SetPasswordPage`, reusing `/auth/change-password` — the operator already knows the printed password as their "current" one) instead of the app shell, on every route. Any successful password reset (self-service change, an Owner's `SetUserPasswordAsync`, or break-glass) clears the flag — a single invariant in `IdentityProvider`, not re-derived per path. **Deliberately a separate credential type** from `recover-admin`'s temp password and from #308's browser step-up re-confirmation grant — different audience (pre-auth shell access vs. an authenticated Owner), different lifetime, never conflated; do not treat any of #283/#265/#308 as covering another.
- **Farm timezone + tzdata/ICU image constraint (#264):** the default account (a fixed `"UTC"` migration literal since #283 — see the bullet above) is provisioned once; a real farm sets its actual IANA zone via **Settings → timezone** after first login (`Account.UpdateSettings`), not at provisioning time (the `Seed:TimeZoneId` config lever from before #283 is retired along with the runtime seeder it fed). The farm clock resolves IANA zones via `TimeZoneInfo.FindSystemTimeZoneById` for every safety-sensitive local-day decision and **fails closed**, so the runtime **image MUST carry tzdata + ICU** (the Ubuntu 24.04 (Noble) `aspnet:10.0` base does; **never** an Alpine/chiseled base without tzdata, and **never** `InvariantGlobalization=true`). `Program.cs` still asserts a representative IANA canary resolves at boot (`TimeZoneAvailability.EnsureResolvable`) — a bad image fails the boot loudly instead of surfacing later as a per-request `FarmTimeZoneException`.
- **Migrate command + prod migration split (#263):** `dotnet Cluckwork.Api.dll migrate` is a third run-then-exit CLI verb (same shape as `seed`) that applies EF migrations then exits — the **pre-deploy-job entrypoint**. In **Production** (`appsettings.Production.json`, loaded because the container leaves `ASPNETCORE_ENVIRONMENT` unset → Production; setting it to Development silently re-enables boot-migration) **`Database:MigrateOnStartup=false`**, so the request-serving process never runs schema DDL. The **actual guarantee is ordering** — migrate runs *before* serve: `deploy/docker-compose.yml` runs a one-shot `migrate` service first and `app` waits on `service_completed_successfully` (any other orchestrator runs the `migrate` job as a pre-deploy step). `DatabaseReadyHealthCheck` is the **backstop**: `/health/ready` returns **503 while any migration is pending**, so an orchestrator's readiness gate won't route traffic to a stale schema. This is the app-side of #263's privilege separation. The **remaining host-specific deploy step**: create a least-privilege runtime role (`GRANT USAGE ON SCHEMA public` + DML/sequence privileges, **no DDL**; plus **`ALTER DEFAULT PRIVILEGES`** for the migrator role so objects future migrations create are auto-granted to the runtime role) and point the migrate job at a separate owner/migrator credential. This PR **does not** close #263 on its own — it references it; the role split closes it.
- **Proxy-trust boot guard (#260):** behind a reverse proxy/edge, HSTS (#144) and the per-IP login limiter (#143) only work if the app trusts the proxy's `X-Forwarded-*`, which it does **only** for networks in `RateLimiting:TrustedProxies`. An **empty list in Production fails the boot** (a serving-process guard placed *after* the CLI dispatch, so `migrate`/`seed`/`recover-admin` are unaffected) rather than silently running with inert HSTS + a one-bucket limiter. Opt out with `RateLimiting:AllowNoTrustedProxies=true` only for a rare direct-TLS deploy with no fronting proxy. The concrete edge CIDR is deploy config (in the separate deployment/ops repo).
- **Production Postgres TLS floor (#261/#262):** `ConnectionStrings:Default` accepts libpq URI form (`postgresql://…`, translated to Npgsql key-value; unknown params skip-with-warning) as well as key-value. In **Production** the effective `sslmode` is validated once at boot as a fail-closed **allow-list**: `VerifyCA`/`VerifyFull` silent, `Require` warns (prefer VerifyFull), and **everything else — `Disable`/`Allow`/`Prefer`, unset (Npgsql defaults to `Prefer`), or any undefined `SslMode` — fails the boot**. Never auto-injected. `Database:AllowInsecureConnection=true` is the explicit opt-out for the co-located plaintext compose stack (set on both `app` and `migrate`; a real deploy never sets it). The `VerifyFull` CA + per-host `sslmode` are deploy config (in the separate deployment/ops repo). Integration tests opt out of this **and** the #260 guard in `CluckworkWebApplicationFactory` (plaintext Testcontainers, no proxy) — a Production-env test that skips those opt-outs will fail the boot.
- **Design-time migration connection fail-closed (#318):** `AppDbContextDesignTimeFactory` (used by `dotnet ef migrations add`/`database update`, never by the `migrate` verb, which uses the built host's own config) has **no default connection** — an unset/blank `CLUCKWORK_MIGRATIONS_CONNECTION` throws immediately, naming the variable, rather than falling back to a predictable `Host=localhost;...;Username=postgres;Password=postgres`. Every target is held to the **same allow-list TLS floor as a Production boot** (#261/#262, reused via `PostgresConnectionString.NormalizeAndValidate` — no second validator), except an explicitly acknowledged **loopback** development target: `CLUCKWORK_MIGRATIONS_ALLOW_INSECURE_LOOPBACK=true` permits plaintext, but only when the connection's host is `localhost`/`127.0.0.1`/`::1` (checked via `IPAddress.IsLoopback`) — set against any other host, it fails rather than silently widening scope.
- **Container image hardening (#267):** the runtime stage runs **non-root** (`USER $APP_UID`, uid 1654 — the base image's built-in `app` account); all three base images are **digest-pinned** (`@sha256:…`, kept current by Dependabot's `docker` ecosystem); a CI job builds the image and **Trivy** fails the build on a *fixable* HIGH/CRITICAL. `aquasecurity/trivy-action` is **SHA-pinned** — the action was compromised in the 2026-03 supply-chain incident, so pin the immutable commit, not the re-pointable tag (this is the standing rule for third-party actions). Keep the full glibc base (tzdata + ICU per #264); never chiseled/Alpine.
- **Container health probe (#266):** the runtime image **and** the compose `app` service carry a `HEALTHCHECK` that runs `dotnet Cluckwork.Api.dll healthcheck` — a **fourth run-then-exit CLI verb**, but **dispatched *before* host build** (`Program.cs`, `args is [HealthCheckCliCommand.Verb, ..]`), not via `CliDispatcher`: unlike `migrate`/`seed`/`recover-admin` — which operate on the built host — this one needs no host/DI/DB/config, so a 30s probe must not re-run the whole app startup or re-log boot warnings on every tick. The hardened image ships **no curl/wget** (#267), so the probe is **in-process**: it GETs `/health/ready` over loopback (port derived from `ASPNETCORE_URLS`, default `8080`) and exits `0` on a 2xx, `1` on any other status **or** an unreachable server (refused/timeout) — never a false green. So an instance whose `/health/ready` is 503 (DB down, migrations pending per #263) reports **unhealthy** and compose/an orchestrator stops routing to it. Its `ProbeAsync`/`DefaultReadyUrl` are unit-tested directly (no Docker). The CI `image` job (#267) now also **boots** the built image against a throwaway Postgres and asserts `/health/ready` goes green + the verb exits `0`, so an unbootable image fails CI instead of at deploy time — this is the **app-side of #266**. Deploy-side (the host's readiness path, wait-for-CI gate, post-deploy smoke) lives in the separate deployment/ops repo.
- **Nullable enabled**, no unused usings (both are build-breaking).

## Secrets — never commit

- `deploy/.env` is gitignored (real values). `deploy/.env.example` holds placeholders only.
- Local API debug config uses `dotnet user-secrets` (keyed by `UserSecretsId` in `Cluckwork.Api.csproj`).
- No hardcoded passwords/keys in source — GitGuardian scans PRs. Generate test credentials at runtime.

## Host-agnostic repo (deployment boundary)

This repo is **host-portable** — it must build and run against any host without
carrying provider-specific config. The app reads every environment specific from
config/env (connection string, `RateLimiting:TrustedProxies`, `Seed:TimeZoneId`,
Postgres `sslmode`, …) and **never names or branches on a hosting provider**.

- **Stays here** (portable operational contract): the Dockerfile (incl. its
  `HEALTHCHECK`), `deploy/` compose as a local/reference stack, health probes
  (`/health/live`, `/health/ready`), the `migrate` / `seed` / `recover-admin` /
  `healthcheck` verbs, `.env.example`, and docs that state *requirements* ("needs tzdata + ICU",
  "needs a trusted-proxy list", "needs TLS to Postgres", "a fronting CDN must
  respect origin cache headers").
- **Does NOT belong here** (goes to a **separate deployment/ops repo**):
  provider deploy manifests (`railway.json`, `fly.toml`), IaC (Terraform/Pulumi),
  CDN / DNS / edge config, secret-store wiring, provider-named runbooks, and the
  concrete environment *values* (proxy CIDRs, CA bundles, connection URLs).

Reviewers: treat a hardcoded provider name in code, config, or a committed doc
like a missing test — flag it. Naming a provider as a passing *example* in prose
is tolerable only when no portable phrasing works; prefer the neutral term.

## Pre-commit hook (opt-in)

`git config core.hooksPath .githooks` enables a ~2s pre-commit hook: unit tests
(domain + application) when `.cs`/`.csproj`/`.sln` files are staged, `npm run typecheck` when
`web/` files are staged. Integration tests are deliberately excluded (Docker,
slow) — CI is the authority. Skip once with `--no-verify`.

## CI security gates (#146)

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

**NuGet lock files.** `Directory.Build.props` sets `RestorePackagesWithLockFile`,
so every project has a committed `packages.lock.json` and CI restores with
`--locked-mode` — restores are **deterministic**, and a dependency can't float to
a different resolved version between a green local run and CI. **When you add or
bump a package, run `dotnet restore` and commit the changed lock files in the
same commit** — otherwise CI fails the restore with `NU1004`.

**How the graph learns about transitive NuGet.** Not from the lock files. GitHub
parses `.csproj`/`.vbproj`/`.nuspec`/`.fsproj`/`packages.config` for NuGet, never
`packages.lock.json`, and doesn't derive NuGet transitives statically — on
manifests alone it sees 20 direct `PackageReference`s out of ~80 resolved, leaving
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
Actions secrets `LOCKFIX_APP_ID` and `LOCKFIX_APP_PRIVATE_KEY`. Until both exist
the workflow fails closed (no push); nothing else breaks.

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

## Releases and image publishing (#351)

**A merge into main cuts a release.** The `release` job in `ci.yml` publishes the
container image and pushes a version tag. There is no manual release step, and no
version file anyone has to remember to bump — the **git tag is the version**, so
nothing to forget and nothing for concurrent PRs to conflict over.

The tag is per *release*, not strictly per merge: when merges land faster than a run
completes, the superseded run skips and the next one publishes an image that already
contains the skipped commit's changes. No change ever goes unreleased; it may just
ship under the following version rather than one of its own.

- **The bump comes from PR labels**, not the commit subject: `release:major` → 2.0.0,
  `release:minor` → 1.3.0, **no label → patch**. An inferred bump is silently wrong on
  a typo'd prefix; a label is a deliberate act with a safe default, so most PRs need
  nothing. Both labels present → major (an over-bump costs a number; an under-bump
  lies to everything pinning a range). The arithmetic lives in
  `.github/scripts/next-version.mjs`, self-tested with `node --test` like the other
  two CI scripts, and the first release ever cut is `v0.1.0`.
- **The published image is the image CI verified — byte for byte.** The `image` job
  exports it (`docker save`) as a workflow artifact and `release` loads it back.
  **Never "simplify" this into a rebuild in the release job**: a second `docker build`
  yields different bytes and a different digest, so the artifact that shipped would be
  one the Trivy scan and boot smoke test never examined. That is the entire point
  of #351. The export only runs on a merge into main, so PR runs are unaffected.
- **Publish is gated on the full suite.** `release` declares `needs: [build-and-test,
  web, image]`; those three run in parallel, so only a job downstream of all of them
  knows the run was green. PRs never publish.
- **Deploy by digest, never by tag.** The job emits `ghcr.io/<owner>/<repo>@sha256:…`
  as a job output and in the run summary; the deploy repo pins that. Tags
  (`:v1.2.3`, `:sha-<commit>`) are for humans and can move.
- **Ordering guarantees.** The job is serialised by a `concurrency` group, and it
  releases **only the current tip of main** — a run overtaken by a newer merge skips
  quietly, so version order always matches history order. Image push happens before
  tagging, so a failure never leaves a version pointing at nothing; re-running
  recomputes the same version and re-pushes identical bytes.
- Registry auth is a plain `docker login` with `GITHUB_TOKEN`, not `docker/login-action`
  — two lines of shell, and no third-party code in the credential path.
- Package visibility and whatever credential the host pulls with are **deploy-side**
  concerns (cluckwork-deploy#6), not this repo's.

## Git / PR workflow

- `origin` = GitHub (`github.com/mforce/cluckwork`); `gitea` = backup mirror. Use `gh` for PRs.
- **Label a PR `release:minor` / `release:major`** when its merge should bump more than a
  patch — see the release section above. Unlabelled is a patch, which is usually right.
- **`main` is protected** — branch, push, open a PR; don't commit to `main`.
- Branch names: `feat/…`, `chore/…`, `spec/…`. PRs squash-merge.
- Only commit/push when the human asks.
- **Keep phase epics in sync**: when filing a slice issue, add it to the phase epic's checklist (epic #14 = Phase 1.1, #15 = Phase 1.5) with its issue number; when its PR merges, check it off. Milestone assignment alone is not enough — the epics are how work is navigated.
- **Keep documentation in sync** (owner directive, 2026-07-17): every PR that adds or changes user-visible behavior updates, in the same PR: (1) `specs/product/GLOSSARY.md` when a concept appears or changes meaning, and (2) the SPA Help page + in-app glossary (once #71 lands). Treat a missing doc update like a missing test — reviewers should flag it.

## Phase context

**Phase 1.0 (MVP) is shipped** — epic #13 closed. The egg loop runs end-to-end from the SPA: daily entry (by grade) → submit → egg lots → stock → customer → sales order → FIFO allocation → stock decremented. Single-farm login (multi-tenant infra present but dormant), customers without payments, draft orders cancellable/editable.

Domain terms (flock lifecycle, daily entry states, egg lots, grades, culls, FIFO allocation) are defined in `specs/product/GLOSSARY.md` — read it before renaming or modeling anything.

Current phase: **Phase 1.1** (epic #14, `specs/product/specs.md` §6) — RBAC UI, product catalog / egg-grade management, inventory movement ledger, feed/water/mortality, expenses, payments, dashboard, reports, audit UI, exports, i18n infrastructure (#45; English-only, translations land in 1.5). Known deferred item with an issue: farm-local timezone boundaries (#35). Work is tracked as GitHub issues (epics + slices).

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

When the user types `/graphify`, use the installed graphify skill or instructions before doing anything else.

Rules:
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- Dirty graphify-out/ files are expected after hooks or incremental updates; dirty graph files are not a reason to skip graphify. Only skip graphify if the task is about stale or incorrect graph output, or the user explicitly says not to use it.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
