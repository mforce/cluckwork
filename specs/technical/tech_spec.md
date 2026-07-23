# Cluckwork — Technical Specification (v1)

**Companion to:** functional spec `specs.md` v4.2
**Status:** initial technical design for Phase 1 build
**Audience:** implementing developer(s) + AI coding agents
**Name:** *Cluckwork* (working name; the system the functional spec calls "Egg Farm Manager" — the broader name reflects the planned pullet/broiler/meat/breeder modules)

This document defines *how* the system in `specs.md` is built. It does not restate functional behavior; it references functional sections (e.g. §8.5, §10.9.1, §23) where the two interlock. Where the functional spec already made a decision (money as integer minor units, idempotency keys, `account_id` on every business table, daily-entry uniqueness, egg-lot `FOR UPDATE` + `version`), this document treats it as fixed and builds on it.

---

# 1. Key Decisions (read this first)

These are the load-bearing choices. Everything else follows from them. Challenge these before reading further if you disagree.

| # | Decision | Choice | Rationale |
|---|---|---|---|
| KD-1 | Backend | .NET 10 (LTS), C#, Minimal APIs, vertical-slice architecture | Stated requirement; LTS; slices map 1:1 to functional use cases |
| KD-2 | Database | PostgreSQL primary, **provider-agnostic via EF Core 10** | Stated requirement. "Swappable with deliberate effort," not "one-line toggle" (see §5) |
| KD-3 | Tenancy | **Single-tenant deploy now, multi-tenant-ready by construction** | Self-host today; SaaS later without a rewrite. Achieved via mandatory `account_id` scoping + global query filter (see §4) |
| KD-4 | Offline | **Local-first for data capture only**; allocation/financial stay online | Daily entry must work in low-connectivity barns. Allocation needs server row locks and cannot be safely queued offline (see §6) |
| KD-5 | Auth | **ASP.NET Core Identity + JWT (access + refresh)** behind an `IIdentityProvider` abstraction | Zero extra infra, free, self-hosted; abstraction preserves swap to Keycloak/Entra for SaaS (see §7) |
| KD-6 | Frontend | **React + TypeScript PWA**, typed client generated from OpenAPI | Best offline/PWA + forms + charting ecosystem; OpenAPI codegen recovers end-to-end type safety (see §8) |
| KD-7 | Mapping/validation | **Mapperly** (source-gen) + **FluentValidation** | Avoids MediatR/AutoMapper commercial licensing; compile-time-checked mapping |
| KD-8 | Hosting reference | Docker Compose on homelab behind Traefik; cloud-portable | Matches existing infra; no cloud lock-in |

---

# 2. Architecture Overview

## 2.1 Style

Vertical-slice architecture over a thin Clean Architecture base. Each functional use case (UC-030 "record daily entry," UC-072 "confirm sale," etc.) is a self-contained slice: endpoint + handler + request/response models + validators, co-located in a feature folder. Cross-cutting concerns (auth, tenancy, logging, idempotency) live in shared middleware/pipeline, not in each slice.

Why not heavy CQRS/MediatR ceremony: the build is solo + AI-assisted at modest scale. Slices give organization and testability without a mediator indirection layer (which is also now a commercial dependency). Handlers are plain classes invoked directly from Minimal API endpoints.

## 2.2 Dependency rule

```text
Api  ──►  Application  ──►  Domain
 │             │
 └──►  Infrastructure  ──► (EF Core, Identity, providers, external)
```

- `Domain` depends on nothing. Entities, value objects, domain rules (e.g. withdrawal restriction logic, money value object).
- `Application` depends only on `Domain`. Use-case handlers, port interfaces (`IRepository`, `IIdentityProvider`, `IClock`, `IUnitOfWork`), DTOs.
- `Infrastructure` implements `Application` ports. EF Core, migrations, Identity, provider config, file/PDF/Excel, background jobs.
- `Api` composes everything. Minimal API endpoints, middleware, DI wiring, OpenAPI.

No layer below `Api` references ASP.NET types. No layer references Npgsql except `Infrastructure`.

## 2.3 Solution layout

