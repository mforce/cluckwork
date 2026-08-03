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
- **Credential epoch revocation (#364):** every access and refresh token is bound to the user's `CredentialEpoch`; all password-reset paths bump it so already-issued credentials fail on their next use. Epoch **`0` is permanently retired**: users start at `1`, legacy refresh rows start at `0`, and a missing or malformed `credential_epoch` claim is always a mismatch — never default the parsed claim to the current database epoch or otherwise treat it as an exemption. `CredentialEpochMiddleware` deliberately performs a fresh database read for every authenticated request; the round trip is the fail-closed revocation guarantee, so do not cache it. Keep the middleware after `TenantResolutionMiddleware` (the lookup is tenant-scoped) and before `MustChangePasswordMiddleware`; `/api/v1/auth/logout` is deliberately exempt so a superseded credential can still terminate its own session. Login and step-up snapshot the epoch + security stamp that the password actually verified **before** clearing the failed-access count, then refuse issuance if a concurrency-loss reload reveals different credentials — never let an old password adopt a concurrent reset. Their disabled-account branches still pay exactly one real password-hash cost, but must not mutate failed-access/lockout state; `AccountLockout` applies the same boundary after a concurrency reload, stopping rather than charging a stale failure to a newly disabled or reset credential. The SPA serializes refresh and self-password-change through the same per-tab queue + cross-tab Web Lock because both replace the shared HttpOnly cookie — a late old-epoch refresh must never overwrite the fresh pair that keeps the self-changing device signed in. This epoch is distinct from #283's first-login password-change flag, #265's operator-only break-glass temporary password, and #308's single-use browser step-up grant: the mechanisms have different audiences and lifetimes and none substitutes for another.
- **Migrations + base provisioning (#283):** EF migrations auto-apply on startup (`Database:MigrateOnStartup`, default true — but **Production sets it `false`**; the `migrate` command runs DDL out-of-band, see the #263 bullet below). The default account, the four assignable roles (`Roles.Assignable`), the default egg grades, and the default packed-unit conversions are **static reference data baked into the migrations themselves** — deterministic and multi-instance-safe by construction, **no runtime seeder, no `Seed:*` config**. They ship as **hand-written `migrationBuilder.Sql` with `WHERE NOT EXISTS` guards, deliberately NOT EF's `HasData`/`InsertData`** — `HasData` keys on the PRIMARY KEY, which assumes a virgin schema, but every install that ever booted pre-#283 already has these rows under *different* ids (the old `DatabaseSeeder` minted roles/grades/conversions with `Guid.NewGuid()`), so `InsertData` either collides on `PK_Accounts` or silently violates the real natural-key unique indexes. The #245 squash retires *that* particular reason (a virgin database is now the only starting state), but **do not "regenerate" these into `HasData`**: `HasData` rows become part of the **model**, and every one of these tables is user-mutable through the app (Settings renames the account, `PUT /api/v1/egg-grades/{id}` renames a grade, `EggUnitConversion.Update` retunes `EggsPerUnit`), so a later model-diff would emit `UpdateData`/`DeleteData` that silently reverts the farm's own edits. Raw SQL seeds once and then leaves the rows alone forever. The guard's scope differs per batch, and the rule is *whether the user can change the natural key or remove a set member*: the **egg grades** batch is gated on the account's grade catalog being **entirely empty** (whole-set), because grades are user-managed — `PUT /api/v1/egg-grades/{id}` renames one, and a per-name guard cannot see a `Small` the farm renamed to `Pullet`, so it would resurrect an active, saleable duplicate (the old seeder skipped the whole set for exactly this reason); **roles, unit conversions, and the default account** stay **per-key**, because their keys are not user-mutable (no role CRUD exists at all; `EggUnitConversion.Update` cannot change `UnitCode` and there is no create/delete; the account's key is a fixed id), and per-key additionally lets a later-added role or unit code back-fill into an older install. `MigrationSecurityReviewTests` pins the SQL shape (12 INSERT statements, 21 rows, every one `WHERE NOT EXISTS`-guarded, the grades guard blind to `"Name"`) and `BaseReferenceDataMigrationTests` pins the result against a real virgin Postgres. `MigrationSecurityReviewTests` also asserts no migration ever inserts a credential-shaped row (`AspNetUsers`, `PasswordHash`, `SecurityStamp`, …) — the schema itself is never a place a credential can hide. The first admin is a **separate, deliberately NOT migration-baked** concern — see the #283 bullet below.
- **One migration, squashed (#245):** `Persistence/Migrations/` holds exactly **one** migration, `InitialCreate`, generated from the current model on 2026-08-01. It replaced the 34 that had accumulated since 2026-06-27 (12 of them carrying raw SQL written for mid-history data states — backfills that touch 0 rows on an empty database but still execute). Done once, before the first production deploy, precisely because no `__EFMigrationsHistory` anywhere needed baselining; **it cannot be repeated after go-live**. Any database still carrying the old history (a dev DB from `deploy/docker-compose.dev.yml`, an old demo/sim instance) can no longer migrate forward — **drop and recreate**: `docker compose -f deploy/docker-compose.dev.yml down -v && docker compose -f deploy/docker-compose.dev.yml up -d`. Two things in that migration are **hand-carried and un-regenerable** — a plain `dotnet ef migrations add` drops both on the floor, so never regenerate `InitialCreate` without re-adding them: (1) the four **`lower("Name")` expression unique indexes** (`IX_EggGrades_AccountId_FarmId_LowerName`, `IX_ExpenseCategories_NameCi`, `UX_InventoryItems_Account_Farm_LowerName`, `IX_Products_AccountId_LowerName`) — EF cannot model a functional index, and losing them loses four uniqueness constraints; (2) the **static base reference data** raw SQL (see the bullet above). `MigrationSecurityReviewTests` and `BaseReferenceDataMigrationTests` fail if either goes missing.
- **Seed command (#280, #284):** demo sample data is NOT boot-seeded — there is no `Seed__Demo` flag anymore. Run it explicitly, against a database that has **already been booted once** so the base seed above exists (default account, `Admin` role, default egg grades — the demo seed needs all three and fails fast with a clear message if they're missing): `ASPNETCORE_ENVIRONMENT=Development dotnet Cluckwork.Api.dll seed --profile demo` (an unset `ASPNETCORE_ENVIRONMENT` defaults to Production, which the guard below blocks — always set it for a local/dev/CI run). The command migrates the schema, runs `DemoDataSeeder`, then exits (Kestrel never starts); it is **authoritative** (seeds regardless of any config) and **fail-loud** (a real exit code: `0` only when data was actually seeded or was already present, non-zero with an stderr message otherwise — never a silent no-op). `DemoDataSeeder` is registered only when `!IsProduction()` (defense-in-depth): running the command against a Production-env process fails with a clear message instead of writing fake data. Intended flow: the serving process for a real farm stays Production; a non-Production process (dev box, CI, or a sim/load-test harness) runs `seed --profile demo` against its own already-base-seeded database. The **`simulation` profile** (#279) sits in the same `switch` with the same authoritative + fail-loud + Production-guarded contract (`SimulationDataSeeder`); it additionally records its date anchor + a completion marker in a durable `SimulationSeedState` row (anchor written before the first fixture mutation; completion stamped only after the manifest succeeds). A clean re-run — even one straddling a UTC-midnight rollover — reuses that anchor and converges to `AlreadySeeded` with an unchanged manifest fingerprint instead of writing a shifted duplicate set; `AlreadySeeded` is reported only when the counts are unchanged, so a `SimulationOptions` change that adds fixtures re-runs as `Seeded`. The durable anchor can't be poisoned by a mid-seed crash or by foreign daily entries a load test writes into the account (a data-derived anchor could); but a polluted account still fails the exact count validation **closed** (`Failed`) rather than certifying a fixture that no longer matches the definition.
- **Break-glass recovery (#265):** `recover-admin` is a second one-off CLI verb on the same binary, same run-then-exit shape as `seed` — but deliberately **NOT** environment-gated (it must work against a real Production database): `dotnet Cluckwork.Api.dll recover-admin --email <e> [--account <guid>] [--reason <text>]`. For a locked-out account (the sole-Owner-lost-password case, no email/SMTP reset path) it, in one transaction, resets to a **freshly generated** temporary password (never one passed on the command line), rotates the security stamp, revokes every refresh token, and writes a conspicuous `User.BreakGlassReset` audit row carrying `--reason`. The temp password is printed to **stdout only** (never the logger/OTLP) and exit `0`; failures go to stderr with exit `1` and change nothing. `AdminRecoveryService` orchestrates; `IdentityProvider.BreakGlassResetAsync` shares the reset/revoke core with `SetUserPasswordAsync`. Full procedure + verification drill: `docs/runbooks/break-glass-account-recovery.md`.
- **First-run admin provisioning (#283):** `bootstrap-admin` is a fourth one-off CLI verb, same run-then-exit shape as `seed`/`migrate`, always available in Production (same posture as `recover-admin`): `dotnet Cluckwork.Api.dll bootstrap-admin --email <e>`. It migrates the schema (idempotent, like the other verbs), then — **only if the default account has no Owner yet** — creates one with a **freshly generated** password and `ApplicationUser.MustChangePassword = true`, printed to **stdout only** (never the logger/OTLP, identical rule to `recover-admin`). A re-run against an already-provisioned account is a silent no-op: no second Owner, no password reprinted. `FirstRunAdminService` orchestrates (mirrors `AdminRecoveryService`'s CLI-wrapper-calls-a-service shape); `IIdentityProvider.CreateUserAsync` gained an optional `mustChangePassword` parameter (default `false` — an ordinary Users-page-created user is never gated) that this is the only caller to pass `true`. While the flag is set: the JWT carries a `must_change_password` claim (`JwtTokenService`), `MustChangePasswordMiddleware` refuses **every** endpoint except `auth/change-password` and `auth/logout` with 403 (placed before `UseAuthorization`, so it applies uniformly regardless of an endpoint's `AuthPolicies` tier, and before the idempotency middleware, so a blocked write burns no key), and the SPA's `ProtectedRoute` renders a **"Set your password"** screen (`SetPasswordPage`, reusing `/auth/change-password` — the operator already knows the printed password as their "current" one) instead of the app shell, on every route. Any successful password reset (self-service change, an Owner's `SetUserPasswordAsync`, or break-glass) clears the flag — a single invariant in `IdentityProvider`, not re-derived per path. **Deliberately a separate credential type** from `recover-admin`'s temp password and from #308's browser step-up re-confirmation grant — different audience (pre-auth shell access vs. an authenticated Owner), different lifetime, never conflated; do not treat any of #283/#265/#308 as covering another.
- **Farm timezone + tzdata/ICU image constraint (#264):** the default account (a fixed `"UTC"` migration literal since #283 — see the bullet above) is provisioned once; a real farm sets its actual IANA zone via **Settings → timezone** after first login (`Account.UpdateSettings`), not at provisioning time (the `Seed:TimeZoneId` config lever from before #283 is retired along with the runtime seeder it fed). The farm clock resolves IANA zones via `TimeZoneInfo.FindSystemTimeZoneById` for every safety-sensitive local-day decision and **fails closed**, so the runtime **image MUST carry tzdata + ICU** (the Ubuntu 24.04 (Noble) `aspnet:10.0` base does; **never** an Alpine/chiseled base without tzdata, and **never** `InvariantGlobalization=true`). `Program.cs` still asserts a representative IANA canary resolves at boot (`TimeZoneAvailability.EnsureResolvable`) — a bad image fails the boot loudly instead of surfacing later as a per-request `FarmTimeZoneException`.
- **Migrate command + prod migration split (#263):** `dotnet Cluckwork.Api.dll migrate` is a third run-then-exit CLI verb (same shape as `seed`) that applies EF migrations then exits — the **pre-deploy-job entrypoint**. In **Production** (`appsettings.Production.json`, loaded because the container leaves `ASPNETCORE_ENVIRONMENT` unset → Production; setting it to Development silently re-enables boot-migration) **`Database:MigrateOnStartup=false`**, so the request-serving process never runs schema DDL. The **actual guarantee is ordering** — migrate runs *before* serve: `deploy/docker-compose.yml` runs a one-shot `migrate` service first and `app` waits on `service_completed_successfully` (any other orchestrator runs the `migrate` job as a pre-deploy step). `DatabaseReadyHealthCheck` is the **backstop**: `/health/ready` returns **503 while any migration is pending**, so an orchestrator's readiness gate won't route traffic to a stale schema. This is the app-side of #263's privilege separation. The **remaining host-specific deploy step**: create a least-privilege runtime role (`GRANT USAGE ON SCHEMA public` + DML/sequence privileges, **no DDL**; plus **`ALTER DEFAULT PRIVILEGES`** for the migrator role so objects future migrations create are auto-granted to the runtime role) and point the migrate job at a separate owner/migrator credential. This PR **does not** close #263 on its own — it references it; the role split closes it.
- **Proxy-trust boot guard (#260):** behind a reverse proxy/edge, HSTS (#144) and the per-IP login limiter (#143) only work if the app trusts the proxy's `X-Forwarded-*`, which it does **only** for networks in `RateLimiting:TrustedProxies`. An **empty list in Production fails the boot** (a serving-process guard placed *after* the CLI dispatch, so `migrate`/`seed`/`recover-admin` are unaffected) rather than silently running with inert HSTS + a one-bucket limiter. Opt out with `RateLimiting:AllowNoTrustedProxies=true` only for a rare direct-TLS deploy with no fronting proxy. The concrete edge CIDR is deploy config (in the separate deployment/ops repo).
- **Production Postgres TLS floor (#261/#262):** `ConnectionStrings:Default` accepts libpq URI form (`postgresql://…`, translated to Npgsql key-value; a param Npgsql supports under a different spelling — `channel_binding`, `target_session_attrs`, `gssencmode` — is **mapped**, and only a genuinely unsupported one — `sslcompression` — skips-with-warning; **`ContainsKey("<libpq name>") == false` does NOT mean Npgsql lacks the setting** — it usually means the keyword is spelled differently (`keepalives` → `Tcp Keepalive`, `client_encoding` → `Client Encoding`, `gssencmode` → `GSS Encryption Mode`), so search the keyword list for the concept before concluding a param is unmappable; that exact false negative is what shipped #332, and the first fix for it re-made the same mistake with `keepalives`. Still unmapped and worth a follow-up: the `keepalives*` family and `client_encoding` — `keepalives` needs a *value* translation (`1` → a bool keyword), not just a keyword map) as well as key-value. In **Production** the effective `sslmode` is validated once at boot as a fail-closed **allow-list**: `VerifyCA`/`VerifyFull` silent, `Require` warns (prefer VerifyFull), and **everything else — `Disable`/`Allow`/`Prefer`, unset (Npgsql defaults to `Prefer`), or any undefined `SslMode` — fails the boot**. Never auto-injected. `Database:AllowInsecureConnection=true` is the explicit opt-out for the co-located plaintext compose stack (set on both `app` and `migrate`; a real deploy never sets it). The `VerifyFull` CA + per-host `sslmode` are deploy config (in the separate deployment/ops repo). Integration tests opt out of this **and** the #260 guard in `CluckworkWebApplicationFactory` (plaintext Testcontainers, no proxy) — a Production-env test that skips those opt-outs will fail the boot.
- **GSS/Kerberos negotiation off by default (#332):** Npgsql's `GssEncryptionMode` defaults to `Prefer`, so every connector probes the GSSAPI stack before authenticating. The runtime image carries no `libgssapi-krb5-2` (#267 keeps it minimal), so that probe made .NET's native security shim print two **unstructured, pre-Serilog** lines to stderr per connecting process (`Cannot load library libgssapi_krb5.so.2`) — non-fatal but it reads like a failure during deploys and lands badly in an OTLP sink. `PostgresConnectionString` now appends `GSS Encryption Mode=Disable` **unless the operator specified it**, detected by **presence, not value** (`prefer` is Npgsql's own default, so a value comparison would silently override an operator who asked for it) — via the *base* `DbConnectionStringBuilder`, since `NpgsqlConnectionStringBuilder.ContainsKey` returns true for every keyword it knows, defaults included. Appended **textually** after the TLS floor runs: a round-trip through the Npgsql builder would reorder/requote the operator's own string and throw on any keyword this Npgsql version doesn't know. Orthogonal to `sslmode` (#262 untouched) and to GSS *authentication* (a separate Npgsql path). Fixing the image instead was rejected — it adds package surface to a Trivy-gated image. **Verified by loader trace** (`LD_DEBUG=libs` against a scram-sha-256 Postgres): `Prefer` dlopens `libgssapi_krb5.so.2`/`libkrb5.so.3`/`libkrb5support.so.0` even though the server never offers GSS; `Disable` produces **zero** gssapi/krb5 loader activity and connects identically — so under password auth there is no residual GSS-*auth* probing either. End-to-end confirmation on the managed cluster (deploy repo) is the only step left.
- **Design-time migration connection fail-closed (#318):** `AppDbContextDesignTimeFactory` (used by `dotnet ef migrations add`/`database update`, never by the `migrate` verb, which uses the built host's own config) has **no default connection** — an unset/blank `CLUCKWORK_MIGRATIONS_CONNECTION` throws immediately, naming the variable, rather than falling back to a predictable `Host=localhost;...;Username=postgres;Password=postgres`. Every target is held to the **same allow-list TLS floor as a Production boot** (#261/#262, reused via `PostgresConnectionString.NormalizeAndValidate` — no second validator), except an explicitly acknowledged **loopback** development target: `CLUCKWORK_MIGRATIONS_ALLOW_INSECURE_LOOPBACK=true` permits plaintext, but only when the connection's host is `localhost`/`127.0.0.1`/`::1` (checked via `IPAddress.IsLoopback`) — set against any other host, it fails rather than silently widening scope.
- **Container image hardening (#267):** the runtime stage runs **non-root** (`USER $APP_UID`, uid 1654 — the base image's built-in `app` account); all three base images are **digest-pinned** (`@sha256:…`, kept current by Dependabot's `docker` ecosystem); a CI job builds the image and **Trivy** fails the build on a *fixable* HIGH/CRITICAL. `aquasecurity/trivy-action` is **SHA-pinned** — the action was compromised in the 2026-03 supply-chain incident, so pin the immutable commit, not the re-pointable tag (this is the standing rule for third-party actions). Keep the full glibc base (tzdata + ICU per #264); never chiseled/Alpine.
- **Container health probe (#266):** the runtime image **and** the compose `app` service carry a `HEALTHCHECK` that runs `dotnet Cluckwork.Api.dll healthcheck` — a **fourth run-then-exit CLI verb**, but **dispatched *before* host build** (`Program.cs`, `args is [HealthCheckCliCommand.Verb, ..]`), not via `CliDispatcher`: unlike `migrate`/`seed`/`recover-admin` — which operate on the built host — this one needs no host/DI/DB/config, so a 30s probe must not re-run the whole app startup or re-log boot warnings on every tick. The hardened image ships **no curl/wget** (#267), so the probe is **in-process**: it GETs `/health/ready` over loopback (port derived from `ASPNETCORE_URLS`, default `8080`) and exits `0` on a 2xx, `1` on any other status **or** an unreachable server (refused/timeout) — never a false green. So an instance whose `/health/ready` is 503 (DB down, migrations pending per #263) reports **unhealthy** and compose/an orchestrator stops routing to it. Its `ProbeAsync`/`DefaultReadyUrl` are unit-tested directly (no Docker). The CI `image` job (#267) now also **boots** the built image against a throwaway Postgres and asserts `/health/ready` goes green + the verb exits `0`, so an unbootable image fails CI instead of at deploy time — this is the **app-side of #266**. Deploy-side (the host's readiness path, wait-for-CI gate, post-deploy smoke) lives in the separate deployment/ops repo.
- **Transient-DB retry, and where it stops (#269):** `EnableRetryOnFailure` (`Database:Resilience:*`, bounded — never infinite) makes a managed-Postgres failover or a dropped pooled connection retry instead of 500ing. It applies to everything EF runs as a **self-contained unit**: every read, and every `SaveChanges` outside a user-initiated transaction. It deliberately does **NOT** apply to a transaction that spans work which is not replayable, because a transaction cannot be *resumed* after a connection loss, only replayed from the start. **Four** such regions exist and all run through **`SingleAttemptExecution`** — inside the execution strategy (EF requires that to open the transaction at all) but **executed exactly once**: `IdempotencyMiddleware`'s request-wide transaction, which covers `next(context)` (replaying the pipeline re-consumes the single-use in-memory #308 step-up grant, and makes a state transition answer 422 for a request that already succeeded); `AmbientTransaction`'s **owned** path (a failed attempt's entities stay tracked as `Added` — EF never detaches them — so a replay flushes duplicate users/audit rows/refresh tokens); and **`FirstRunAdminService`'s whole lock → check → create region** (#350 review). That last one is the subtle case, and the reason "the write is single-attempt" is *not* sufficient on its own: `bootstrap-admin` guards "exactly one first-run Owner" with a **session-scoped** `pg_advisory_lock`, but the Owner/conflict **reads** that decide whether to create were ordinary EF units, so the strategy retried *them*, and a retry **reconnects** — dropping the lock and walking into the create unguarded (two invocations with different emails could each mint an Owner). Wrapping the whole region makes it one non-replayed attempt; on top of that the create is gated on **proving** ownership — `pg_locks … AND pid = pg_backend_pid()` — because EF also silently *replaces* a connection it finds no longer usable, with no exception and no retry to intercept. That proof is asked **twice**, and the second one is the load-bearing one (#350 review round 3): a check that merely precedes the write still returns before `AmbientTransaction` establishes the create transaction, and a connection replaced in *that* gap leaves the `INSERT` running on a backend that never took the lock while the check's `true` describes a backend that no longer exists. So `FirstRunAdminService` **owns the create transaction itself** and re-proves ownership as the first statement *inside* it — same backend as the `INSERT` by construction, and from there a connection loss aborts the transaction instead of reconnecting invisibly; `IdentityProvider.CreateUserAsync` simply **joins** it (`AmbientTransaction`'s ambient path, exactly as under #307's request-wide transaction), so the lock check is never imposed on the Users page or the seeders that share that method. The pre-transaction check is kept in front as one cheap round trip that refuses before a password is even generated. All **fail closed** (`Bootstrap.LockLost`, exit 1): a refused `bootstrap-admin` is re-runnable and idempotent, a second farm Owner is not undoable. Never widen a session-scoped lock's region without all three parts, and never let a *comment* claim continuity a point-in-time check cannot deliver — that overstatement is exactly how the write-side gap survived two review rounds. The **fourth** region is `AccountLockout`'s `AccessFailedAsync` **save** (#350 review round 4) and it is the one that shows "unreplayable" is not a synonym for "transactional": there is no transaction here at all, just a plain `SaveChanges` — textbook self-contained unit. What disqualifies it is the CALLER. Neither password oracle is idempotency-wrapped (login is anonymous so the tenant gate skips it; `/auth/step-up` is on `ResponseNotCacheable`), so the strategy replays that save; on the ambiguous commit the replay re-issues the `UPDATE` under the stale Identity `ConcurrencyStamp`, matches 0 rows, and returns `IdentityResult.Failed` — which the reload loop (correctly present, for genuine parallel writers) reads as "lost a race", reloading the **already-incremented** user and incrementing it **again**. One wrong password would then cost two failed accesses and lock at roughly half the #128 threshold — the same consequence that shipped once already on a different path in #336. Wrap the **save** only; the reload is an ordinary replayable read and keeps its retry. The test to write for this shape is not "does it retry" but **"does one input produce exactly one effect"**. What recovers a write instead is the **client retrying with the same `Idempotency-Key`**, which #307 already makes exactly-once — plus, for the narrow ambiguous commit (Postgres committed, the ack was lost), the middleware probes its own claim and **replays the response it published** rather than reporting a failure. Never fix a symptom of this by re-widening the retry, and never by a blanket `db.ChangeTracker.Clear()` on a context a longer-lived caller may share (that regression dropped `SimulationDataSeeder`'s pending writes). **But refusing to replay is not the answer to every instance of this shape** (#350 review round 5, found by sweeping for it rather than by report). The general hazard is *an automatic replay sitting above a **stateful detector*** — a monotonic counter, a CAS stamp, a single-use claim — where the detector cannot tell "this request racing itself" from the real signal it exists to detect. `SingleAttemptExecution` is the right cure only when **the replay itself is observable**, as with `AccessFailedAsync`'s second durable increment. The three auth-token sites are the other kind: EF wraps their statements in one transaction, so a replay after a lost commit ack writes **nothing** and a replay after a rolled-back attempt simply succeeds — the defect was the **catch misreading** the replay's failure, not the replay. There the fix is to **ask the database whether this attempt's own work is durable**, keyed on evidence nobody else could have produced (the hash of a 256-bit token the attempt minted and has not yet handed out): `IdentityProvider.RefreshAsync` (rotation — the severe one: it is *unrecoverable by retrying*, because the rotation is durable so the caller's cookie holds a revoked token while its live replacement was delivered to nobody), `LoginAsync`'s token `INSERT`, and — a replay damaging a *later* statement rather than its own — `AccountLockout.ResetFailedAccessCountAsync`, where Identity swallows the concurrency loss into a discarded `IdentityResult` and leaves the user tracked under a superseded stamp, which the refresh-token `INSERT` sharing that `DbContext` then re-flushed (a **correct** password answered 409 and issued no token). Ask the database first and classify second: which exception EF raises depends on statement ordering inside the batch, so branching on the exception type reads a coin flip. All three fail **closed** — the probe proves durability or the original error stands, so a session is never invented — and #176's anti-fork guarantee is untouched, because a competing consumer mints its *own* random token and can never write our hash. Measured, and pinned from both sides in `RetryBoundaryTests`: wrapping the rotation in `SingleAttemptExecution` fixes the reported bug **and regresses** the ordinary fail-before blip from 200 to 409. One instrument note that cost a review round: `TransientCommandFaultInterceptor(afterExecution: true)` is the ambiguous commit **only for a single-statement save** (no auto transaction to roll back); for a multi-statement one it is a fail-*before*, and using it there yields a green test with the defect fully present — reach for `TransientCommitFaultInterceptor`.
- **A new Production boot guard must be taught to the sim harness (#370).** Every guard that fails the boot on missing/invalid config (#260 trusted proxies, #261/#262 the TLS floor, #316 the OTLP endpoint, #319 `AllowedHosts`, and whatever comes next) applies to `tools/simulation/` too — its `app` container runs **Production config on purpose**. That harness is **deliberately not in CI** (dev tooling; a GitHub job per push is out of proportion to 5 seconds of work), so **nothing will tell you when you break it**: every path into it is human-started — `reset.sh` directly, or `run-baseline.sh`, which calls `reset.sh` once per rep — and by 2026-08 four guards had landed without it and it could no longer boot `main` at all. **When you add or change a boot guard, or add/rename/retire a config key, update `tools/simulation/bootstrap.sh` + `docker-compose.sim.yml` in the same PR** and add the check to `tools/simulation/verify-harness.sh` — a ~0.1s self-check that `reset.sh` runs automatically before it wipes the volume, so a config defect fails in a tenth of a second instead of five minutes later at `/health/ready`. Treat a boot-guard PR that leaves the harness behind like a missing test. Note the harness satisfies guards **properly** (a concrete `AllowedHosts`, the documented plaintext opt-outs for its co-located sidecar) — never by disabling one.
- **SPA E2E lives in `tools/simulation/ui/` (#277/#385), not in `web/`.** Playwright drives the real built SPA against the **same** `seed --profile simulation` fixture k6 uses, so screens are populated rather than empty; `web/` stays Vitest + Testing Library. Three rules it exists to enforce, all of which have already caught something: **never hardcode a credential** (personas come from the git-ignored `.sim-cast.json`, same file k6 reads), **never hardcode English** (selectors resolve through the SPA's own en/es/tl catalogs — a missing key throws rather than falling back, so an es spec cannot assert an English string and pass), and **respect the farm clock** (`America/Chicago` is behind UTC, so a UTC "today" is refused both by the report endpoints and by every date input's `max`). The suite is **mutation-checked** — `npm run mutation` breaks a guarantee at the network boundary and requires the covering spec to go red; a red baseline aborts the run, because a mutation pass over an already-failing suite reads exactly like success. **CI is `workflow_dispatch` only** (`.github/workflows/e2e-smoke.yml`): it needs the whole stack plus a multi-minute seed, so the #370 warning applies to it too — nothing runs it for you, and the runner exercises a *second* browser path (downloaded Chromium; the NixOS dev box uses a system one, because the downloaded binaries do not launch there).
- **A change to a write contract must update its non-CI callers in the same PR (#394).** Same failure as #370 with a different trigger: adding a required field, tightening a validator, or changing an aggregate's state machine breaks automated callers of that endpoint that no test references. **Coverage is not uniform, and the gaps do not announce themselves — so work out which layer you changed before trusting any of it.** The **seeders are covered, but only to the handler/domain layer**: `DemoSeedTests`, `SimulationSeederTests`, `SeedCommandTests`, `SimulationSeedCommandTests` and `SimulationCrossDayRerunTests` invoke `DemoDataSeeder`/`SimulationDataSeeder` under `dotnet test`, so a fixture violating a new **handler or aggregate** invariant turns a PR **red**, and `SimulationDataSeeder`'s exact-count validation fails **closed** rather than certifying bad data. **A validator-only tightening is invisible to all five.** The seeders construct commands and call `HandleAsync` **directly** (`DemoDataSeeder` contains not one `ValidateAsync` call) while the endpoint runs `ValidateAsync` *before* the handler — so a fixture payload the real API would now reject keeps every seeder test green. If your change lives in a `*Validator`, the seeders prove nothing; push the rule into the handler/aggregate, or accept that CI is silent here. What is **uncovered outright** is `tools/simulation/k6/` and the Playwright specs in `tools/simulation/ui/` (`workflow_dispatch` only, #385) — and **do not expect a run to surface it either**: `bundles.js`'s `dailyEntryScreen` passes `[403, 409, 422]` as *tolerated*, `authedPost`'s `check()` counts a tolerated status as a **pass**, and `noteIfUnexpected` is handed `expected.concat(tolerated)` — so a new validator answering 422 makes the write workload stop landing entirely against a **100% green baseline**. `reset.sh` never runs k6 at all; it boots and seeds and stops. **So verify the request and status contract by reading it, not by running something.** Grep for callers of the endpoint you changed, check each payload still satisfies the new rule, and narrow the tolerated-status list if a status that used to mean "constrained write" now means "broken write". Read what each caller *does* first: `manager.spec.ts` records **and submits**, while `worker.spec.ts` deliberately **saves a draft and never submits** (its rerunnability contract depends on that), so a submit-contract change must not "fix" the worker spec into submitting. Treat a write-contract PR that leaves the uncovered callers behind like a missing test.
- **Production logs are compact JSON on stdout, and the base layer must stay argument-free (#404).** stdout is the only log destination — deliberately no file sink (the runtime image is non-root with no writable log volume, #267) and no vendor sink (that would name a hosting provider, against the deployment boundary below). Production selects `CompactJsonFormatter` so a collector can index `TraceId`/`AccountId`/status; Development supplies the human `outputTemplate`; the base `appsettings.json` Console entry carries **`Name` only, no `Args`**. That last part is the non-obvious invariant: `IConfiguration` merges layers per leaf and **cannot delete a key**, so a template left in the base file merges in *beside* Production's formatter — and Serilog.Settings.Configuration then **silently binds the `outputTemplate` overload and ignores the formatter**. No exception, no dropped sink; Production just keeps logging prose while the config file says otherwise. Verified by probe against Serilog.Settings.Configuration 10.0.0, and identical for the keyed and array `WriteTo` forms — the keyed form is used for a *different* reason (array merge is by index, which silently mismatches once a second sink is added). `"outputTemplate": null` is not an escape hatch either: it throws building the logger. Two further shapes to avoid: overlaying a *differently-keyed* Console entry duplicates every line rather than replacing it, and moving `WriteTo` wholesale into the environment files leaves any other environment with no sink at all. `ProductionLogFormatTests` pins all of it — including one test that drives `ReadFrom.Configuration` itself and asserts the emitted line, because every other assertion reads config leaves or the formatter directly and a **binder** regression would slip past all of them. **Consequence worth internalising before attaching anything to a log event: compact JSON serializes EVERY property, where the old prose template rendered six and silently dropped the rest.** Anything pushed via `BeginScope`/`LogContext`/`ForContext` now reaches the collector — that is how `/client-errors`' anonymous, caller-supplied `Stack`/`ComponentStack` went from latent to live (#273 owns the redaction answer and names that endpoint). Conversely, the log-forging risk that motivates stripping control characters from anonymous strings is **plain-text-sink-specific**: a JSON writer escapes them, so it applies to Development's template, not Production's — strip unconditionally anyway, since the format is a config choice the emitting code cannot see. OTLP **log** export stays a separate additive step (traces/metrics already go via #214/#316); stdout must keep working when the collector is down.
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

## Releases and image publishing (#351)

Two stages, deliberately separate: **CI publishes, the release PR versions.**

1. **Every merge into main** → the `publish` job in `ci.yml` pushes the image that
   run just built, Trivy-scanned and boot-tested, named by commit:
   `ghcr.io/<owner>/<repo>:sha-<commit>`. No version, no git tag. Idempotent per
   commit, so there is no ordering hazard and nothing to race — two merges publish
   two different names.
2. **Merging the "Release vX.Y.Z" PR** (maintained by release-please) → a **draft**
   release with a generated `CHANGELOG.md`; the already-published image for that
   commit is **promoted** to `:vX.Y.Z`; and only then is the release published.

- **Promotion is a server-side retag of an existing digest** (`docker buildx
  imagetools create`), never a rebuild. **Do not "simplify" it into a build step**:
  a second `docker build` yields different bytes and a different digest, so the
  image carrying a version would be one no scan or smoke test ever examined. That
  is the whole point of #351. **`--prefer-index=false` is load-bearing** — that flag
  defaults to *true*, and with a single source the default wraps the manifest in a
  new image index with a **different top-level digest**, which silently defeats the
  guarantee. Do not drop it.
- **The release stays a draft until its image is promoted**, and GitHub withholds
  the git tag for a draft. So a failed promotion leaves no tag and no public
  release, instead of a version pointing at nothing. Publishing (`--draft=false`)
  is the last step. Draft is safe for release-please's own bookkeeping because
  manifest mode reads the current version from `.release-please-manifest.json`, a
  committed file, not from tags.
- **Repair path, in two parts.** Re-running the push event never helps —
  release-please reports `release_created: false` for an already-created release,
  so promotion would be skipped forever.
  1. *If the commit has no image at all* — a `[skip ci]` anywhere in a commit
     message suppresses the push run entirely, and GitHub matches those keywords
     **anywhere** in the message, so one can reach the squashed release commit via
     a changelog entry. There is then no run to re-run. Dispatch **CI** with the
     exact sha; it rebuilds through the same gates and publishes. It refuses any
     commit that is not already an ancestor of `main`, so it cannot be used to
     publish arbitrary branch content.

     **Dispatch it from `main`** (the default ref selector; `gh workflow run`
     without `--ref`). The *sha input* names the commit to build, but the *ref*
     you dispatch from decides which `ci.yml` definition runs — and promotion
     verifies the attestation with `--source-ref refs/heads/main`. Dispatched
     from a branch, the rebuild succeeds and publishes, then refuses to promote,
     and the only clue is the error at promote time. That strictness is the
     point (see the provenance bullet below); just don't trip over it mid-incident.
  2. Then dispatch **Release** with the tag (and the exact sha if the release's
     `target_commitish` is a branch name rather than a commit) to promote and
     publish the draft. It refuses a tag whose release is **already published** —
     promotion retags and rewrites notes, so aiming it at a live version would
     repoint it — and when the release records a real commit, that commit is
     authoritative: a supplied sha may only agree with it, never override it.
- **The bump comes from conventional commits**, so PR titles are load-bearing —
  squash-merge puts the title on main as the commit subject. The mapping is
  **damped while below 1.0.0**, via two settings in `release-please-config.json`:
  `bump-minor-pre-major` ("breaking changes only bump minor if version < 1.0.0")
  and `bump-patch-for-minor-pre-major` ("feature changes only bump patch if
  version < 1.0.0"). So today `feat!:`/`BREAKING CHANGE` → **minor**, and
  `feat:` along with **everything else** → **patch**.

  **Both are required and they are not interchangeable.** `bump-minor-pre-major`
  alone still lets a `feat` take the minor digit; the second setting is what keeps
  features on patch.

  **`initial-version` is a third, separate lever, and the two bump settings do not
  cover it.** The *first* release computes no bump at all — `Strategy.initialReleaseVersion()`
  returns `Version.parse(this.initialVersion)` or, absent that, a hardcoded
  `1.0.0`. So a fresh repo proposes **1.0.0** no matter what the pre-major
  settings say. That is exactly what happened here twice: PR #372 proposed
  `release 1.0.0`, adding the two bump settings changed nothing, and PR #374
  proposed `1.0.0` again. `"initial-version": "0.0.1"` is what fixes the first
  release; the bump settings govern every release after it. Don't diagnose one as
  the other.

  **This mapping changes silently at 1.0.0**, when both settings stop applying and
  the conventional defaults resume (`feat:` → minor, breaking → major). Reaching
  1.0.0 should therefore be deliberate — a `Release-As: 1.0.0` footer when you mean
  it — not a side effect.

  Note that `hidden: true` in `changelog-sections` only suppresses a type in the
  changelog *text*; it does **not** make it unreleasable. `DefaultVersioningStrategy`
  returns `PatchVersionUpdate()` for any commit set with no feat/breaking, and the
  only early exit is "zero conventional commits" — so a `chore:`-only merge does
  bump the patch digit. It lands in the pending release PR rather than in a
  release, so it costs a number, not a deploy.
- **Deploy by digest, never by tag**, and treat *obtaining* the digest and
  *verifying* it as two separate problems — the deploy side needs both, and one
  does not imply the other.
  - **Obtain:** every release carries an **`image.json` asset**
    (`gh release download <tag> -p image.json -R <owner>/<repo>`) with `image`,
    `digest`, `reference`, `tag`, `commit`, `repository`. A release asset, not a workflow
    artifact — it never expires and needs no `actions:read` on this repo. The
    digest is also in the notes for humans. **Do not make the deploy side parse
    prose, and do not have it resolve a tag.**
  - **Verify:** `ci.yml`'s publish job writes a **build-provenance attestation**
    (#354) against the pushed digest, stored as an OCI referrer beside the image
    (`push-to-registry: true`). The deploy side runs:

    ```bash
    # oci:// needs registry credentials; GHCR ignores the username
    echo "$GITHUB_TOKEN" | docker login ghcr.io -u x-access-token --password-stdin
    gh attestation verify oci://<image>@<digest> \
      --repo <owner>/<repo> \
      --signer-workflow <owner>/<repo>/.github/workflows/ci.yml \
      --source-ref refs/heads/main \
      --bundle-from-oci
    ```

    **All three flags are load-bearing and none is the default**, and each is
    easy to drop without noticing anything break — a missing one weakens the
    check silently rather than failing it.
    - `--bundle-from-oci` makes `gh` read the registry copy; without it the
      bundle is fetched from the **GitHub API**, so `push-to-registry` goes
      unused and the "no GitHub access needed" property is lost.
    - `--signer-workflow` binds the identity to the *workflow*; with `--repo`
      alone, **any** workflow here holding `attestations: write` satisfies the
      check.
    - `--source-ref` binds it to the *ref*, and this is the subtle one:
      `--signer-workflow` pins the workflow's **path**, and `workflow_dispatch`
      runs the workflow **definition** from whatever ref is selected. So without
      it, anyone able to push a branch could edit `ci.yml` there, dispatch it,
      publish and attest arbitrary bytes, and still match a path-only check.

    Consequence worth knowing: **a CI repair dispatch must run from `main`.**
    Dispatched from a branch it produces an image that will not promote, on
    purpose. Note also the consumer still authenticates to the **registry** for
    an `oci://` subject; the saving is no GitHub API access to this repo, not no
    credentials at all.

  **Holding a digest is not the same as knowing where it came from.** A digest
  identifies bytes exactly and cannot be moved — but bytes pushed by hand have a
  perfectly valid digest too, and a gate that checks digest *syntax* accepts
  them. Only the attestation distinguishes CI's bytes from anything pushed with
  `packages: write` (realistically a leaked token). That is why obtaining and
  verifying are listed as two steps and not one.

  **Where the fail-closed actually comes from.** `ci.yml` attests *before*
  uploading the digest artifact, so a failed attestation leaves no artifact —
  but that is a **within-a-run** property only, and it is easy to overclaim.
  Promotion finds the artifact by **name**, repo-wide, taking the first
  unexpired match, so an artifact from an earlier run of the same commit would
  still satisfy it. What makes it fail closed at *release* level is that
  `release-please.yml` **verifies the attestation before it retags**. Keep both;
  never let the ordering stand in for the check. And do not make either
  `continue-on-error` — an attestation nobody can rely on is worse than none,
  because it reads as coverage.

  **There are two gates, and they do not have the same strength — don't conflate
  them.**

  - **Promotion's check** (the verify in `release-please.yml`) is *inside a
    branch-editable workflow*. `workflow_dispatch` runs a workflow's definition
    from the selected ref, so someone who can push a branch can dispatch a copy
    with the verify deleted, running with that job's `contents: write` +
    `packages: write`. No check written inside a branch-editable workflow closes
    that — the attacker edits the check. The controls there are repo-level: who
    may push branches, who may run workflows, branch protection on `main`.
  - **Deploy-side verification** is *not* subject to that, and is the stronger
    of the two. It runs outside this repo, against the registry, and a
    branch-built image carries an attestation naming **that branch** as its
    source ref — which `--source-ref refs/heads/main` rejects. So a branch writer
    cannot get *their own bytes* deployed.

    But it proves **origin, not currency**, and that gap is reachable: the
    verify command answers "did this repo's CI on `main` build these bytes",
    **not** "are these the bytes this release promoted". Anyone able to rewrite
    a published release's `image.json` can point it at an **older, genuinely
    attested** digest. The deploy side reads `.reference` from that file,
    verifies it, and it passes — a **downgrade**, using bytes that really were
    CI's. Nothing in an attestation binds a digest to a release.

    A tag/digest comparison narrows that and is worth doing: check that
    `:vX.Y.Z` in the registry resolves to the digest **you verified**, and refuse
    if they differ. Compare against the digest in `reference` — the one the
    `oci://` subject named — and **never** against the asset's separate `digest`
    field. CI writes `reference` as `image + "@" + digest`, but nothing forces a
    *rewritten* asset to keep them equal: an attacker sets `reference` to an old
    attested digest and leaves `digest` matching the current tag, and a check
    reading `digest` compares the current digest to itself, passes, and deploys
    the old one. **Be precise about who that stops.** It defeats an
    attacker who can rewrite the release asset *and nothing else* — a leaked
    `contents: write` credential. It does **not** defeat the branch-dispatch
    actor above, who holds `contents: write` **and `packages: write`** (both are
    on the `promote` job, and a dispatched copy declares its own permissions).
    That actor moves the tag onto the older digest with the same
    `imagetools create` promotion itself uses, and the two then agree. Moving a
    tag is an ordinary registry operation — this bullet's own heading says so.

    So that actor is bounded by repo-level controls, exactly as promotion's
    check is: who may push branches, and who may run workflows. The registry
    half is separately closable with **immutable tags for `v*`** on the package —
    #354's third acceptance criterion, deliberately not in this PR because it is
    a registry setting rather than code.

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

  **The paragraph directly above is the canonical statement of the boundary** —
  the one beginning "Net, stated at exactly the strength". `README.md` and the
  `ci.yml` comment carry a summary and point here rather than restating it,
  because successive corrections to this claim repeatedly updated one copy and
  left the others contradicting it. If you correct it again, correct it there
  and check the two summaries still agree.
- **Promotion reads the digest from CI's own run artifact, never by resolving
  `:sha-<commit>`.** That tag is mutable by anyone holding `packages: write`, and
  the merge commit is public seconds after merge while CI needs minutes to push —
  so resolving the tag would accept the first manifest to appear under that name,
  and a forged push in that window would be promoted and written into the release
  notes as the thing to deploy. **Do not "simplify" the promote step back into a
  registry tag lookup.** (That closed "there is no digest to deploy"; the
  provenance half — proving the digest is CI's — is the attestation above, #354.)
- **Adding a CI job that should gate a release? Add it to `publish.needs`.** The
  digest artifact is what promotion accepts as proof, and it proves exactly what
  `publish.needs` in `ci.yml` covers — no more. A job outside that list can be
  **red while publish still records a digest**, and promotion will then certify
  bytes that failed it. Nothing enforces the list: `needs` is hand-kept, and a new
  job defaults to *not* gating, which is the dangerous default. Treat "is it in
  `publish.needs`?" as part of adding any gating job.
- **Watch the version on the release PR, not just the changelog.** A
  `Release-As: X.Y.Z` footer on any commit reaching main overrides the computed
  version, and squash-merge can be configured to put a PR *body* into the commit
  body — so a contributor can force a version jump from a PR description. The
  human merging the release PR is the control; its title states the version.
- **Config lives in three files**, all machine-maintained — `release-please-config.json`,
  `.release-please-manifest.json`, and `version.txt`. **Never hand-edit the manifest
  or `version.txt`**; release-please owns them and a manual edit desynchronises the
  version it believes from the tags that exist.
- **The release PR is opened with a GitHub App token, not `GITHUB_TOKEN`** — and
  both reasons are load-bearing, so do not "simplify" it back.
  1. `GITHUB_TOKEN` **cannot open a pull request at all** unless the repo-wide
     *"Allow GitHub Actions to create and approve pull requests"* setting is on.
     That is how this first shipped, and the first real run failed with
     `GitHub Actions is not permitted to create or approve pull requests` after
     release-please had already pushed its branch.

     **Do not describe the off setting as the safeguard** — that setting governs
     `GITHUB_TOKEN` *only*, and does not constrain an App installation token,
     which is the whole reason this fix works. The trade is about **what a job
     must hold** to reach PR-write, not about capability existing at all:

     - **Setting on** removes a repo-wide *policy gate* on `GITHUB_TOKEN`. It
       grants nothing by itself — a job must still declare `pull-requests:
       write`, and the repo default is `read`. From then on any job that
       declares it can create and approve PRs with the ambient token and **no
       secret**. Scope that to runs whose `GITHUB_TOKEN` is write-capable at
       all: pushes, and `pull_request` runs from **same-repository** branches. A
       **fork** PR (this repo is public and allows forking) or a **Dependabot**
       PR receives a read-only token whatever the workflow declares, unless the
       separate fork / Dependabot write-token policies are turned on.

       That carve-out keys on the **event**, not on who triggered it — it covers
       the *direct* `pull_request` run only. A **`workflow_run`** fired off the
       back of a fork or Dependabot PR runs from the default branch with a
       normal writable-per-`permissions` token and full secret access, which is
       precisely what `dependabot-lockfix.yml` depends on to read the App key
       (see the design doc). So if that workflow — or any future privileged
       follow-on — ever declared `pull-requests: write`, turning the checkbox on
       would make it PR-write-capable with **no** fork/Dependabot policy
       involved. The exposure is not "any contributor can approve"; it is every
       job, present and future, that asks for the scope on a write-capable
       event.
     - **App token** leaves that gate closed, so no `GITHUB_TOKEN` anywhere can
       do it, and PR-write is reachable only by a job that explicitly references
       the App private key.

     **Do not restate this as "the capability lives in one job".** The private
     key is a *repository secret* and `permission-*` caps the returned token, not
     the key — so **any** job referencing that secret can mint the App's full
     grant. Two do today (release-please, and lockfix's `commit`). What makes
     that safe is that neither executes PR-controlled code; it is not a function
     of job count.

     `Pull requests: write` is indivisible — opening and approving a PR are the
     same scope — so **the release token can approve a PR**, and only the pinned
     action's behaviour stops it. Currently inert: `main` requires **zero**
     approving reviews and has **no** required status checks (verified against
     the live repo, 2026-08-02), so there is nothing for a rogue approval to
     satisfy. It stops being inert the day required reviews are enabled, which is
     why that capability belongs behind a secret rather than behind a declared
     permission any workflow can ask for.
  2. GitHub does not trigger workflows for anything `GITHUB_TOKEN` opens or
     pushes (recursion prevention), so a `GITHUB_TOKEN` release PR carries no
     `pull_request` checks. An App identity is exempt, so the release PR is
     built, tested and scanned like any other.

     **Do not inflate this into "the version commit would ship unverified".** It
     would not, and the gate that prevents it is elsewhere in this design:
     merging the release PR is a *human* action, so it produces an ordinary
     `push` to `main` that runs CI in full, and `promote` refuses to run without
     `published-digest-<sha>`, which `publish` records only after
     `build-and-test`, `web` and `image` have all passed. **Release verification
     rests on that artifact gate, not on PR checks.** What this reason buys is
     narrower and still worth having: seeing red *before* the merge rather than
     after, and not deadlocking releases the day required status checks are
     enabled on `main` — a checkless PR could never satisfy them.

  The token is **downscoped per permission** (`permission-contents`,
  `permission-pull-requests`, `permission-issues`) rather than taking whatever the
  installation holds. `issues: write` is not incidental — it is what creates and
  applies the `autorelease: *` labels release-please uses to recognise its own
  merged PR. The **same App also backs `dependabot-lockfix.yml`**, so that
  workflow pins `permission-contents: write` on its own mint: without the cap, the
  PR/issue grants added here would ride along into the token held by the job that
  pushes to a Dependabot branch. **Any new consumer of this App must downscope the
  same way** — the installation's permissions are a ceiling, not the intended
  grant. Note the failure mode: omitting `permission-*` entirely does not mint a
  *narrow* token, it mints the **union of everything the App holds**, silently and
  with no warning. Nothing enforces the cap today, which is #368.

  Setup: the App needs **Contents: RW, Pull requests: RW, Issues: RW**, and the
  repo needs `LOCKFIX_APP_CLIENT_ID` / `LOCKFIX_APP_PRIVATE_KEY` (shared with
  lockfix). Fails closed — a missing secret fails the mint step, so no release is
  cut with a fallback token.

  **`client-id`, not `app-id`.** `create-github-app-token` v3 deprecated `app-id`
  (`deprecationMessage: "Use 'client-id' instead."`), so every run annotated a
  warning until both mints moved over. They are *different values* for the same
  App — the App ID is a number, the Client ID a string (`Iv23li…`), both on the
  App's General page — so this needed a new secret rather than editing the old
  one. Neither is a credential; only the private key is. Don't "fix" a future
  deprecation by pointing `client-id` at `LOCKFIX_APP_ID`: the mint fails.
- Package visibility and the host's pull credential are **deploy-side** concerns
  (cluckwork-deploy#6), not this repo's.

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
