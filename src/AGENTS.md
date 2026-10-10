# AGENTS.md: `src/`

Rules for the backend, its tests, the container image and the compose stack. The root [`AGENTS.md`](../AGENTS.md) applies too. Persistence edits also read [`Cluckwork.Infrastructure/Persistence/AGENTS.md`](Cluckwork.Infrastructure/Persistence/AGENTS.md).

Many rules here are enforced by architecture tests in `tests/Cluckwork.Application.Tests/Architecture/` and by the `Cluckwork.Analyzers` build errors (CW1000-CW1004). When one fails, its message names the violation and usually prints the row to add. Read the message and the decision record it cites before you change anything.

## Conventions (follow these)

### Application shape

- Handlers and domain methods return `Result` / `Result<T>`. Throw only for invariant violations.
- One handler per feature, called directly from endpoints. No MediatR. Register services in the owner's file under `Cluckwork.Api/Modules/<Owner>/` (Access: `AddCluckworkIdentity`).
- One FluentValidation validator per command. Endpoints call `ValidateAsync` and return `ValidationProblem`.
- Endpoints are minimal APIs under `/api/v1/...`. Writes require auth and an `Idempotency-Key`.
- `.editorconfig` rules at `:warning` fail the build. Adding one is a decision, not a preference. Fix IDE0065/IDE0161 with `dotnet format style`. Never name a namespace segment the same as a type; it shadows the type. → [`985-csharp-style-gate.md`](../docs/decisions/985-csharp-style-gate.md)

Module boundaries: the ledger lives in `src/Cluckwork.Domain/Common/Architecture/Modules/<Owner>.cs`. The ledger and the table-owner walk authorise no module move and no write into another owner's data; the approved design governs. A clean move between modules can pass every walk. → [`514-module-ledger.md`](../docs/decisions/514-module-ledger.md), [`845-table-owners.md`](../docs/decisions/845-table-owners.md)