```text
Cluckwork.sln
src/
  Cluckwork.Domain/
    Common/            (Entity, AggregateRoot, ValueObject, Money, Result)
    Flocks/  Eggs/  Sales/  Inventory/  Health/  Feed/  Accounts/
  Cluckwork.Application/
    Common/            (ports, behaviors, pagination, errors)
    Features/
      DailyEntries/    (RecordDailyEntry/, EditDailyEntry/, ...)
      Sales/           (ConfirmSale/, ...)
      EggLots/  Inventory/  Feed/  Health/  Reports/  Accounts/
  Cluckwork.Infrastructure/
    Persistence/       (AppDbContext, configurations, interceptors, migrations/)
    Identity/          (IdentityProvider, JWT, token service)
    Providers/         (Postgres/, SqlServer/  — provider-specific config only)
    Jobs/  Files/  Time/
  Cluckwork.Api/
    Endpoints/         (feature endpoint groups)
    Middleware/        (tenant resolution, idempotency, exception → ProblemDetails)
    Program.cs
tests/
  Cluckwork.Domain.Tests/         (pure unit)
  Cluckwork.Application.Tests/     (handler unit, in-memory ports)
  Cluckwork.Api.IntegrationTests/  (Testcontainers Postgres, full stack)
frontend/
  (see §8)
deploy/
  docker-compose.yml  traefik/  .env.example
```

---

# 3. Backend Stack & Conventions

## 3.1 Libraries (all free / no commercial-license trap)

```text
Web:           ASP.NET Core 10 Minimal APIs
ORM:           EF Core 10 (Npgsql provider primary)
Mapping:       Mapperly (source generator)
Validation:    FluentValidation
Auth:          ASP.NET Core Identity + JWT bearer
Logging:       Serilog (structured) → console/file; OTLP exporter optional
Telemetry:     OpenTelemetry (traces + metrics), ASP.NET + EF Core instrumentation
Health:        Microsoft.Extensions.Diagnostics.HealthChecks
Jobs:          BackgroundService + PeriodicTimer + durable job table
               (no scheduler library needed — see §9; Quartz.NET only if
               complex cron or clustered scheduling later emerges)
PDF export:    QuestPDF — FREE under its Community license ONLY for orgs under
               ~$1M USD annual revenue; paid above that. Free-fallback if that
               threshold is ever crossed: PdfSharpCore (MIT), or HTML→PDF via a
               headless browser. (Verify the current revenue threshold at build
               time — vendor terms change.)
Excel export:  ClosedXML (MIT)
Testing:       xUnit, FluentValidation.TestHelper, Testcontainers (Postgres), WebApplicationFactory
API docs:      built-in OpenAPI (Microsoft.AspNetCore.OpenApi)
```

Explicitly avoided: MediatR, AutoMapper, MassTransit, FluentAssertions (all moved to commercial dual-licenses in 2025). Where a mediator is wanted later, a source-generated one or a hand-rolled dispatcher is the substitute.

## 3.2 API conventions

- **Versioning:** URL segment `/api/v1/...`. Additive changes don't bump; breaking changes do.
- **Errors:** RFC 9457 `ProblemDetails` for every non-2xx. Validation failures → 400 with per-field errors, each carrying a stable machine-readable code (FluentValidation `ErrorCode`) so clients can localize (functional §4.5); message text is an English fallback, not a contract. Domain rule violations → 422 with a machine-readable `code`. Other response classes (auth, idempotency, concurrency) carry no localization contract.
- **Pagination:** cursor-based for ledgers/lists (`?cursor=&limit=`); never offset for large tables.
- **Idempotency:** all write endpoints honor an `Idempotency-Key` header (functional §23). Key + account + endpoint hashed → stored result; replays return the original response. This is the backbone of safe offline retry (§6).
- **Concurrency:** optimistic via `version`/row-version token on mutable aggregates (functional §10.9.1 already mandates `egg_lots.version`). Mismatch → 409 with current state.
- **Money:** integer minor units only, carried with `currency_code` + `currency_minor_unit` per row (functional §4.6). A `Money` value object in `Domain` enforces same-currency arithmetic.
- **Time:** UTC at rest for audit timestamps; operational dates in farm timezone (functional §3). `IClock` injected, never `DateTime.Now`.

