# AGENTS.md — Cluckwork

Poultry egg-farm management system. Backend: **.NET 10** (C#), layered DDD. Frontend: **React 19 + Vite** SPA in `web/`. Postgres via EF Core.

This file is the shared brief for any coding agent (Claude Code, Codex, etc.).

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
- **Both JWT keys are checked at boot, and the check is serving-only (#510/#347).** `AddCluckworkIdentity` requires `Jwt:PublicKeyPem` **and** `Jwt:PrivateKeyPem` to be non-blank **and to actually import**; a `Serving` process refuses to start otherwise. Two traps this closes, both of which shipped: `configuration[...] ?? throw` catches **only null** while the shipped `appsettings.json` carries `""` — use **`IsNullOrWhiteSpace`**, never `??`, or the guard is decorative for every real deployment; and importing inside the `AddJwtBearer` delegate makes a corrupt key a **per-request** failure, so the farm boots green, `/health/ready` passes, the container `HEALTHCHECK` passes, and every authenticated request 500s — an orchestrator sees a healthy instance that rejects every login. The check takes a `ProcessRole` and is skipped for one-shot verbs, which neither issue nor validate a token; **making it eager without that scope is a fresh #331** in the most security-sensitive file of the set. Pinned from both sides: four rows in `ProcessRoleGuardTests` (each key × missing/unusable) plus `OneShotVerbMinimalConfigTests`, whose environment carries no `Jwt:*` at all.
- **Credential epoch revocation (#364).** Every access/refresh token is bound to the user's `CredentialEpoch`; every password-reset path bumps it so issued credentials fail on next use. Epoch **`0` is permanently retired** (users start at `1`, legacy refresh rows at `0`), and a missing/malformed `credential_epoch` claim is **always a mismatch** — never default it to the DB epoch. `CredentialEpochMiddleware` does a **fresh DB read per authenticated request** (the round trip *is* the fail-closed guarantee — do not cache), sits **after `TenantResolutionMiddleware`, before `MustChangePasswordMiddleware`**, and exempts `auth/logout`. Login/step-up snapshot the verified epoch+stamp before clearing failed-access and refuse issuance on a concurrency-loss reload. Distinct from #283/#265/#308 — none substitutes for another. **Break it and a revoked credential keeps working.** → [`docs/decisions/364-credential-epoch-revocation.md`](docs/decisions/364-credential-epoch-revocation.md)
- **Base reference data via guarded raw-SQL migrations (#283).** The default account, four assignable roles, default egg grades, and packed-unit conversions are **static reference data baked into the migrations** as hand-written `migrationBuilder.Sql` with `WHERE NOT EXISTS` guards — **never `HasData`/`InsertData`** (which key on the PK and would either collide or emit `UpdateData`/`DeleteData` that silently reverts the farm's own edits). No runtime seeder, no `Seed:*`. The **grades** guard is whole-set (empty catalog, because grades are user-renamable); roles/conversions/account are **per-key** (keys aren't user-mutable). Pinned by `MigrationSecurityReviewTests` + `BaseReferenceDataMigrationTests`. **Regenerate these into `HasData` and a later model-diff reverts a renamed grade.** → [`docs/decisions/283-migrations-base-provisioning.md`](docs/decisions/283-migrations-base-provisioning.md)
- **Migrations: `InitialCreate` frozen, one migration per change (#407).** `Persistence/Migrations/` is one squashed `InitialCreate` (recorded id `20260801190854_InitialCreate`); it is **frozen and never regenerated**, and **every schema change gets its own `dotnet ef migrations add`**. EF never re-runs an applied migration, so a column **hand-folded into `InitialCreate` silently does not exist** on any booted DB — it surfaces as broken behaviour (a failed login, #399), not a migration error. `InitialCreate` also carries four un-regenerable `lower("Name")` expression indexes + the base-reference SQL; regenerating mints a new timestamp that desynchronises `__EFMigrationsHistory` everywhere. Pre-squash or pre-#407 dev DBs can't migrate forward — drop and recreate. `MigrationSecurityReviewTests` freezes the id + a portable operation digest. → [`docs/decisions/407-migration-freeze.md`](docs/decisions/407-migration-freeze.md)
- **Schema docs are generated, committed, and regenerated with every migration (#417).** `docs/schema/` (mermaid ERD + full catalog: every column, constraint, and index — including the raw-SQL expression indexes and partial predicates EF reflection can't see) is produced by `tools/schema-docs/generate.sh` from an ephemeral migrated Postgres via digest-pinned tbls. **Every PR that adds a migration runs the generator and commits the result in the same PR** — CI's `build-and-test` runs `generate.sh --check` (byte-diff against a fresh generation) and fails a stale PR. Generated files under `docs/schema/` are **never hand-edited**, and a conflict there after a rebase is resolved by regenerating, never by hand-merging. Pinned from three sides by `SchemaDocsTests` (postgres-pin uniformity across every tracked file, no environment leakage, completeness against the live catalog). → [`docs/decisions/417-schema-docs.md`](docs/decisions/417-schema-docs.md)
- **`AuditEvents` is not time-partitioned, on purpose (#505).** The dominant read (`GetProvenanceAsync`, run on every Flocks/Egg grades/Daily entries/Sales/Expenses page load) filters on `AccountId`+`EntityType`+`EntityId` with **no date predicate**, so partitioning by month would convert one index lookup into one per partition — added per-partition overhead that only grows as months pass, for no pruning benefit — plus a PK change, a new INSERT-fails-without-next-month's-partition failure mode inside the same transaction as the audited mutation (#93), and no partition-maintenance job to prevent it. At current growth (~20 MB/year at 100 flocks) this is not close to needed; if it ever is, partition by `AccountId` (the column the query actually filters on) once genuinely multi-tenant, not by time. → [`docs/decisions/505-audit-events-no-time-partition.md`](docs/decisions/505-audit-events-no-time-partition.md)
- **Seed / simulation data is never boot-seeded (#280, #284, #279).** Run it explicitly against an **already-base-seeded, non-Production** database: `ASPNETCORE_ENVIRONMENT=Development dotnet Cluckwork.Api.dll seed --profile demo|simulation` (an unset env → Production → blocked). The verb migrates, seeds, then exits — **authoritative** (ignores config) and **fail-loud** (a real exit code, never a silent no-op). `simulation` additionally records a durable date anchor + completion marker, so a clean re-run — even across a UTC-midnight rollover — converges to `AlreadySeeded`; a polluted account fails the exact-count validation **closed**. **Both profiles now require an Owner and attribute what they seed to real people (#500)** — see the next bullet. → [`docs/decisions/280-seed-and-simulation.md`](docs/decisions/280-seed-and-simulation.md)
- **Nothing writes an audit event without an actor (#500).** `AuditWriter` **throws** on an unresolved `ICurrentUser`, symmetric with the tenant guard beside it. The old fallback stamped `"(unresolved)"` — silent, reachable only from non-HTTP callers, and it shipped ~256 such rows into every demo farm, visible on five screens once #494 rendered provenance. Every non-HTTP caller therefore declares who it is: `bootstrap-admin` and `recover-admin` declare **system actors** (`SystemActors.BootstrapAdmin` / `.BreakGlass`, via `CurrentUserContext.ResolveSystemActor`), and **both seeders now require an Owner** — demo exits `PrerequisitesMissing` naming `bootstrap-admin`, a deliberate break with the old "demo needs nothing but a connection string" contract. Simulation goes further and attributes **per persona**: managers create flocks/products/expenses, sales staff book orders, a rotating worker pool records the daily entries, and roughly one submission in three is a manager signing off somebody else's draft (both #494 provenance shapes). **`ICurrentUser` is an AUTHORIZATION input, not an audit label** — `FlockScopeGuard` reads `Roles`/`UserId`, so which persona the seeder acts as decides what it is *allowed* to write: picking the flock-restricted worker for a foreign flock returns `FlockScope.NotAssigned` and fails the entire seed. Never resolve an actor carrying roles it does not actually hold; build one from `UserManager.GetRolesAsync`, never from a literal.
- **Break-glass recovery (#265):** `recover-admin` is a second one-off CLI verb on the same binary, same run-then-exit shape as `seed` — but deliberately **NOT** environment-gated (it must work against a real Production database): `dotnet Cluckwork.Api.dll recover-admin --email <e> [--account <guid>] [--reason <text>]`. For a locked-out account (the sole-Owner-lost-password case, no email/SMTP reset path) it, in one transaction, resets to a **freshly generated** temporary password (never one passed on the command line), rotates the security stamp, revokes every refresh token, and writes a conspicuous `User.BreakGlassReset` audit row carrying `--reason`. The temp password is printed to **stdout only** (never the logger/OTLP) and exit `0`; failures go to stderr with exit `1` and change nothing. `AdminRecoveryService` orchestrates; `IdentityProvider.BreakGlassResetAsync` shares the reset/revoke core with `SetUserPasswordAsync`. Full procedure + verification drill: `docs/runbooks/break-glass-account-recovery.md`.
- **First-run admin provisioning: `bootstrap-admin` (#283).** `dotnet Cluckwork.Api.dll bootstrap-admin --email <e>` (run-then-exit, always available in Production) migrates, then — **only if the default account has no Owner** — creates one with a **freshly generated password (stdout only, never the logger/OTLP)** and `MustChangePassword=true`; a re-run is a **silent no-op**. While the flag is set the JWT carries `must_change_password`, `MustChangePasswordMiddleware` 403s every endpoint except `auth/change-password`+`auth/logout` (before `UseAuthorization` and before idempotency), and the SPA renders **Set your password**. Any successful reset clears the flag (one invariant in `IdentityProvider`). **Deliberately a separate credential type from #265/#308** — never conflated. → [`docs/decisions/283-first-run-admin-provisioning.md`](docs/decisions/283-first-run-admin-provisioning.md)
- **Farm timezone + tzdata/ICU image constraint (#264):** the default account (a fixed `"UTC"` migration literal since #283) is provisioned once; a real farm sets its actual IANA zone via **Settings → timezone** after first login (`Account.UpdateSettings`), not at provisioning time (the `Seed:TimeZoneId` config lever from before #283 is retired along with the runtime seeder it fed). The farm clock resolves IANA zones via `TimeZoneInfo.FindSystemTimeZoneById` for every safety-sensitive local-day decision and **fails closed**, so the runtime **image MUST carry tzdata + ICU** (the Ubuntu 24.04 (Noble) `aspnet:10.0` base does; **never** an Alpine/chiseled base without tzdata, and **never** `InvariantGlobalization=true`). `AddCluckworkPersistence` still asserts a representative IANA canary resolves at boot (`TimeZoneAvailability.EnsureResolvable`), for **both** process roles — a bad image fails the boot loudly instead of surfacing later as a per-request `FarmTimeZoneException`.
- **Migrate command + prod migration split (#263).** `dotnet Cluckwork.Api.dll migrate` applies EF migrations then exits — the **pre-deploy-job entrypoint**. In Production `Database:MigrateOnStartup=false`, so the serving process never runs DDL; the **guarantee is ordering** — compose runs a one-shot `migrate` service before `app` waits on `service_completed_successfully`. `DatabaseReadyHealthCheck` is the backstop: `/health/ready` **503s while any migration is pending**. The remaining host step (deploy repo) is a least-privilege runtime role with no DDL, plus `ALTER DEFAULT PRIVILEGES` for the migrator. → [`docs/decisions/263-migrate-command.md`](docs/decisions/263-migrate-command.md)
- **Proxy-trust boot guard (#260):** behind a reverse proxy/edge, HSTS (#144) and the per-IP login limiter (#143) only work if the app trusts the proxy's `X-Forwarded-*`, which it does **only** for networks in `RateLimiting:TrustedProxies`. An **empty list in Production fails the boot** (a `ProcessRole.Serving`-only guard, so `migrate`/`seed`/`recover-admin` are unaffected — see the process-role bullet below) rather than silently running with inert HSTS + a one-bucket limiter. Opt out with `RateLimiting:AllowNoTrustedProxies=true` only for a rare direct-TLS deploy with no fronting proxy. The concrete edge CIDR is deploy config (in the separate deployment/ops repo).
- **Process role, not statement order (#347).** A boot guard's scope is declared, never positional. `ProcessRoles.From(args)` (`Hosting/ProcessRole.cs`) computes `ProcessRole.Serving | OneShot` **once**, before the host is built, and every role-scoped guard takes it: `ServingBootGuards.EnsureServingConfiguration` (#260, #319) is called **before** the CLI dispatch on purpose and still leaves the verbs alone, and `AddCluckworkTelemetry` (#316) and `AddCluckworkRateLimiting` (all of `RateLimitingOptions.Validate`) degrade with a stderr warning for `OneShot`. The one-shot verb list is derived from `CliDispatcher.Commands` so a new verb classifies itself; **`healthcheck` is the one that must be named explicitly** (it isn't an `ICliCommand` — it needs no host) and omitting it silently classifies the container's own probe as `Serving`. **Never gate a guard on where its statement sits**: that is what #331 was — the #316 validation ran at service registration, ahead of the dispatcher, and killed `recover-admin` (the break-glass verb) with SIGABRT 134. **Scope the whole subsystem, not the one setting that bit you**: #316's *endpoint* check was role-scoped while `ParseProtocol` two lines above it was not, so `Otlp:Protocol=bogus` still killed `recover-admin`; and `RateLimiting:TrustedProxies` **empty** was correctly serving-only while the **same key malformed** aborted every verb. **Every serving-only guard needs a row in `ProcessRoleGuardTests.ServingOnlyGuards`, one per VIOLATION rather than per subsystem** — `ServingGuardCoverageTests` reflects over `ServingBootGuards`, `RateLimitingOptions` and every `.ValidateOnStart()` registration and fails when a row is missing. Note that `.ValidateOnStart()` is serving-only *by mechanism* (it runs from `Host.StartAsync`, which the CLI dispatcher never reaches), so converting one to an eager registration-time check aborts every verb. The #261/#262 TLS floor and the #264 tzdata canary take no role: they apply to **both**, and unconditional code says so better than a parameter nobody branches on. → [`docs/decisions/347-process-role.md`](docs/decisions/347-process-role.md)
- **Production Postgres TLS floor (#261/#262).** `ConnectionStrings:Default` accepts libpq URI or key-value form. In Production the effective `sslmode` is a **fail-closed allow-list**: `VerifyCA`/`VerifyFull` silent, `Require` warns, **everything else — `Disable`/`Allow`/`Prefer`, unset, or undefined — fails the boot**. `Database:AllowInsecureConnection=true` is the explicit opt-out for the co-located plaintext compose stack. When mapping a libpq param, **`ContainsKey("<libpq name>")==false` does NOT mean Npgsql lacks the setting** — usually the keyword is spelled differently (`keepalives`→`Tcp Keepalive`, `client_encoding`→`Client Encoding`); search the keyword list before concluding it's unmappable (that exact false negative shipped #332). Integration tests opt out of this **and** the #260 guard. → [`docs/decisions/261-postgres-tls-floor.md`](docs/decisions/261-postgres-tls-floor.md)
- **GSS/Kerberos negotiation off by default (#332).** `PostgresConnectionString` appends `GSS Encryption Mode=Disable` **unless the operator set it** (detected by **presence, not value**), **textually** after the TLS floor runs (a round-trip through the Npgsql builder would reorder/requote the operator's string and throw on any keyword this version doesn't know). Without it, Npgsql's `Prefer` default makes every connector dlopen the absent `libgssapi_krb5.so.2` and print two pre-Serilog stderr lines per process. Orthogonal to `sslmode` and to GSS *authentication*. → [`docs/decisions/332-gss-kerberos.md`](docs/decisions/332-gss-kerberos.md)
- **Design-time migration connection fail-closed (#318):** `AppDbContextDesignTimeFactory` (used by `dotnet ef migrations add`/`database update`, never by the `migrate` verb, which uses the built host's own config) has **no default connection** — an unset/blank `CLUCKWORK_MIGRATIONS_CONNECTION` throws immediately, naming the variable, rather than falling back to a predictable `Host=localhost;...;Username=postgres;Password=postgres`. Every target is held to the **same allow-list TLS floor as a Production boot** (#261/#262, reused via `PostgresConnectionString.NormalizeAndValidate` — no second validator), except an explicitly acknowledged **loopback** development target: `CLUCKWORK_MIGRATIONS_ALLOW_INSECURE_LOOPBACK=true` permits plaintext, but only when the connection's host is `localhost`/`127.0.0.1`/`::1` (checked via `IPAddress.IsLoopback`) — set against any other host, it fails rather than silently widening scope.
- **Container image hardening (#267):** the runtime stage runs **non-root** (`USER $APP_UID`, uid 1654 — the base image's built-in `app` account); all three base images are **digest-pinned** (`@sha256:…`, kept current by Dependabot's `docker` ecosystem); a CI job builds the image and **Trivy** fails the build on a *fixable* HIGH/CRITICAL. `aquasecurity/trivy-action` is **SHA-pinned** — the action was compromised in the 2026-03 supply-chain incident, so pin the immutable commit, not the re-pointable tag (this is the standing rule for third-party actions). Keep the full glibc base (tzdata + ICU per #264); never chiseled/Alpine.
- **Container health probe: the `healthcheck` verb (#266).** The runtime image and the compose `app` service run `dotnet Cluckwork.Api.dll healthcheck` — a run-then-exit verb **dispatched before host build** (it needs no host/DI/DB/config, so a 30s probe doesn't re-run startup or re-log boot warnings). The hardened image ships no curl/wget, so the probe is **in-process**: it GETs `/health/ready` over loopback and exits `0` on a 2xx, `1` on any other status or an unreachable server — **never a false green**. CI boots the built image and asserts `/health/ready` goes green. → [`docs/decisions/266-container-health-probe.md`](docs/decisions/266-container-health-probe.md)
- **Transient-DB retry, and where it stops (#269).** `EnableRetryOnFailure` retries only **self-contained EF units** (every read, every `SaveChanges` outside a user transaction); it must **not** wrap unreplayable work. The general hazard is **an automatic replay sitting above a stateful detector** (a monotonic counter, a CAS stamp, a single-use claim) that can't tell "this request racing itself" from the signal it exists to detect. Two cures, and picking wrong ships the bug: **`SingleAttemptExecution`** when the replay is itself observable (e.g. `AccessFailedAsync`'s second durable increment — otherwise one wrong password locks at ~half the #128 threshold); a **durability probe keyed on a self-minted token** when the replay writes nothing and the defect is a mis-read `catch` (refresh-token rotation, login/reset `INSERT`s). All fail closed; pinned from both sides in `RetryBoundaryTests`. → [`docs/decisions/269-transient-db-retry-boundary.md`](docs/decisions/269-transient-db-retry-boundary.md)
- **A new Production boot guard must be taught to the sim harness (#370).** Every guard that fails the boot on missing/invalid config (#260, #261/#262, #316, #319, …) and every config-key add/rename/retire must **also update `tools/simulation/bootstrap.sh` + `docker-compose.sim.yml` + `verify-harness.sh` in the same PR** — that harness runs **Production config on purpose**, is **deliberately not in CI**, and every path into it is human-started, so nothing tells you when you break it (four guards once landed unnoticed until it couldn't boot `main`). Satisfy guards **properly** (a concrete `AllowedHosts`, the documented opt-outs), never by disabling one. **Treat a boot-guard PR that leaves the harness behind like a missing test.** → [`docs/decisions/370-sim-harness-boot-guards.md`](docs/decisions/370-sim-harness-boot-guards.md)
- **SPA E2E lives in `tools/simulation/ui/` (#277/#385).** Playwright drives the real built SPA over the **same** `seed --profile simulation` fixture k6 uses; `web/` stays Vitest + Testing Library. Three enforced rules, each of which has caught something: **never hardcode a credential** (personas from the git-ignored `.sim-cast.json`), **never hardcode English** (selectors resolve through the SPA's own en/es/tl catalogs — a missing key throws), and **respect the farm clock** (`America/Chicago` is behind UTC). Mutation-checked (`npm run mutation`; a red baseline aborts). **Anything reasoning about `inert` or the accessibility tree must go through CDP** (`src/ax.ts`) — Playwright's `ariaSnapshot`/`getByRole`/`isVisible` do **not** model `inert`, so such a spec written with them passes whether or not the app inerts anything (#501). **The quick suite runs on PRs** (path-filtered, ~3 min; owner call 2026-08-08 after #433 broke it silently); the `slow`/`canary` modes and the k6 sibling stay `workflow_dispatch`-only, so the #370 warning still applies to those. → [`docs/decisions/277-spa-e2e.md`](docs/decisions/277-spa-e2e.md)
- **A write-contract change must update its non-CI callers (#394).** Adding a required field, tightening a validator, or changing an aggregate's state machine breaks automated callers no test references. **Coverage is not uniform:** the seeders are covered only to the handler/domain layer, so a **validator-only** tightening is invisible to all five seeder tests (they call `HandleAsync` directly, skipping `ValidateAsync`); `tools/simulation/k6/` and the Playwright specs are **uncovered**, and a green baseline hides it (`authedPost` counts a tolerated `[403,409,422]` as a pass, and `reset.sh` never runs k6). **So verify the request/status contract by reading the callers, not by running.** Grep callers of the endpoint, re-check each payload, and narrow a tolerated-status list when a status flips from "constrained write" to "broken write". → [`docs/decisions/394-write-contract-callers.md`](docs/decisions/394-write-contract-callers.md)
- **Production logs: compact JSON on stdout; base layer argument-free (#404).** stdout is the only sink (no file/vendor sink). Production selects `CompactJsonFormatter`; Development the human `outputTemplate`; the base `appsettings.json` Console entry carries **`Name` only, no `Args`** — a template left in the base file merges *beside* the formatter and Serilog **silently binds the template and ignores the formatter**. Trace fields differ by format: compact JSON writes **`@tr`/`@sp`**, the Dev template `{TraceId}`/`{SpanId}` — a collector query against the wrong name matches nothing. **Compact JSON serializes EVERY property**, so anything pushed via `BeginScope`/`LogContext`/`ForContext` now reaches the collector (see #273 for the redaction answer). Pinned by `ProductionLogFormatTests`. → [`docs/decisions/404-production-logs.md`](docs/decisions/404-production-logs.md)
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

### Deploy invariant: exactly ONE serving API instance (#271, #338)

**Run one serving instance.** More than one breaks four separate things, and
none of them announces itself as "you are running two replicas". This is a
*requirement* the app imposes on its host, so it lives here; the concrete
replica count and how it is pinned are deploy-side.

`AddHostedService<DurableJobWorker>()`
(`Hosting/CluckworkJobServiceCollectionExtensions.cs`) means **every** instance
runs the worker loop, and the poll claims nothing — no `FOR UPDATE SKIP
LOCKED`, no lease, no advisory lock. What that exposes today is **the three
recurring sweeps**, which ride the same poll and run unconditionally per
instance: `DailyEntryLockSweep`, `RefreshTokenPurgeSweep`,
`IdempotencyRecordPurgeSweep`. The durable-job half is still a scaffold that
selects pending rows and logs them — no handlers are registered — so job
double-execution is latent, not live. **Registering the first handler makes it
live**, which is the moment this invariant stops being about sweeps only.

Not every double-run is equally bad, and the difference decides what an operator
would actually see. The two purge sweeps are idempotent deletes, so a second
runner wastes work and reports nothing — genuinely silent.
`DailyEntryLockSweep` is not silent: it reads-then-writes behind an optimistic
`Version` token, so the losing replica's `SaveChangesAsync` throws a concurrency
exception, which its per-account `catch` logs as `Lock sweep failed for account
{AccountId}` (`Jobs/DailyEntryLockSweep.cs`). That is observable — but it reads
as a database fault, not as "two replicas are sweeping", which is the sense in
which the duplicate stays invisible.

**Four independent blockers, all of which must close before scaling** — and
they are not all in #271:

- **#271 — background work has no single-runner guarantee** (this section's
  subject). Needs an advisory-lock lease or `FOR UPDATE SKIP LOCKED` with crash
  recovery, **plus** a two-instance test proving each job and each sweep runs
  exactly once.
- **#338 — `IStepUpGrantRegistry` is process-local.** `InMemoryStepUpGrantRegistry`
  is a per-process singleton holding step-up replay tracking and logout epochs,
  and its own header says a multi-instance deployment must move it to a shared
  store. Both #308 guarantees degrade per replica: a single-use grant becomes
  usable **once per replica**, and a logout honoured by one replica is invisible
  to the others. These grants gate privileged account-control operations, so
  this is the blocker with teeth — closing #271 alone does not license scaling.
- **The IP-keyed auth limiters (#143) are in-process.** `AddRateLimiter`'s
  partitions live in each process — login, refresh, and client-error reports
  alike — so N replicas allow roughly N times the intended attempts per IP
  before lockout.
- **The per-account report concurrency cap (#311) is in-process.**
  `ReportConcurrencyLimiter` is a singleton owning a `PartitionedRateLimiter<Guid>`
  (`Api/RateLimiting/ReportConcurrencyLimiter.cs`, registered at
  `Hosting/CluckworkRateLimitingServiceCollectionExtensions.cs`), so one account
  can hold up to N × `ReportsConcurrency.PermitLimit` heavyweight report queries
  in flight — the DB/CPU ceiling that cap exists to bound, multiplied by the
  replica count.

The last two have no open issue; they are recorded here rather than left to be
rediscovered.

**How this list was derived, because it was twice derived wrongly.** Both misses
were the same shape — a process-local limiter — and the second one lived in a
file the first sweep had already opened. So do not extend this list from memory.
Re-derive it: enumerate **every** `AddSingleton`/`AddHostedService` under `src/`
plus every in-memory state primitive (`ConcurrentDictionary`, `IMemoryCache`,
`PartitionedRateLimiter`, `Channel`, `SemaphoreSlim`, mutable statics), then
classify each one as safe or not. That walk currently finds 12 `AddSingleton`
registrations and 1 `AddHostedService` (13 total, the hosted service is not one
of the 12); the four above are what survives it. Excluded deliberately, so
the next walk need not re-litigate them: `TimeProvider.System`, the Serilog
diagnostic contexts, `IValidateOptions`/`IAuthorizationMiddlewareResultHandler`
(all stateless), and `FirstRunProvisioningLatch` — a monotonic one-way cache of
"the default account has an Owner", where a per-replica copy costs at most a few
extra reads and cannot go stale in the unsafe direction.

**#307 (multi-replica HTTP write idempotency) is CLOSED**, so the request-path
half is genuinely done — do not read that closure, or #271's, as permission to
scale. Documenting the invariant, as this section does, is the interim
mitigation, not the close for any of the four.

The run-then-exit verbs (`migrate`, `seed`, `recover-admin`, `bootstrap-admin`,
`healthcheck`) are unaffected: they do not start the host's hosted services.

## Writing a guard (a test that asserts an invariant)

A guard is a test whose job is to *fail* when someone later does the wrong thing — the migration freeze (`MigrationSecurityReviewTests`), the body-reading endpoint check, the simulation manifest's exact counts. **A wrong guard is worse than no guard, because it reads as safety.** #407 spent five review rounds on one. The full rules with the incidents that earned them are in [`docs/decisions/407-writing-a-guard.md`](docs/decisions/407-writing-a-guard.md); in brief:

- **Run a local adversarial pass before the first push** — mutation checks, or a second agent handed the diff and told to *refute* it. Most of #407's rounds were findable locally in seconds.
- **Mutation first, claim second** — never write "this catches X" before running the mutation that makes the guard go red.
- **Two misses of the same shape mean the METHOD is wrong** — prefer "walk everything, exclude deliberately" over "list what I thought of."
- **For a pinned/golden value, prove portability** — repetition on one machine cannot detect environment leakage; assert the generated content names no absolute paths or assembly files.
- **Prefer the boring guard** — complexity costs double when the complicated thing is the thing you're trusting.

## Pre-commit hook (opt-in)

`git config core.hooksPath .githooks` enables a ~2s pre-commit hook: unit tests
(domain + application) when `.cs`/`.csproj`/`.sln` files are staged, `npm run typecheck` when
`web/` files are staged. Integration tests are deliberately excluded (Docker,
slow) — CI is the authority. Skip once with `--no-verify`.

## CI security gates (#146)

CI fails a PR when a **production** dependency carries a known **high+** advisory — NuGet (`dotnet list package --vulnerable`) and npm prod deps (`npm audit --omit=dev`; dev-only advisories are logged, not blocking). Plus dependency-review (PR diff), CodeQL (advisory), and a weekly scheduled audit. Both audit gates run through `.github/scripts/vuln-gate.mjs` and **fail closed**; the only mute is a dated `.github/security-exceptions.json` entry (exact GHSA id, required `expires`).

**NuGet lock files.** Every project has a committed `packages.lock.json` and CI restores `--locked-mode`. **When you add or bump a package, run `dotnet restore` and commit the changed lock files in the same commit** — else CI fails the restore with `NU1004`. Dependabot NuGet PRs are auto-healed by `.github/workflows/dependabot-lockfix.yml`.

**Pin third-party Actions to a full commit SHA** with a trailing `# vX.Y.Z` comment — **never a mutable tag** (the 2026-03 `aquasecurity/trivy-action` and 2025-03 `tj-actions/changed-files` compromises retargeted tags to secret-exfiltrating code). `actions/*` and `github/*` may keep major-version tags.

Full rationale — the transitive-graph submission, the shared GitHub App and its `permission-*` downscoping, the exception-gate fail-closed shapes — is in [`docs/decisions/146-ci-security-gates.md`](docs/decisions/146-ci-security-gates.md).

## Releases and image publishing (#351)

Two stages, deliberately separate: **CI publishes an image per merge; the release PR turns one into a version.** `README.md`'s "Releases & container images" is the **how-to**; this section is the **invariants — what not to break**; the full mechanism (release-please twice per push, the `groom` boundary probe, the commit-body parser, the App-token reasoning, the repair path) is in [`docs/decisions/351-releases.md`](docs/decisions/351-releases.md).

- **Every merge to `main`** publishes `ghcr.io/<owner>/<repo>:sha-<commit>` from the `publish` job (Trivy-scanned, boot-tested). **Merging the "Release vX.Y.Z" PR** drafts the release, **promotes** that commit's image to `:vX.Y.Z`, then publishes.
- **Promotion is a server-side retag of the existing digest** (`docker buildx imagetools create --prefer-index=false`), **never a rebuild** — a rebuild yields different bytes no scan ever examined. `--prefer-index=false` is load-bearing: the default `true` wraps a new top-level digest.
- **Promotion reads the digest from CI's own run artifact, never by resolving `:sha-<commit>`** — that tag is mutable in the public window between merge and CI's push. Adding a CI job that should gate a release? **Add it to `publish.needs`** — that list is exactly what the digest artifact proves, and nothing enforces it.
- **The release stays a draft until its image is promoted**; GitHub withholds the git tag for a draft, so a failed promotion leaves no version pointing at nothing.
- **The version comes from conventional commits, damped below 1.0.0** (`feat!:`/`BREAKING CHANGE` → minor; `feat:` and everything else → patch) via two `*-pre-major` settings; the *first* release is set by a separate `initial-version` lever. The mapping flips **silently at 1.0.0** — reach it deliberately with a `Release-As:` footer.
- **A commit-body parse error drops the whole commit** (no changelog entry, no bump, green run): **never start a line with `word(` that has another `(` inside it**. `.githooks/commit-msg` catches the body, but a **multi-commit PR's subject comes from the PR title**, which no local hook sees — so **the PR title is the release note**.
- **The release PR is opened with a GitHub App token, not `GITHUB_TOKEN`** (which can't open a PR unless a repo-wide setting is on, and whose PRs get no CI run). Every App consumer **must** keep `permission-*` downscoping — omitting it mints the union of every grant the App holds, silently. Shared with `dependabot-lockfix.yml`; needs `LOCKFIX_APP_CLIENT_ID`/`LOCKFIX_APP_PRIVATE_KEY`; use `client-id`, not the deprecated `app-id`.
- **Never hand-edit `.release-please-manifest.json` or `version.txt`** — release-please owns them; a manual edit desynchronises it from the tags that exist.
- **Deploy by digest, never by tag** — *obtaining* the digest and *verifying* its origin are two separate problems, and one does not imply the other.
  - **Obtain:** every release carries an `image.json` **release asset** (`gh release download <tag> -p image.json`) with `reference`/`digest`/`commit`. Don't parse prose; don't resolve a tag.
  - **Verify:** `ci.yml` writes a build-provenance attestation (#354); deploy runs `gh attestation verify oci://<ref> --repo … --signer-workflow …/ci.yml --source-ref refs/heads/main --bundle-from-oci`. **All three flags are load-bearing and none is the default** — `--bundle-from-oci` (read the registry copy), `--signer-workflow` (bind to the workflow, not just the repo), `--source-ref` (bind to the ref — so a repair dispatch must run **from `main`**).
  - **Then confirm the tag still resolves to the digest you verified** — compare against `reference`, **never** the asset's separate `digest` field.

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

  **The paragraph directly above is the canonical statement of the boundary.** `README.md` and the `ci.yml` comment carry a summary and point here rather than restating it, because successive corrections to this claim repeatedly updated one copy and left the others contradicting it. If you correct it, correct it in all three and check they agree. Full derivation of the two gates is in [`docs/decisions/351-releases.md`](docs/decisions/351-releases.md).
- Package visibility and the host's pull credential are **deploy-side** concerns (cluckwork-deploy#6), not this repo's.

## Git / PR workflow

- `origin` = GitHub (`github.com/mforce/cluckwork`); `gitea` = backup mirror. Use `gh` for PRs.
- **`main` is protected** — branch, push, open a PR; don't commit to `main`.
- Branch names: `feat/…`, `chore/…`, `spec/…`. PRs squash-merge.
- **The PR title is the release note.** It becomes the squashed commit subject, which
  is what release-please parses for both the changelog and the version bump — so a
  typo'd or non-conventional prefix silently costs a bump. See the release section above.
- Only commit/push when the human asks.
- **Keep phase epics in sync**: when filing a slice issue, add it to the phase epic's checklist (epic #14 = Phase 1.1, #15 = Phase 1.5) with its issue number; when its PR merges, check it off. Milestone assignment alone is not enough — the epics are how work is navigated.
- **Keep documentation in sync** (owner directive, 2026-07-17): every PR that adds or changes user-visible behavior updates, in the same PR: (1) `specs/product/GLOSSARY.md` when a concept appears or changes meaning, and (2) the SPA Help page + in-app glossary (once #71 lands). Treat a missing doc update like a missing test — reviewers should flag it.

## Phase context

**Phase 1.0 (MVP) is shipped** — epic #13 closed. The egg loop runs end-to-end from the SPA: daily entry (by grade) → submit → egg lots → stock → customer → sales order → FIFO allocation → stock decremented. Single-farm login (multi-tenant infra present but dormant), customers without payments, draft orders cancellable/editable.

**Phase 1.1 (Operational fill) is shipped** — epic #14 closed 2026-08-11. Every item in `specs/product/specs.md` §6's Phase 1.1 scope landed: RBAC UI, product catalog / egg-grade management, inventory movement ledger, feed/water/mortality, expenses, payments, dashboard, reports, audit UI, exports, i18n infrastructure (#45; English-only, translations land in 1.5). Follow-on work discovered while shipping 1.1 (not part of its original scope) moved to epic #15 on close, not a new phase number.

Domain terms (flock lifecycle, daily entry states, egg lots, grades, culls, FIFO allocation) are defined in `specs/product/GLOSSARY.md` — read it before renaming or modeling anything.

Current phase: **Phase 1.5** (epic #15, `specs/product/specs.md` §6) — egg product hardening: legacy import, inventory reconciliation, alert center, packaging inventory, additives/supplements, vaccination records, native-speaker es/tl review, deployment readiness, and the Phase 1.1 carryover items listed on the epic. Work is tracked as GitHub issues (epics + slices).

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

When the user types `/graphify`, use the installed graphify skill or instructions before doing anything else.

Rules:
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- Dirty graphify-out/ files are expected after hooks or incremental updates; dirty graph files are not a reason to skip graphify. Only skip graphify if the task is about stale or incorrect graph output, or the user explicitly says not to use it.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- Users need to periodically run `graphify update .` to keep the graph current (AST-only, no API cost) — exact frequency is unknown yet. Don't run it as part of every code change: bundling it into each commit inflates PRs with unrelated changed lines and works against keeping PRs small.