- When `ModuleLedgerRealTreeTests` or CW1001 prints an undeclared `[ModuleEdge]` row, read the code before you paste it. Set `Kind` yourself: `W` if it writes through the other owner in one transaction, `R` if it reads or validates. The printed row defaults to `R`, and only review checks `Kind`. Write a `Reason` that names the port or type; never widen an existing reason to cover new code.
- Never silence a CW diagnostic with `#pragma`, `NoWarn` or `.editorconfig`, and never add an exemption. Fix the reach. For CW1004, call the owner's contract. Moving a type into a module's `Contracts/` folder is a reviewed policy change, not a fix. A new module project adds the same analyzer-only `ProjectReference` to `Cluckwork.Analyzers` as Domain, Application, Infrastructure and Api; without it, CW1004 never runs there. → [`859-typed-rule-registries.md`](../docs/decisions/859-typed-rule-registries.md#the-module-edge-analyzer)
- Application interfaces never expose persistence types (`DbContext`, `DbSet<>`, `IQueryable`, EF types, Infrastructure types). Return a DTO, value object, concrete aggregate or `PagedResult`. → [`847-seam-surface-guard.md`](../docs/decisions/847-seam-surface-guard.md)
- Endpoints never take `AppDbContext`, `DbContext`, `DbSet` or `IQueryable`. → [`846-adapter-reach-ratchet.md`](../docs/decisions/846-adapter-reach-ratchet.md)
- Declare an adapter tier's privilege (an `[AdapterTier]` row on `PlatformModuleRules`) before its surface exists. Drop `AdapterTierRealTreeTests.McpTierRow_IsDormantToday` in the same commit that activates the MCP tier. → [`843-adapter-tiers.md`](../docs/decisions/843-adapter-tiers.md)
- A read of a contracted module's tables from outside it goes through the module's contract or a reviewed compatibility-exception row. `CompatibilityExceptionRealTreeTests` sees only `DbSet<T>` reads; raw SQL is outside it and needs an explicit module-reach review. → [`850-compatibility-exceptions.md`](../docs/decisions/850-compatibility-exceptions.md)
- Adapters reach a module through its `I<Module>Module` contract; peer modules through its contract or seam. Contract types never carry entities, aggregates or persistence types. → [`849-module-contract.md`](../docs/decisions/849-module-contract.md), [`1023-peer-contract-guard.md`](../docs/decisions/1023-peer-contract-guard.md)
- Never move `IReportQueries` or `IExportQueries` into `Contracts/`; that lets adapters bypass the Insights facade.
- Regenerate the coupling matrix; never hand-edit it: `CLUCKWORK_REGENERATE_MATRIX=1 dotnet test tests/Cluckwork.Application.Tests --filter FullyQualifiedName~CouplingMatrixRealTreeTests`. → [`848-generated-coupling-matrix.md`](../docs/decisions/848-generated-coupling-matrix.md)
- Ports that join a caller's transaction never save: `IMortalityLedger.AppendAsync`, `IEggStock` draws and restores, fixture purges, the provisioning ports. Keep it that way. → [`852-flock-contract.md`](../docs/decisions/852-flock-contract.md), [`854-commerce-contract.md`](../docs/decisions/854-commerce-contract.md)
- Lock order is part of the contract. Egg lots lock in `(ProductionDate, Id)` order on every path; `RecordFeedUsage` locks the item, reads flock eligibility, then locks FIFO lots. Keep `IFlockLookup` reads untracked. → [`853-egg-operations-contract.md`](../docs/decisions/853-egg-operations-contract.md), [`855-inventory-contract.md`](../docs/decisions/855-inventory-contract.md)
- Sign-in resolves a farm code through `IIdentityProvider.ResolveFarmCodeAsync`, never `IFarmModule`. Cross-farm reads go only through `IFarmDirectory`, named only in `Api/Cli` and `Infrastructure/Jobs`. Never put it behind a forwarding helper, and never let a request path reach it: `FarmDirectoryCallerTests` checks names, not calls. → [`851-farm-contract.md`](../docs/decisions/851-farm-contract.md)
- A new table that stores a farm-currency amount registers an `ICurrencyBoundRowSource`. Nothing fails if you forget. Keep the order of the first two `ICurrencyBoundRowSource` registrations and demo cleanup's flocks-last delete; no guard sees either. → [`854-commerce-contract.md`](../docs/decisions/854-commerce-contract.md)
- Seeder-only ports are named `I<Module>Fixture` (Access: `IAccessSeedLookup`) and registered only outside Production. → [`858-platform-composition.md`](../docs/decisions/858-platform-composition.md)

### Data and correctness

- Every aggregate mutation bumps `Version`. EF checks the original value but never increments it, so a missing `Version++` lets concurrent writers overwrite each other silently. Every new mutation, and every fix to one, gets a parallel-race integration test.
- Every tenant-owned entity has a non-nullable `Guid AccountId` and a tenant `HasQueryFilter`. The interceptor and the concurrency token come from model walks; the query filter on an entity without `FlockId` does not, and no test checks it. Seeders run before the tenant resolves and use `IgnoreQueryFilters()`. → [`530-multi-farm-tenancy.md`](../docs/decisions/530-multi-farm-tenancy.md)
- A farm code changes only through `Account.Rename` via the `rename-account` verb. Never a raw `UPDATE`, endpoint or settings field. Retired codes are reusable at once, so run `list-accounts` immediately before every rename. → [`732-farm-code-rename.md`](../docs/decisions/732-farm-code-rename.md)
- Flock-scoped reads come from a model walk over `Flock` and every entity with a scalar `FlockId`. A child without `FlockId` gates through a filtered parent or an explicit policy: `DailyEntryGrade` loads only through filtered `DailyEntry`, `EggInventoryMovement` only through filtered `EggLot`, and both children's direct exports stay `AdminOnly`. The walk cannot see such a child, so every new parent-derived child or exclusion needs a stated rationale and a causal mutation test.
- Flock-scoped writes need a resolved actor. A new system-actor caller of a flock-scoped handler is account-wide and needs an access review.
- Business dates are `DateOnly`. `CreatedAtUtc` and `UpdatedAtUtc` are stamped by Postgres; factories and handlers never set them. Chronological lists order by business date, `CreatedAtUtc`, then the shadow `Sequence`, all in the list's direction; omit the business-date key when there is none. `Sequence` never leaves the database. Name-ordered lookups and canonical FIFO or `FOR UPDATE` ordering do not gain a sequence. Preserve action-specific timestamps and `Version`. Legacy audit backfills use only exact whitelisted row events and fall back to the documented unknown sentinel. When you declare a type not-read-by-time, the reason must be true; only review checks it. → [`819-business-record-chronology.md`](../docs/decisions/819-business-record-chronology.md)
- Use `EnableRetryOnFailure` only for self-contained EF units. Above a stateful detector (a counter, CAS stamp or single-use claim), an automatic replay cannot tell the request racing itself from the signal the detector should catch. Use `SingleAttemptExecution` when replay is observable. When replay writes nothing, probe durability with a self-minted token. → [`269-transient-db-retry-boundary.md`](../docs/decisions/269-transient-db-retry-boundary.md)
- `AuditEvents.Sequence` is global, not per farm. Never expose it or treat it as a per-farm revision.

### Auth and credentials

- `GET` and `PUT /account/settings` and the logo and banner writes are `OwnerOnly`. `GET /account`, `/account/logo` and `/account/banner` stay open to every role. The Farm settings navigation entry follows the exact `Admin` role; the rest of Setup keeps its `isAdmin` gate. → [`729-owner-only-farm-configuration.md`](../docs/decisions/729-owner-only-farm-configuration.md)
- Never commit key material. Call `PemKey.Normalize` before `ImportFromPem`. Check the JWT key settings with `IsNullOrWhiteSpace`, never `??`: the shipped `appsettings.json` carries `""`.
- `CredentialEpochMiddleware` reads the epoch from the database on every request. Never cache it. Never repair a bad epoch by setting it to 1; that revives old credentials. → [`364-credential-epoch-revocation.md`](../docs/decisions/364-credential-epoch-revocation.md)
- OAuth: the issuer comes from configuration, never the request Host. Never authenticate OAuth tokens at authorization time. Endpoints accept OAuth tokens only through `AcceptOAuthTokens(scopes)`. Never grant `openid` without a persistent signing key. No path issues a code without spending a step-up grant. Rate limits use the shared `IFixedWindowCounter`, never a process-local limiter. → [`795`](../docs/decisions/795-openiddict-server.md), [`796`](../docs/decisions/796-oauth-fail-closed.md), [`797`](../docs/decisions/797-oauth-client-registration.md), [`798`](../docs/decisions/798-oauth-consent.md), [`799`](../docs/decisions/799-connected-apps.md), [`1146`](../docs/decisions/1146-connected-apps-switch.md)
- Client metadata document fetches go only through `ClientMetadataFetcher`. Never add config that loosens its address check beyond `OAuth:ClientMetadata:PrivateHosts`. → [`1148-client-id-metadata-documents.md`](../docs/decisions/1148-client-id-metadata-documents.md)
- `ICurrentUser` is an authorization input. System callers declare a `SystemActors` identity. Seeders resolve an Owner and load its real roles with `UserManager.GetRolesAsync`, never literal roles. → [`500-audit-actor.md`](../docs/decisions/500-audit-actor.md)
- Write generated passwords to stdout only, never to the logger. Never accept a password on the command line.

- An MCP tool learns its caller only from `McpCallContext`, and never injects `TenantContext`, `CurrentUserContext`, `FlockScope`, `AppDbContext`, `IServiceProvider`, `IServiceScopeFactory`, `HttpContext` or `IHttpContextAccessor`. The tests in `tests/Cluckwork.Api.IntegrationTests/Mcp/` enforce it; [`02-guards.md`](../docs/plans/770-mcp-server/02-guards.md) lists what they cannot see.

### Boot guards and process roles

- Scope every boot guard by `ProcessRoles`, not by statement order, and scope the whole subsystem. Each serving-only guard gets rows in `ProcessRoleGuardTests.ServingOnlyGuards`, one per violation, not one per subsystem. → [`347-process-role.md`](../docs/decisions/347-process-role.md)
- The runtime image needs tzdata and ICU: never an Alpine or chiseled base, never `InvariantGlobalization=true`. → [`264-farm-timezone.md`](../docs/decisions/264-farm-timezone.md)
- A new Production boot guard that fails on missing config, and every config key added, renamed or retired, updates the sim harness in the same PR: `tools/simulation/bootstrap.sh`, `docker-compose.sim.yml`, `verify-harness.sh`. Only `e2e-smoke.yml` boots it, and only for some paths. Satisfy the guard there; never disable one to make the harness pass. → [`370-sim-harness-boot-guards.md`](../docs/decisions/370-sim-harness-boot-guards.md)
- A new required config key also updates the AppHost (`src/Cluckwork.AppHost/Program.cs`) in the same PR. No CI job derives the API's required keys, so a miss breaks only `aspire run`. → [`565-aspire-local-orchestration.md`](../docs/decisions/565-aspire-local-orchestration.md)

### Operations and callers

- A write-contract change (request shape, validation, status codes) updates its callers outside CI by reading them: seeders and `tools/simulation/k6/`, which runs only on dispatch. → [`394-write-contract-callers.md`](../docs/decisions/394-write-contract-callers.md)
- Seed and simulation data are never boot-seeded. Use the `seed` verb. → [`280-seed-and-simulation.md`](../docs/decisions/280-seed-and-simulation.md)
- The base `appsettings.json` Console sink carries only `Name`. Anything pushed through `BeginScope` or `LogContext` reaches the log collector in Production. → [`404-production-logs.md`](../docs/decisions/404-production-logs.md)

### Accepted risks (declined on purpose)

These were declined deliberately. Do not "fix" one without reopening its decision.

- Login does not lock the account row. A suspension that commits between the active check and the mint returns an inert token. A `FOR SHARE` lock was declined for hot-path cost. The token stays inert only while four premises hold: the middleware's live read, `RefreshAsync`'s suspended check, suspension revoking existing credentials in the same transaction, and reactivation's revoke. The revocation must execute inside that transaction, not merely be defined there by a deferred helper. Breaking any one premise reopens #579. → [`579-suspension-issuance-window.md`](../docs/decisions/579-suspension-issuance-window.md)
- Only `AspNetUserRoles` carries a shadow `AccountId` and a composite FK to `AspNetUsers(Id, AccountId)`. The other four user-keyed Identity tables have no writer in `src/`; adding one needs the same treatment. → [`530-multi-farm-tenancy.md`](../docs/decisions/530-multi-farm-tenancy.md)
- `AuditEvents.Sequence` is one global counter, not per farm, and ordering leads with `OccurredAtUtc`, so clock rollback is unhandled. → [`508 diagnosis`](../docs/plans/508-audit-monotonic-order/01-diagnosis.md)
- Job handlers are always at-least-once and idempotent; nothing is exactly once. Behind a transaction-pooling proxy the leader lock can move between backends, so single leader is not guaranteed. Point `ConnectionStrings:LeaderLease` at a session-pinned endpoint to keep at most one leader. → [`271-single-serving-instance.md`](../docs/decisions/271-single-serving-instance.md)
- Backend coverage is measured, never gated. Adding a threshold is a separate decision. → [`776-backend-coverage.md`](../docs/decisions/776-backend-coverage.md)
- `Account.AllowConnectedApps` defaults on through the migration default and the aggregate initialiser, never an EF model default, which would drop an inserted `false`. → [`1146-connected-apps-switch.md`](../docs/decisions/1146-connected-apps-switch.md)
- Replacing the Data Protection certificate makes every stored key unreadable and invalidates outstanding tokens. Rotate only by the runbook. → [`794-data-protection-key-ring.md`](../docs/decisions/794-data-protection-key-ring.md)

### Enforced rules and their records

A guard, a verb or the Dockerfile already carries each rule below, so this file does not restate it. Read the record before you change that mechanism.

- Production boot fails with an empty `RateLimiting:TrustedProxies` unless `RateLimiting:AllowNoTrustedProxies=true` (`TrustedProxyGuardTests`). → [`260-proxy-trust.md`](../docs/decisions/260-proxy-trust.md)
- Production boot fails on a Postgres `sslmode` outside the allow-list unless `Database:AllowInsecureConnection=true` (`PostgresConnectionStringTests`, `ConnectionTlsFloorWiringTests`). → [`261-postgres-tls-floor.md`](../docs/decisions/261-postgres-tls-floor.md)
- `PostgresConnectionString` turns GSS encryption off unless the operator sets it (`PostgresConnectionStringTests`). → [`332-gss-kerberos.md`](../docs/decisions/332-gss-kerberos.md)
- A serving boot fails on a blank or unimportable JWT key (`ProcessRoleGuardTests`). → [`510-jwt-key-boot-check.md`](../docs/decisions/510-jwt-key-boot-check.md)
- The `migrate` verb applies migrations and exits; `/health/ready` returns 503 while one is pending (`MigrateCommandTests`). → [`263-migrate-command.md`](../docs/decisions/263-migrate-command.md)
- The `healthcheck` verb probes `/health/ready` without DI or config (`HealthCheckCliCommandTests`). → [`266-container-health-probe.md`](../docs/decisions/266-container-health-probe.md)
- `bootstrap-admin` creates an Owner only when the default farm has none (`BootstrapAdminCommandTests`). → [`283-first-run-admin-provisioning.md`](../docs/decisions/283-first-run-admin-provisioning.md)
- `recover-admin` resets an admin in one audited transaction (`RecoverAdminCommandTests`, the [runbook](../docs/runbooks/break-glass-account-recovery.md)). → [`265-break-glass-recovery.md`](../docs/decisions/265-break-glass-recovery.md)
- The Dockerfile runs the runtime stage non-root on digest-pinned bases. `image-scan.yml` files an issue for each fixable HIGH or CRITICAL finding but gates nothing. → [`267-container-hardening.md`](../docs/decisions/267-container-hardening.md)
- `AccessOwnershipClaimTests` keeps `IIdentityProvider` and `IStepUpGrantService` Access-owned, so the ownership walk fails a peer or adapter that takes either. → [`857-access-contract.md`](../docs/decisions/857-access-contract.md)

### Deploy invariant: exactly ONE serving API instance (#271)

The app supports one serving instance. Background jobs run under a single-leader advisory lock. Any new in-process state (a singleton cache, limiter, counter or hosted service) is a scaling blocker unless it uses the shared stores (`IClaimOnceStore`, `IFixedWindowCounter`, the lease). Find these by walking every `AddSingleton` and `AddHostedService` under `src/`, every in-memory state primitive, and the registrations inside every package `Add*`/`With*` extension that `Program.cs` calls, at the version `packages.lock.json` resolves. Never from memory. → [`271-single-serving-instance.md`](../docs/decisions/271-single-serving-instance.md)