## 3.3 Transactions & the unit of work

Each command handler runs in one transaction (`IUnitOfWork.SaveChangesAsync`). The sale-confirmation handler implements functional §10.9.1 exactly:

```text
BEGIN
  SELECT candidate egg_lots ... FOR UPDATE     (pessimistic lock)
  re-read quantity_available
  validate allocation still fits
  insert sales_order_item_egg_allocations
  insert egg_inventory_movements
  update egg_lots.quantity_available + version
COMMIT  (on conflict / insufficient stock → rollback, 409)
```

Pessimistic lock here is deliberate: allocation is a correctness-critical, short-lived, contended write. Everywhere else uses optimistic concurrency.

---

# 4. Multi-Tenancy (self-host now, SaaS-ready)

Goal: ship single-tenant, carry zero code debt that blocks multi-tenant later. Approach: **shared schema, `account_id` discriminator, enforced globally** — never per-feature.

## 4.1 Model

- Every business table already carries `account_id` (functional spec). This is the tenant key.
- One self-hosted deployment today runs exactly one account row. The code never assumes that.
- The functional spec models the §4.5 localization settings (timezone, locale, currency, unit system, first day of week, format overrides) on `farms`. There is no farms aggregate yet — `SeedDefaults.FarmId` is a stand-in id — so while a deployment runs exactly one farm those columns live on the `accounts` row (#123). That is where `IFarmClock` already reads the timezone from and where financial rows already copy the currency at creation, so no consumer changes when a real farms table arrives: the settings move, the ports do not.
- The farm **logo** (#123) is the one thing deliberately kept off that row. `accounts` is read on every dated and every priced operation (`IFarmClock`, and every handler that snapshots the currency), and EF generates a `SELECT` over all mapped columns — so a `bytea` there would be detoasted and put on the wire for all of them. It lives in `farm_logos`, one row per farm, touched only by the logo endpoints. Its read port is split by cost: a projection without the bytes answers "is there one, and which", and only the serve endpoint's cache-miss path selects `content`.
- Tenant strategy is **shared-database/shared-schema**. This is the most SaaS-portable and is invisible in single-tenant mode (one account). DB-per-tenant is deliberately *not* chosen — it complicates migrations and is unnecessary at expected scale.

## 4.2 Enforcement (the important part)

```text
1. A TenantContext (scoped) is resolved from the authenticated principal's
   account_id claim by middleware, before any handler runs.
2. EF Core global query filter on every entity:
       modelBuilder.Entity<T>().HasQueryFilter(e => e.AccountId == _tenant.AccountId)
   → every read is automatically tenant-scoped; a missing WHERE can't leak data.
3. A SaveChanges interceptor stamps account_id on every inserted row from
   TenantContext → writes can't be mis-tagged.
4. Integration tests assert cross-tenant isolation (seed two accounts, assert
   account A can never read/write account B) — this is a required test, not optional.
```

When SaaS arrives: tenant resolution changes from "the one configured account" to "from subdomain/host/JWT," and an account-provisioning + onboarding flow is added. The data layer, query filters, and handlers do **not** change. That is the whole point of doing this now.

## 4.3 What is deferred (do not build in Phase 1)

Per-tenant billing, subdomain routing, tenant self-signup, plan/quota enforcement, per-tenant config overrides. Reserved; not blocked.

---

# 5. Data Access & Provider-Agnostic Strategy

The requirement is provider portability. EF Core delivers it *if disciplined*. Honest target: swapping Postgres → SQL Server/SQLite is a **deliberate, tested task**, not a runtime switch.

## 5.1 Rules to stay portable

```text
- All EF/provider code lives in Infrastructure. Domain/Application never see Npgsql.
- No provider-only column types: NO jsonb, NO Postgres arrays, NO hstore.
  Use EF owned types / value conversions for structured data instead.
- No raw SQL with provider-specific syntax in handlers. If raw SQL is ever
  needed, isolate it behind a port with one implementation per provider.
- Concurrency tokens via EF abstraction (IsRowVersion / xmin mapped generically),
  not a hand-written Postgres-only column.
- Decimal/money is integer minor units — provider-neutral by construction.
- Booleans, dates, enums: use EF conventions, not provider literals.
```

## 5.2 Migrations are the real boundary

Generated migration SQL differs per provider. Do **not** pretend one migration set serves all.

```text
- Postgres is the primary, with its own migrations assembly + history.
- Adding a second provider = a separate migrations assembly generated against
  that provider, selected at startup by configuration.
- Provider is chosen via config:  "Database:Provider": "Postgres" | "SqlServer"
  → DI registers the matching DbContext options + migrations assembly.
```

## 5.3 Provider selection shape

```text
Infrastructure/Providers/
  Postgres/   PostgresDbContextConfigurator.cs   (UseNpgsql, migrations asm)
  SqlServer/  SqlServerDbContextConfigurator.cs  (UseSqlServer, migrations asm)
A single IDbProviderConfigurator is resolved from "Database:Provider".
```

## 5.4 Testing implication

Integration tests run against **real Postgres via Testcontainers**, not SQLite. SQLite's SQL semantics differ enough (collation, concurrency, type affinity) to hide real bugs. If a second provider is added, its own Testcontainers suite runs the same scenarios.

---

# 6. Offline-First / Sync Architecture (KD-4)

The decision: **data capture works offline; shared-state mutation does not.**

## 6.1 What is offline vs online

```text
OFFLINE-CAPABLE (queue + sync):
  - Daily production entry: eggs by grade, cracked/dirty/discard, mortality,
    feed usage, water usage, weight samples
  - Draft daily entries
  - Reading recently-cached reference data (farms/houses/flocks/grades)

ONLINE-ONLY (require live server):
  - Sales order confirmation + egg-lot FIFO allocation (needs FOR UPDATE lock)
  - Inventory reconciliation
  - Manual egg-lot adjustments
  - Payments / financial confirmation
  - Medication events that set withdrawal (safety-critical)
  - User/role administration
```

This split is not arbitrary — it mirrors functional §8.5, which already scopes the fast daily-entry path to exclude medication, reconciliation, and manual adjustments. Anything that allocates shared inventory or touches money cannot be optimistically applied offline because two devices could allocate the same physical eggs; that conflict can only be resolved by a server holding a lock.

## 6.2 Pattern: application-layer mutation queue (not a DB sync engine)

Sync happens at the **application layer through the existing idempotent API**, not by replicating Postgres to the client. Reason: all domain rules (withdrawal enforcement, KPI calc, validation, allocation) live in the .NET API. A Postgres-direct sync engine (ElectricSQL, PowerSync) would stream rows past that logic and bypass it. So the client treats the API as the sync target.

```text
Client (React PWA)
  ├─ Local store: IndexedDB (via Dexie)
  │    ├─ cache:  reference data needed offline (read-optimized)
  │    └─ queue:  pending mutations (the outbox)
  ├─ Write path (offline-capable forms only):
  │    1. validate locally (same Zod schema as online)
  │    2. write optimistically to local cache  → UI shows "pending sync"
  │    3. enqueue mutation { clientId(UUID=Idempotency-Key),
  │                          naturalKey(account/farm/house/flock/date),
  │                          baseVersion, payload, timestamp }
  ├─ Sync engine (foreground queue):
  │    - flush FIFO on: `online` event, app focus, periodic timer
  │    - each item POSTed with its Idempotency-Key header
  │    - exponential backoff on failure; Background Sync API as enhancement
  └─ on success: mark local record synced; on conflict: see §6.4
```

Alternatives considered and rejected for Phase 1: **ElectricSQL/PGlite** and **PowerSync** (bypass server domain logic; add infra); **CRDTs (Yjs/Automerge)** (daily entry is single-writer per natural key — no collaborative-editing problem to justify the complexity); **RxDB** (heavier than needed for a scoped offline surface — revisit only if the offline surface grows beyond daily capture).

## 6.3 Why this is safe: idempotency + natural key

- The client generates the `Idempotency-Key` (a UUID) *before* the first send attempt and reuses it on every retry. A flushed-but-unacked mutation that resends is deduplicated server-side (functional §23). No double-posted daily entries from flaky reconnects.
- The daily-entry **uniqueness constraint** `UNIQUE(account_id, farm_id, house_id, flock_id, date)` (functional spec) is the server's backstop: even two *different* devices creating the same logical entry collide deterministically at the DB, not silently.

## 6.4 Conflict resolution

Keyed on entry state (reuses the functional daily-entry state machine: draft / submitted / locked / manager_adjusted / voided):

```text
- Same natural key, server has no entry yet      → accept (normal case).
- Same natural key, server entry is DRAFT,
  client baseVersion == server version           → accept, bump version.
- Same natural key, server entry is DRAFT,
  client baseVersion < server version (another
  device synced first)                           → last-write-wins on the entry,
                                                    surface the superseded values
                                                    to the user for review (do not
                                                    silently discard).
- Same natural key, server entry SUBMITTED/LOCKED → reject the offline mutation,
                                                    flag for manager review →
                                                    manager_adjusted path.
```

Field workers get a clear per-record status: Pending / Synced / Needs review. No silent data loss; contested entries route to the manager flow the functional spec already defines.

## 6.5 Offline auth

A previously-authenticated user keeps a cached, unexpired JWT and can work offline within its window; the refresh token renews on reconnect (§7). **First** login requires connectivity. Access-token lifetime is tuned long enough for a normal field shift; refresh handles the rest.

## 6.6 Phasing note

Recommend Phase 1 ship **online-first** daily entry (queue present but thin: buffer-and-retry), then harden into full local-first cache + conflict UI in Phase 1.5. This de-risks the walking skeleton without changing the architecture — the queue and idempotency are there from day one; the richer local cache and review UI come second.

---

# 7. Authentication & Authorization (KD-5)

## 7.1 Choice

ASP.NET Core Identity (user store in the same Postgres) issuing **JWT access tokens (short-lived) + refresh tokens (rotating)**, all behind an `IIdentityProvider` port in `Application`. Identity provides password hashing, lockout, MFA, email confirmation out of the box.

Why not Keycloak/Entra/Auth0 now: extra infra (Keycloak) or external dependency + per-MAU cost and a cloud tie for barn-side login (Entra/Auth0). For self-host-now at modest scale, Identity is the lowest-friction path.

## 7.2 SaaS-ready escape hatches

```text
- IIdentityProvider abstraction: swapping to an external OIDC IdP (Keycloak realms
  per tenant, or Entra) later means a new implementation, not domain changes.
- If the system must BECOME an OAuth/OIDC server (third-party access, SSO),
  layer OpenIddict on top of Identity — still self-hosted, still free.
```

## 7.3 Authorization model

Maps to the functional permission matrix (§2.2–2.3):

```text
- Roles: Owner, Manager, Worker, ReadOnly (per functional spec), scoped per farm.
- Claims: account_id (tenant), user_id, roles, farm scope.
- Policy-based authorization in the API; handlers receive a scoped principal.
- Tenant claim (account_id) feeds TenantContext (§4.2) — auth and tenancy are linked:
  a token for account A can never resolve a TenantContext for account B.
```

## 7.4 Token handling

```text
- Access token: JWT, short TTL (tuned for a field shift), signed (asymmetric keys).
- Refresh token: rotating, stored hashed server-side, revocable.
- Frontend: access token in memory; refresh token in a secure, httpOnly cookie
  (web) — avoids localStorage token theft. PWA caches the access token for the
  offline window only.
```

---

# 8. Frontend Architecture (KD-6)

## 8.1 Stack

```text
Framework:      React + TypeScript (Vite)
Routing:        TanStack Router  (or React Router)
Server state:   TanStack Query   (cache, retries, offline mutation persistence)
Forms:          React Hook Form + Zod
Local store:    IndexedDB via Dexie (offline cache + mutation queue)
UI:             Tailwind CSS + shadcn/ui
Charts:         Recharts (or visx for custom dashboard viz)
API client:     generated from OpenAPI (Kiota or NSwag) → typed client + types
PWA:            service worker (Workbox) for app shell + offline; Web App Manifest
```

## 8.2 End-to-end type safety without Blazor

The main reason teams pick Blazor — sharing C# types across the wire — is recovered here by generating the TypeScript client + DTO types from the API's OpenAPI document on every build. A backend contract change that breaks the frontend becomes a **compile error**, not a runtime surprise. This is what makes React the stronger pick for this app rather than a tradeoff.

## 8.3 Offline-first frontend specifics

```text
- Reads: render from IndexedDB cache first, sync in background (local-first read).
- Writes (offline-capable forms): optimistic local write + enqueue (see §6.2).
- The SAME Zod schema validates online and offline — single source of truth,
  ideally generated/derived to mirror server FluentValidation rules.
- Clear connectivity + per-record sync status UI (Pending / Synced / Needs review).
- Online-only actions (sales, allocation) are disabled with an explanatory state
  when offline — never silently queued.
```

## 8.4 Mobile

PWA (installable, offline, app-like) covers the field-worker daily-entry flow without an app-store release. A native shell (React Native / Capacitor) is a later option if push notifications or device hardware (barcode scan) become requirements — the React + TS core ports over.

---

# 9. Background Jobs

Functional rules that run on a schedule, not on request:

```text
- Egg-lot withdrawal expiry: clear restricted_until when withdrawal period ends.
- Alert rule evaluation: low stock, production drops, withdrawal nearing, etc.
- Notification dispatch (if/when added).
```

Implementation: a `BackgroundService` driven by a `PeriodicTimer` that sweeps a durable `jobs` table (so work survives restarts and is observable). These are simple periodic sweeps (e.g. hourly/daily), not complex schedules, so **no scheduling library is needed** — plain .NET primitives cover it with zero dependencies. Jobs are tenant-aware (iterate accounts) and idempotent (safe to re-run).

When multiple API instances run (HA or SaaS later), a naive `BackgroundService` fires on every instance, so each sweep would run N times. Handle that with a claim-on-pull pattern — `SELECT … FOR UPDATE SKIP LOCKED` against the `jobs` table so exactly one instance processes each job — rather than reaching for a scheduler.

**Quartz.NET only earns a place** if one of these actually materializes: complex cron expressions, admin-managed/user-editable schedules, or clustered scheduling with misfire recovery. None apply to the Phase 1 workloads above, so it is deliberately *not* a Phase 1 dependency.

---

# 10. Observability

```text
- Logging:   Serilog, structured, with account_id + correlation id on every scope.
- Tracing:   OpenTelemetry (ASP.NET + EF Core + HttpClient instrumentation),
             OTLP export (to a local collector in the homelab).
- Metrics:   request rates/latency, job durations, sync queue depth (if surfaced).
- Health:    /health/live and /health/ready (DB connectivity, migrations applied).
- Audit:     functional audit log is domain data (not telemetry) — written in the
             same transaction as the change it records.
```

---

# 11. Testing Strategy

```text
- Domain unit tests:       pure, fast, no I/O (money math, withdrawal logic, KPIs).
- Application handler tests: in-memory ports, assert behavior + validation.
- Integration tests:       Testcontainers real Postgres, full WebApplicationFactory
                           stack; assert idempotency replay, concurrency 409s,
                           and TENANT ISOLATION (required).
- Contract:                OpenAPI doc is generated and diffed in CI to catch
                           accidental breaking changes; frontend client regenerates
                           from it.
- Frontend:                component tests (Vitest + Testing Library); offline path
                           tests that kill the network mid-flow and assert queue +
                           resync + conflict handling.
```

Critical-path tests that must exist before Phase 1 is "done": idempotent daily-entry resync, egg-lot allocation under concurrent confirmation, withdrawal-restricted lot cannot be sold, cross-tenant isolation.

---

# 12. Security

```text
- Transport: HTTPS everywhere; TLS terminated at Traefik.
- Secrets:   not in source. .env / user-secrets locally; a secrets store
             (e.g. Docker secrets / vault) in deployment.
- AuthN/Z:   per §7; least-privilege roles; policies enforced at the endpoint.
- Tenant:    isolation enforced by query filter + interceptor + tested (§4.2).
- Input:     validated (FluentValidation server-side, Zod client-side).
- Output:    ProblemDetails leak no internals; stack traces never returned.
- Dependencies: scanned (dotnet list package --vulnerable; npm audit) in CI.
- Audit:     who/what/when on all mutations (functional spec).
```

---

# 13. CI/CD & Deployment

## 13.1 Pipeline

```text
- Build + restore (backend: dotnet; frontend: npm/pnpm).
- Test: unit + integration (Testcontainers needs Docker in CI).
- Generate OpenAPI → regenerate + typecheck frontend client (fail on drift).
- Vulnerability scan (NuGet + npm).
- Build container images (API, frontend static assets).
- Apply EF migrations on deploy (gated, not auto on every boot in prod).
```

## 13.2 Reference deployment (homelab)

```text
deploy/docker-compose.yml:
  - traefik         (reverse proxy, TLS, routing)
  - api             (.NET 10 container)
  - frontend        (static PWA assets via nginx/caddy, or served by API)
  - postgres        (volume-backed; backups scheduled)
  - otel-collector  (optional)
Migrations: run as a one-shot init step / job before api starts serving.
```

Cloud portability: nothing here is homelab-specific except the compose file and Traefik labels. The same images run on any container host; Postgres swaps to a managed instance via connection string.

---

# 14. Phase Alignment

Tech capabilities mapped to the functional Unified Phase Plan (§6):

```text
Phase 1 (egg walking skeleton):
  - 4-project solution, EF Core + Postgres, Identity + JWT, tenancy enforcement,
    Minimal API slices for: auth, account/farm/house/flock, daily entry,
    egg production→lots, egg inventory, feed/water usage, mortality,
    basic sales + allocation (ONLINE), basic expenses, dashboard, core reports.
  - Idempotency + concurrency from day one.
  - Offline: thin buffer-and-retry queue (online-first).

Phase 1.5 (operational hardening):
  - Full local-first daily-entry cache + conflict-review UI (§6.4).
  - Inventory reconciliation, audit log surfacing, alert jobs.

Phase 2+ (per functional spec):
  - Pullet, broiler, meat, breeder modules reuse the same slice/tenant/offline
    patterns; product-generic sales model already supports them.

SaaS readiness (when triggered, not Phase 1):
  - Tenant resolution from host/subdomain, account onboarding, billing/quotas.
    No data-layer changes required (§4.2).
```

---

# 15. Open Technical Decisions (revisit, don't block)

```text
- Access-token TTL exact value: tune to observed field-shift length.
- Whether Zod schemas are generated from FluentValidation or hand-mirrored.
- pnpm vs npm; TanStack Router vs React Router (both fine).
- Quartz.NET: NOT needed for Phase 1 (BackgroundService + PeriodicTimer +
  FOR UPDATE SKIP LOCKED covers it). Only revisit if complex cron, user-editable
  schedules, or clustered misfire recovery actually emerge.
- Second DB provider: only wire SqlServer/SQLite when an actual need appears —
  keep the seam (§5) clean meanwhile, don't pre-build the implementation.
- Native mobile shell: only if push/hardware needs materialize.
```

---

## Summary

A .NET 10 vertical-slice API over EF Core (Postgres-primary, provider-portable by discipline), single-tenant today but multi-tenant-ready via mandatory `account_id` query-filter enforcement, with ASP.NET Identity + JWT behind a swappable provider. Offline is local-first **for data capture only**, synced through the existing idempotent API with a client mutation queue and state-machine-based conflict resolution — allocation and money stay online by necessity. Frontend is a React + TypeScript PWA with a typed client generated from OpenAPI for end-to-end type safety. Everything ships on Docker Compose behind Traefik today and lifts to the cloud or SaaS without an architectural rewrite.
