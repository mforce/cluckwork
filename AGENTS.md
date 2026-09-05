# AGENTS.md — Cluckwork

Poultry egg-farm management system. Backend: **.NET 10** (C#), layered DDD. Frontend: **React 19 + Vite** SPA in `web/`. Postgres via EF Core.

This file is the shared brief for any coding agent (Claude Code, Codex, etc.) and
the **canonical rule set** for the repo. Humans usually want the short path first:
[`CONTRIBUTING.md`](CONTRIBUTING.md) (develop, test, commit),
[`docs/`](docs/README.md) (runbooks, decision records, releasing),
[`SECURITY.md`](SECURITY.md).

**Every rule here is one paragraph.** A rule that carries a `→` link records
what stands behind it, and the narrative is behind that link in
[`docs/decisions/`](docs/decisions/): **follow it before changing the rule.**
Two kinds, and the link alone does not say which: an **earned rule** was
earned by a defect that shipped (what broke, which review round found it, what
the wrong fix was), and an **accepted-risk rule** records a deliberate
declination — no incident, the record says `No incident` and the rule is
*load-bearing* (break it and the accepted risk returns). A rule with no link
is a plain convention that has not yet cost anything; it needs consistency, not
archaeology.

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
  Cluckwork.AppHost         .NET Aspire local orchestration — dev only, never a deploy path
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
dotnet build Cluckwork.sln                 # warnings are errors — keep it clean
dotnet test  Cluckwork.sln                 # 2295 tests as of 2026-09; integration needs Docker
```

- **Integration tests** spin up a real Postgres via Testcontainers (`docker` required). No SQLite — EF SQL semantics differ.
- **Run full stack (prod-like):** `docker compose -f deploy/docker-compose.yml up --build` → SPA + API on http://localhost:8080 (single container; API serves the built SPA from `wwwroot`).
- **Run frontend dev:** `cd web && npm run dev` → http://localhost:5173 (proxies `/api` → :8080).
- **Debug API (no docker stack):** `docker compose -f deploy/docker-compose.dev.yml up -d` (Postgres on :5432), then run/debug `Cluckwork.Api` (Development env). Dev secrets live in **user-secrets**, not files.
- **Run the whole stack under Aspire:** `aspire run` from the repo root (Aspire CLI 13.5) starts Postgres, Redis, the API and Vite with a dashboard → [runbook](docs/runbooks/aspire-local-development.md). **Aspire is a SECOND database (#565)**, not a view of the Compose one: its own volume, username `postgres`, and a generated password in the *AppHost's* user-secrets. Aspire injects that connection string into the `api` resource **it launches** and nowhere else, so a hand-run one-shot verb (`bootstrap-admin`, `seed`, `migrate`, `recover-admin`) falls through to the API's own user-secrets and silently addresses the **Compose** database — pass `ConnectionStrings__Default` explicitly instead ([form 4](docs/runbooks/first-admin-provisioning.md#4-aspire-apphost-stack)). The committed `LocalPorts` defaults must stay clear of the ports Compose publishes; override per machine, **never by editing the committed file**. → [`565-aspire-local-orchestration.md`](docs/decisions/565-aspire-local-orchestration.md)

## Conventions (follow these)

### Application shape

- **Result pattern:** domain/handlers return `Result` / `Result<T>` (see `Domain/Common`). Don't throw for expected failures; throw only for invariant violations (e.g. `Flock.Create` guards).
- **Handler per feature**, invoked directly from endpoints — **no MediatR**. Register handlers/validators/repos in `Program.cs`.
- **Validation:** FluentValidation validators (`*Validator`), one per command; endpoints call `ValidateAsync` and return `ValidationProblem`.
- **Endpoints:** minimal APIs grouped under `/api/v1/...` via `Map<Feature>Endpoints`; writes require auth + an `Idempotency-Key` (middleware).
- **Nullable enabled**, no unused usings — both are build-breaking.

### Data and correctness

- **Every aggregate mutation must bump `Version`.** `Version` is an EF concurrency token (`IsConcurrencyToken`): EF puts the *original* value in the UPDATE's `WHERE` but never auto-increments it, so a mutation without `Version++` silently loses concurrent races — both writers match `WHERE Version = N` — instead of 409ing. This shipped three times; each fix carries a parallel-race integration test, and so must any new mutation.
- **Multi-tenancy:** every tenant-owned entity has `AccountId`, enforced by an EF **global query filter** plus a **`TenantStampInterceptor`** (stamps on insert, and rejects a mismatched write on update/delete). `TenantContext` resolves per-request from the JWT `account_id` claim and is **single-assignment** — a differing re-resolve throws; at startup it is unresolved, so seeders use `IgnoreQueryFilters()`. Several farms now coexist on one deployment: sign-in takes a farm code, and one email address can belong to a user in more than one farm. **`AccountId` is also an EF concurrency token on every entity that carries one (#562):** the `UPDATE`/`DELETE` the database runs carries `AccountId = <original>`, so a detached stub aimed at another farm's row matches nothing — the interceptor alone cannot see a detached write, and an owned-only edit on an attached stub was writing through until the token landed. The token comes from a model walk in `AppDbContext.OnModelCreating`, so a new entity is covered automatically; never remove it, and a database refusal under a resolved tenant is logged as `Tenant.WriteRefusedByDatabase`. **`AccountId` must be a non-nullable `Guid`, and both layers now fail closed on anything else (#673):** the walk throws `TenantAccountIdShapeException` at model build for any other CLR type and the interceptor throws it at `SaveChanges` for a mapped type that is not `Guid` or a value that does not box to one — before, a `Guid?` or a strongly-typed id got no token and no check, which is #562's detached-write hole reopened by one mapping with every test green. Map a new tenant-owned entity's `AccountId` as a plain `Guid`; a strongly-typed id there needs this decision reopened, not a cast. **Identity's `AspNetUserRoles` carries a shadow `AccountId` plus a composite foreign key to `AspNetUsers(Id, AccountId)` (#670):** both layers select by property NAME, so the shadow column is stamped, verified and tokened like any other, and the FK makes a role grant to another farm's user — or under no resolved tenant — a database refusal; the other four user-keyed Identity tables have no writer in `src/` and are recorded as accepted risk. → [`530-multi-farm-tenancy.md`](docs/decisions/530-multi-farm-tenancy.md)
- **Flock-scoped EF reads are discovered from the model (#613).** Every mapped `Flock` or entity with a scalar `FlockId` must keep one structural `AccountId AND flock-scope` query filter; `UserRoleAssignment` is the sole deliberate exclusion because those rows resolve the scope itself. Two no-`FlockId` children rely on named parent/policy gates: daily-entry list/detail loads `DailyEntryGrade` only through a filtered `DailyEntry` aggregate and Worker-readable production totals correlate it through that same filtered parent; the lot movement ledger gates `EggInventoryMovement` through filtered `EggLot`; both children's direct exports remain `AdminOnly`. Any new exclusion or parent-derived child needs an explicit rationale and a causal mutation test; do not replace the model walk with a recalled entity list or expression `ToString()` checks.
- **Transient-DB retry stops at unreplayable work (#269).** `EnableRetryOnFailure` covers self-contained EF units only; an automatic replay above a stateful detector (a counter, a CAS stamp, a single-use claim) cannot tell "this request racing itself" from the signal the detector exists to catch. Two cures, and picking wrong ships the bug: `SingleAttemptExecution` when the replay is itself observable, a durability probe on a self-minted token when the replay writes nothing. → [`269-transient-db-retry-boundary.md`](docs/decisions/269-transient-db-retry-boundary.md)
- **`AuditEvents` is not time-partitioned, on purpose (#505).** The dominant read filters on `AccountId`+`EntityType`+`EntityId` with no date predicate, so monthly partitions would turn one index lookup into one per partition for no pruning benefit. If it is ever needed, partition by `AccountId`. → [`505-audit-events-no-time-partition.md`](docs/decisions/505-audit-events-no-time-partition.md)
- **Suspension is immediate for *use*, not for *issuance* (#579).** Login checks `Account.IsActive` and mints in two steps; a suspension committing between them returns 200 with an inert credential, and the `FOR SHARE` lock that would close the window was declined twice on hot-path cost. #530's "race-safe" means use, not issuance — the inertness rests on four named premises (live middleware read, `RefreshAsync`'s suspended check, suspension's same-transaction revocation of credentials existing when the sweep executes, reactivation's revoke), each pinned by a guard that fails when that premise alone breaks; break any one and #579 reopens. → [`579-suspension-issuance-window.md`](docs/decisions/579-suspension-issuance-window.md)
- **Removing a presentational transform makes every string it transformed a caller (#662).** `.badge`'s `text-transform: uppercase` had let two i18n strings ship lower-case at source; removing the transform in #652 rendered `no entry` and `yes` — typos, in all three locales — until `web/src/i18n/badgeCase.test.ts` pinned it. This is the #394 caller rule applied to text: a presentational transform's removal makes every string it was transforming a caller, and each one must be read, not just the code that stopped transforming it.
- **Help prose naming a control follows that control's LABEL, per locale, and only review checks it (#688).** `catalogParity` compares key sets, so a translated string that renames a control passes every gate: in #678 the `es` help text called the expiry field *caducidad* in one string and *vencimiento* in another while its label said `Vencimiento`, and `tl` called it *pagkaluma* (becoming obsolete). The mechanical fix — assert each help string contains its control's label term — was built and **rejected on evidence**: 81 pairs derived from the `<strong>` spans help prose uses to name controls fail 12 times in `es` and 20 in `tl` with no defect present, because Spanish number agreement and Tagalog affixation defeat it: the label `Nabebenta` is not a substring of `naibibenta` or `maibebenta`, and those three share no common PREFIX at all — what they share is the root `benta`, sitting behind different prefixes, so catching the pair needs root extraction rather than `includes()` or stem matching — and `tl` is the locale the defect landed in, so a guard skipped there is no guard. When you write help or glossary prose that names a labelled control, look up that label **in each locale** and use its word; a locale must never disagree with itself about the same control. **Nothing enforces this.** → [`688-i18n-help-label-pairing.md`](docs/decisions/688-i18n-help-label-pairing.md)

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
- **Farm timezone + tzdata/ICU (#264).** A farm may be given its IANA zone at provisioning time with `provision-account --timezone` (#603, validated there against `FarmSettingsRules.IsKnownTimeZone` — the same rule Settings enforces, so the two cannot disagree); omitted, it starts in `UTC` and its Owner sets the zone in Settings after first login. The clock resolves zones via `TimeZoneInfo.FindSystemTimeZoneById` and fails closed, so the runtime image **must** carry tzdata + ICU — never an Alpine/chiseled base, never `InvariantGlobalization=true`. `TimeZoneAvailability.EnsureResolvable` asserts a canary at boot for **both** process roles. → [`264-farm-timezone.md`](docs/decisions/264-farm-timezone.md)
- **A new Production boot guard must be taught to the sim harness (#370).** Every guard that fails the boot on missing config, and every config-key add/rename/retire, updates `tools/simulation/bootstrap.sh`, `docker-compose.sim.yml` and `verify-harness.sh` **in the same PR** — that harness runs Production config on purpose, is deliberately not in CI, and nothing tells you when you break it. Satisfy guards properly, never by disabling one. → [`370-sim-harness-boot-guards.md`](docs/decisions/370-sim-harness-boot-guards.md)
- **A new required config key must also be taught to the AppHost (#565).** Aspire wires the API's configuration by hand — `WithReference(database, connectionName: "Default")` and the `SharedState__Redis__ConnectionString` entry in `src/Cluckwork.AppHost/Program.cs` — so a newly required key that nothing there supplies breaks `aspire run` and nothing tells you: the AppHost is deliberately not in CI, exactly like the sim harness above. Same PR, same discipline; the difference is only that #370's harness runs Production config while this one runs Development. → [`565-aspire-local-orchestration.md`](docs/decisions/565-aspire-local-orchestration.md)

### Operations and callers

- **Migrate command + prod migration split (#263).** `dotnet Cluckwork.Api.dll migrate` applies migrations then exits — the pre-deploy-job entrypoint. Production sets `Database:MigrateOnStartup=false`, so the serving process never runs DDL; the guarantee is **ordering**, and `DatabaseReadyHealthCheck` is the backstop (`/health/ready` 503s while any migration is pending). → [`263-migrate-command.md`](docs/decisions/263-migrate-command.md)
- **Container health probe: the `healthcheck` verb (#266).** Dispatched before host build (no DI, DB or config), it GETs `/health/ready` over loopback and exits `0` only on a 2xx — the hardened image ships no curl/wget, and the probe must never report a false green. → [`266-container-health-probe.md`](docs/decisions/266-container-health-probe.md)
- **Container image hardening (#267).** Runtime stage runs non-root (`USER $APP_UID`), all three base images are digest-pinned, and Trivy fails the build on a fixable HIGH/CRITICAL. Keep the full glibc base per #264. → [`267-container-hardening.md`](docs/decisions/267-container-hardening.md)
- **Seed / simulation data is never boot-seeded (#280, #284, #279).** Run it explicitly against an already-base-seeded, non-Production database: `ASPNETCORE_ENVIRONMENT=Development dotnet Cluckwork.Api.dll seed --profile demo|simulation` (unset env → Production → blocked). The verb migrates, seeds, exits — authoritative and fail-loud. Both profiles require an Owner (#500); `simulation` additionally requires `Simulation:CastPassword` and fails closed without it. → [`280-seed-and-simulation.md`](docs/decisions/280-seed-and-simulation.md) · [runbook](docs/runbooks/simulation-fixture-on-a-dev-database.md) for loading the simulation fixture into a local debug database
- **A write-contract change must update its non-CI callers (#394).** Coverage is not uniform: the seeders are covered only to the handler layer, so a **validator-only** tightening is invisible to every seeder test, and `tools/simulation/k6/` plus the Playwright specs are uncovered while a green baseline hides it. **Verify the request/status contract by reading the callers, not by running.** → [`394-write-contract-callers.md`](docs/decisions/394-write-contract-callers.md)
- **SPA E2E lives in `tools/simulation/ui/` (#277/#385).** Playwright drives the real built SPA over the same `seed --profile simulation` fixture k6 uses; `web/` stays Vitest. Three enforced rules, each of which has caught something: never hardcode a credential, never hardcode English, respect the farm clock. Anything reasoning about `inert` or the accessibility tree must go through CDP (`src/ax.ts`) — Playwright's own APIs do not model `inert` (#501). → [`277-spa-e2e.md`](docs/decisions/277-spa-e2e.md)
- **A PR whose purpose is visual attaches a 1:1 before/after comparison, captured from a stack rebuilt at the head under review (#662).** It is the only check in this repo that reads the *rendered* result: on #661 it found the one product defect that four review seats, three CI Playwright runs and the driver's own verification all passed. Two mechanics matter — capture at 1:1, because downscaling destroys exactly the 1px hairlines and 4–5% alpha shadows a styling change is about, and rebuild first, because a long-running sim stack serves the bytes it was built from, not the branch under review.
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

### Deploy invariant: exactly ONE serving API instance (#271)

**Run one serving instance.** Every instance runs `DurableJobWorker`, but its poll and the three recurring sweeps now run **only under a single-leader gate** — a session-scoped Postgres advisory lock (`pg_try_advisory_lock`) on a dedicated, non-pooled connection (**#271**, closed): at most one instance leads, crash recovery is automatic (a dead leader's session releases the lock), and the contract is at-most-one-leader with at-least-once, idempotent handlers — never "exactly once". That guarantee holds on a **session-pinned** Postgres endpoint (a direct connection or a session-pooled proxy); under a **transaction-pooling** proxy (e.g. PgBouncer in transaction mode) the lock can migrate across backends and single-leader is *not* guaranteed — a backend-PID affinity check narrows but does not close the window, so that topology relies on the at-least-once + idempotent contract until a dedicated session-pinned lease endpoint (**#556**) lands. The per-account report concurrency cap (#311) was the last independent in-process blocker, and **#545 closed it**: it now runs on the shared renewable lease (#543), keyed per `AccountId`, each permit pinned to the backend that granted it — so N replicas enforce one combined per-account permit count, degrading to a bounded per-instance ceiling (never fail-open) under a store outage. The IP-keyed auth limiters (#143) were another — and **#544 closed it**: they now run on the shared `IFixedWindowCounter` (#543, Redis-backed with an in-process fallback + alarm), so N replicas enforce one combined per-IP budget instead of N. The step-up grant registry was another — the blocker with teeth — and **#338 closed it**: replay now lives in the shared `IClaimOnceStore` (#543) and logout revocation in the durable per-user `ApplicationUser.StepUpLogoutEpoch`, an integer compared for equality (never a timestamp), so both survive across replicas without a shared clock. **#307 is closed and does not license scaling.**

**Do not extend that list from memory** — it was twice derived wrongly, both times a process-local limiter. Re-derive it by walking every `AddSingleton`/`AddHostedService` under `src/` plus every in-memory state primitive, then excluding deliberately. As of 2026-08-25 that walk finds **19 `AddSingleton` call sites and 1 `AddHostedService`** — but count call SITES, not live registrations: `SharedStateRegistration.cs` registers `IClaimOnceStore` and `IFixedWindowCounter` twice each on mutually exclusive branches (Redis vs the in-process fallback), so at most **17** are live in one process, and **16** when Redis is unconfigured. A bare count here has already gone stale twice; re-run the walk rather than trusting this sentence. The run-then-exit verbs are unaffected: they never start hosted services. The #543 shared-state registrations (`IConnectionMultiplexer` + the `IClaimOnceStore`/`IFixedWindowCounter` ports) are **not** blockers — they are the shared store: #338 wired the `IClaimOnceStore` grant-replay caller and #544 wired the `IFixedWindowCounter` auth limiters (both closed above). #545 wired the report cap onto the lease backends directly: `ReportConcurrencyCapRegistration` constructs `RedisLease`/`InProcessLease` itself and pins each permit to its granting backend, rather than resolving a shared `ILease` port from DI. Redis-backed they are multi-replica-safe, their in-process fallbacks a deliberate alarmed degradation. → [`271-single-serving-instance.md`](docs/decisions/271-single-serving-instance.md)

## Writing a guard (a test that asserts an invariant)

A guard is a test whose job is to *fail* when someone later does the wrong thing — the migration freeze, the body-reading endpoint check, the simulation manifest's exact counts. **A wrong guard is worse than no guard, because it reads as safety.** #407 spent five review rounds on one. → [`407-writing-a-guard.md`](docs/decisions/407-writing-a-guard.md); in brief:

- **Run a local adversarial pass before the first push** — mutation checks, or a second agent handed the diff and told to *refute* it.
- **Mutation first, claim second** — never write "this catches X" before running the mutation that makes the guard go red.
- **Two misses of the same shape mean the METHOD is wrong** — prefer "walk everything, exclude deliberately" over "list what I thought of".
- **For a pinned/golden value, prove portability** — repetition on one machine cannot detect environment leakage.
- **Prefer the boring guard** — complexity costs double when the complicated thing is the thing you are trusting.
- **Key a registry by what the code says about itself, never by where it sits (#632).** The tenant-bypass classification file keyed each approved filter-free query by `file:line`, which is precise and fail-closed and also moves for reasons that have nothing to do with the query: three separate changes re-pinned rows whose SQL nobody touched (#601 shifted eight, #609/#606 three, #627 two — `457→469`, `500→512`), and the re-pinning is exactly the moment a reviewer stops reading and starts pasting. It is now keyed by enclosing symbol + set + a hash of the normalized statement, so a comment above the query is invisible to it while an edit TO the query still demands re-review. Measured before adopting: two comment lines had made three classifications simultaneously unclassified and stale. When a stable key can collide where a positional one could not, assert uniqueness in the same guard — one identity excusing two queries is the failure the scheme introduces.
- **Adding an entry to a registry? Find its guards by grepping the registry's READERS, never by recall.** A registry here is any list other code walks — `CliDispatcher.Commands`, `AuditActions`, an enum mirrored into `web/src/i18n/enums.ts`. Adding #534's two verbs, a recalled list produced `CliDispatcherTests` and `ProcessRoleRegistryTests` and missed `OneShotVerbMinimalConfigTests`, which walks `ProcessRoles.OneShotVerbs` and fails any verb with no minimal-config case; `grep -rn "CliDispatcher.Commands\|ProcessRoles.OneShotVerbs" tests/` returns all three in a second. This is the bullet above turned on the guards themselves: a remembered list of guards is exactly the hand-maintained list they exist to stop anyone trusting.
- **A guard that inspects call-site SYNTAX has to be read before you author the call site.** Its rule is not inferable from the code it guards, and violating it is a build failure rather than a review comment. `AuditVocabularyCoverageTests` accepts only `AuditActions.X`, or a ternary of two such references, as the action argument of an `IAuditWriter.WriteAsync` call — it fails closed on everything else and carries exactly one bespoke exemption (`IdentityProvider`'s forwarded parameter) with three companion assertions holding that exemption honest. So the obvious refactor, forwarding the action through a shared private helper's parameter, goes red on a test the author never opened; #534 caught that in a pre-dispatch review and switched to the ternary.
- **Count a selector's call sites before styling it (#662).** Three selectors named across #651/#652 had moved on before the work started: `.stat`/`.stat-label` no longer existed, `.eyebrow` had zero call sites, and `.toolbar` had zero call sites too but was restyled anyway — so half of #651 changed nothing on screen, caught only when the owner compared before/after screenshots and said they looked the same. Before styling a selector an issue names, run `grep -rn "<class>" web/src --include='*.tsx'` and record the count in the design; zero is a legitimate answer — deliberate groundwork counts — but it has to be a stated decision, not a discovery after merge.

## Pre-commit hook (opt-in)

`git config core.hooksPath .githooks` enables a ~2s pre-commit hook: unit tests (domain + application) when `.cs`/`.csproj`/`.sln` files are staged, `npm run typecheck` when `web/` files are staged. Integration tests are deliberately excluded (Docker, slow) — CI is the authority. Skip once with `--no-verify`.

## CI security gates (#146)

CI fails a PR when a **production** dependency carries a known **high+** advisory — NuGet (`dotnet list package --vulnerable`) and npm prod deps (`npm audit --omit=dev`; dev-only advisories are logged, not blocking). Plus dependency-review, CodeQL (advisory), and a weekly scheduled audit. Both audit gates run through `.github/scripts/vuln-gate.mjs` and **fail closed**; the only mute is a dated `.github/security-exceptions.json` entry (exact GHSA id, required `expires`). → [`146-ci-security-gates.md`](docs/decisions/146-ci-security-gates.md)

- **NuGet versions live in `Directory.Packages.props` (#684).** Central Package Management: every `.csproj` carries bare `PackageReference` elements and the one `PackageVersion` list at the repo root decides the version, so a bump is one file, and two projects cannot silently disagree. `CentralPackageFloatingVersionsEnabled` is on because the ranges (`10.*`, `1.*`) moved over as they were; the committed lock files, not the ranges, pin what restores. `Directory.Build.props` beside it holds `RestorePackagesWithLockFile` and the four build properties every project shares (target framework, nullable, implicit usings, warnings as errors) — never a version; do not merge the two. The Dockerfile's restore layer, the CI drift guard and every path filter name the props file explicitly; a new restore input goes in all of them.
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
- **A PR that ships work another OPEN issue claims amends that issue in the same PR.** An issue body is the spec whoever picks it up next plans, sizes and routes against, and it does not update itself. #532 shipped `AccountSuspensionService` whole — its own header naming #534 as the caller it was waiting for — while #534's body went on listing that service's revocation, epoch bump and stamp rotation as *its* scope, so #534 was picked up months later against a description wrong in most of its bullets. **Leave the body as written for history**; add an amendment note at the top pointing at a comment that says what already shipped and where, what actually remains, and which acceptance criteria the shipped code deliberately does not meet. That last part is the one that gets skipped and the one that matters: a criterion nobody will implement is either a follow-up issue or a recorded won't-fix, never a bullet quietly left to rot in a closed issue.

## Phase context

**Phase 1.0 (MVP) is shipped** — epic #13 closed. The egg loop runs end-to-end from the SPA: daily entry (by grade) → submit → egg lots → stock → customer → sales order → FIFO allocation → stock decremented.

**Phase 1.1 (Operational fill) is shipped** — epic #14 closed 2026-08-11: RBAC UI, product catalog / egg-grade management, inventory movement ledger, feed/water/mortality, expenses, payments, dashboard, reports, audit UI, exports, i18n infrastructure. Follow-on work discovered while shipping it moved to epic #15.

**Phase 1.6 (Multi-farm tenancy) is substantially shipped** — epic #530: several farms coexist on one deployment, sign-in takes a farm code, per-account email identity, immediate suspension, operator provisioning (`provision-account`), and all four scale-out blockers closed. Remaining: #357, #388, #537, #556. The decisions and their accepted costs are in [`530-multi-farm-tenancy.md`](docs/decisions/530-multi-farm-tenancy.md).

**Current phase: 1.5** (epic #15, `specs/product/specs.md` §6) — egg product hardening: legacy import, inventory reconciliation, alert center, packaging inventory, additives/supplements, vaccination records, native-speaker es/tl review, deployment readiness, and the Phase 1.1 carryover items on the epic.

Domain terms (flock lifecycle, daily entry states, egg lots, grades, culls, FIFO allocation) are defined in [`specs/product/GLOSSARY.md`](specs/product/GLOSSARY.md) — read it before renaming or modeling anything, and [`docs/architecture.md`](docs/architecture.md) for how those states actually connect.

## graphify

This project has a knowledge graph at `graphify-out/` with god nodes, community structure, and cross-file relationships. When the user types `/graphify`, use the installed graphify skill or instructions before doing anything else.

- For codebase questions, run `graphify query "<question>"` first when `graphify-out/graph.json` exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts — these return a scoped subgraph, usually much smaller than `GRAPH_REPORT.md` or raw grep output.
- Dirty `graphify-out/` files are expected after hooks or incremental updates and are not a reason to skip graphify. Only skip it if the task is about stale graph output, or the user says not to.
- If `graphify-out/wiki/index.md` exists, use it for broad navigation instead of raw source browsing. Read `GRAPH_REPORT.md` only for broad architecture review.
- Run `graphify update .` periodically (AST-only, no API cost) — but not as part of every code change: bundling it into each commit inflates PRs with unrelated changed lines.
