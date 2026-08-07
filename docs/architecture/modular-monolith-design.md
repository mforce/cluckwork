# Modular-monolith design

**Status:** proposed architecture; no production refactor is authorized by this document.  
**Evidence date:** 2026-08-04, branch `work`. `graphify-out/graph.json` exists, but the
`graphify` executable was unavailable in the analysis environment. The inventory was
therefore produced from project references, namespace references, constructors, EF
configuration, endpoints, tests, seeders and SPA calls. Folder names were not treated as
boundary evidence.

## 1. Decision and vocabulary

Adopt a **hybrid modular monolith**: business-module interface and implementation
assemblies, a small shared kernel, a platform/persistence assembly, and the existing API
as composition root. It remains one process, one deployment and one PostgreSQL database.

* A **module** owns a cohesive business responsibility, its rules and its data.
* A module's public **interface** is the narrow contract other modules and host adapters
  compile against; its **implementation** is internal and cannot be referenced by peers.
* A **seam** is a deliberate call/event boundary. An HTTP endpoint, CLI command or job is
  an **adapter** at a seam, not a place for business orchestration.
* **Depth** means substantial policy behind a small interface. **Leverage** is how many
  callers gain that policy without learning internals. **Locality** means a change to a
  business rule normally stays inside its owning module.

The recommended business modules are **Access**, **Farm**, **Flock Management**, **Egg
Operations**, **Commerce**, **General Inventory**, **Finance**, and **Insights**. **Platform**
is a host capability. Egg Operations deliberately merges daily capture, egg grades, egg lots
and the egg ledger because submission/correction prove that they share invariants. Commerce
owns the customer-to-payment lifecycle and calls one deep stock seam.

## 2. At-a-glance diagrams

These diagrams are intentionally higher-level than the inventory tables below. They show
what changes for a reader who wants the shape of the architecture before the details.

### 2.1 Current state — layered monolith with feature folders but shared internals

```text
Browser SPA
    |
    v
Cluckwork.Api
  minimal endpoints, middleware, jobs, CLI verbs, DI registration
    |
    | direct handler calls
    v
Cluckwork.Application
  feature folders: Accounts, DailyEntries, Flocks, Inventory, Sales, Users, ...
  handlers can reference common ports and feature repositories across folders
    |
    | repository interfaces / unit of work / identity ports
    v
Cluckwork.Infrastructure
  EF repositories, Identity provider, seeders, read queries, background jobs
    |
    | one AppDbContext exposes all sets
    v
PostgreSQL database
  Accounts, Flocks, DailyEntries, EggLots, EggInventoryMovements,
  SalesOrders, SalesOrderAllocations, Payments, InventoryLots, Expenses, ...

Cluckwork.Domain sits underneath the application layer and contains aggregates/value
objects for all capabilities. Because the monolith is layered by technical concern,
feature folders improve navigation but do not enforce module ownership.
```

Current-state coupling hot spots:

```text
DailyEntries  --->  EggGrades / EggLots / EggInventoryMovements / Flocks
Sales         --->  Catalog / Customers / EggGrades / EggLots / EggInventoryMovements
Inventory     --->  Accounts / Flocks
Expenses      --->  Accounts / Flocks
Users         --->  Flocks

Meaning: these are not necessarily wrong business dependencies, but today they are
implemented through broad project/folder visibility rather than deliberate module seams.
```

### 2.2 Proposed state — modular monolith with explicit seams

```text
Browser SPA
    |
    v
Cluckwork.Api
  HTTP / CLI / job adapters only
    |
    | calls contracts, not repositories or implementations
    v
Module contracts
  Access.Contracts
  Farm.Contracts
  FlockManagement.Contracts
  EggOperations.Contracts
  Commerce.Contracts
  Inventory.Contracts
  Finance.Contracts
  Insights.Contracts
    |
    | DI wires each contract to one hidden implementation
    v
Module implementations
  Access.Implementation          Farm.Implementation
  FlockManagement.Implementation EggOperations.Implementation
  Commerce.Implementation        Inventory.Implementation
  Finance.Implementation         Insights.Implementation
    |
    | approved persistence/session abstractions only
    v
Platform.Persistence
  single AppDbContext, migrations, tenant filter/stamping, transactions,
  idempotency, audit append, execution strategy
    |
    v
Same PostgreSQL database

SharedKernel
  tiny primitives only: Result/Error, Money, clocks/paging DTOs as needed
```

The important change is not process count. The important change is that module
implementations become private and callers cross a deliberate interface seam.

### 2.3 Cross-module write seams that stay synchronous

```text
Egg Operations.SubmitDailyEntry
    | same ambient transaction
    +--> Flock Management.AppendMortalityMovement
    +--> Egg Operations creates lots + egg-ledger opening movements
    +--> Platform appends the audit row in the SAME transaction, commits once, then publishes read events

Commerce.ConfirmSale / VoidSale
    | same ambient transaction
    +--> Egg Operations.ReserveFifo / RestoreReservation
    +--> Commerce updates order, allocation and payment state
    +--> Platform appends the audit row in the SAME transaction, commits once, then publishes read events

Inventory.RecordFeedUsage
    | inventory transaction, item lock held first
    +--> Flock Management.GetFlockProductionEligibility (enlisted, after the item lock)
    +--> Inventory consumes FIFO lots and writes movement/usage rows
```

These seams stay in-process because Cluckwork must preserve atomic ledger updates,
FIFO lock ordering, optimistic concurrency and existing HTTP/idempotency contracts.
The audit row is one of those atomic writes, not a decoupled event: it's appended
in the SAME transaction as the mutation it records (§3.5 item 4, §6's Platform row)
and would be rolled back with it on failure. Only the READ-model publish happens
after commit — a crash between commit and that publish loses a cache refresh, never
an audit trail entry.

## 3. Current-state evidence

### 3.1 Assembly and composition shape

`Application` references `Domain`; `Infrastructure` references both; `Api` references all
three. The API composition root registers feature repositories and handlers one by one.
`AppDbContext` inherits the Identity context and exposes every business and platform set.
This is useful runtime unity but accidental compile-time coupling: any infrastructure
class can access any table and any application handler can inject any feature repository.

Shared abstractions are:

* `Domain.Common`: `Entity`, `AggregateRoot`, `ValueObject`, `Money`, `Result`, `Error`,
  and domain-event marker. `Result`/`Error` and the smallest identity-free primitives are
  legitimate shared-kernel candidates; aggregate bases remain implementation details.
* `Application.Common`: clock/farm clock, current user, audit writer, identity provider,
  flock-scope guard, generic repository, unit of work, paging and result logging. The
  clocks/paging/result DTOs are shared contracts. Repositories, `IUnitOfWork`, audit,
  identity, and flock scope are currently high-leverage but boundary-erasing ports and
  must move behind owners or Platform contracts.
* Infrastructure: one context, tenant context/stamping, transaction/execution strategy,
  repositories, Identity, jobs, seeders and cross-module read queries.

### 3.2 Capability inventory

| Capability | Domain model | Application/infra/API | SPA alignment |
|---|---|---|---|
| Access | `UserRoleAssignment`; account roles | user handlers, `IIdentityProvider`, Identity users/roles/refresh tokens, credential middleware, auth/users/me endpoints, bootstrap/recovery/purge | login, set-password, users; auth/session contexts |
| Farm | `Account`, `FarmLogo`, currency/unit/brand settings | account/logo handlers and repos, tenant/farm clocks, account endpoints | settings/account, farm context |
| Flock Management | `Flock`, `BirdMovement` | flock handlers/repos and lifecycle/scope checks | flocks |
| Egg Operations | `DailyEntry`/lines, `EggGrade`, `EggLot`, egg movements | daily-entry, grade and stock handlers/repos; lock-sweep | daily entry, history, grades, stock |
| Commerce | `Product`, conversions/mappings, `Customer`, `SalesOrder`, items, allocations, `Payment` | catalog/customer/sales handlers/repos | products, customers, sales |
| General inventory | item, lot, movement, feed usage, water usage | inventory/water handlers and repos | inventory, water |
| Finance | expense/category | expense handlers/repos | expenses |
| Insights | no write aggregates; report/export DTOs | `ReportQueries`, `ExportQueries`, audit query | dashboard, reports, export, audit |
| Platform | audit event plus operational records | `AppDbContext`, tenant interceptor, unit of work, idempotency, durable jobs, health, migrations, seeders, CLI | client error/idempotency transport only |

There are 49 application handlers. A source-namespace scan found these direct feature
reference counts: Production-shaped `DailyEntries` points to EggGrades 5, EggLots 3,
Eggs 3 and Flocks 4; Sales points to Accounts 1, Catalog 1, Customers 1, EggGrades 2,
EggLots 2 and Eggs 2; Inventory points to Accounts 3 and Flocks 3; Catalog points to
Accounts 2 and EggGrades 2; Expenses points to Accounts 1 and Flocks 2; Users points to
Flocks 1. These counts measure source references, not runtime weight.

### 3.3 Tables, owners and cross-owner foreign keys

| Owner | Tables |
|---|---|
| Access | `AspNetUsers`, `AspNetRoles`, Identity join/claim/token tables, `refresh_tokens`, `UserRoleAssignments` |
| Farm | `Accounts`, `FarmLogos` |
| Flock Management | `Flocks`, `BirdMovements` |
| Egg Operations | `DailyEntries`, `DailyEntryGrades`, `EggGrades`, `EggLots`, `EggInventoryMovements` |
| Commerce | `Products`, `ProductEggGradeMappings`, `EggUnitConversions`, `Customers`, `SalesOrders`, `SalesOrderItems`, `SalesOrderAllocations`, `Payments` |
| General Inventory | `InventoryItems`, `InventoryLots`, `InventoryMovements`, `FeedUsages`, `WaterUsages` |
| Finance | `ExpenseCategories`, `Expenses` |
| Insights | no source-of-truth tables initially; reads `AuditEvents` and owner tables |
| Platform | `AuditEvents`, `idempotency_records`, `durable_jobs`, `simulation_seed_state`, EF history |

Important cross-owner FKs are retained as database integrity constraints: Egg Operations
daily-entry grades to Egg Operations grades; Egg Operations lots to Flock Management flocks;
General Inventory usages/movements to Flock Management flocks and daily-entry provenance;
Finance expenses to Flock Management flocks; Access flock assignments to Flock Management
flocks. Their existence
does not grant an owner permission to mutate the referenced table. References in module
models are IDs plus contract snapshots, not navigation entities.

### 3.4 Coupling matrix

`W` = necessary synchronous write coupling, `R` = read/validation coupling, `E` =
after-commit event, `Q` = read-model query, `P` = platform service, `—` = none. Rows call
columns in the target design.

| from / to | Access | Farm | Flock | Egg Ops | Commerce | Inventory | Finance | Insights | Platform |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Access | — | R | R | — | — | — | — | E | P |
| Farm | — | — | — | — | — | — | — | E | P |
| Flock | — | R | — | — | — | — | — | E | P |
| Egg Ops | — | R | W | — | — | — | — | E | P |
| Commerce | — | R | — | W | — | — | — | E | P |
| Inventory | — | R | R | — | — | — | — | E | P |
| Finance | — | W | R | — | — | — | — | E | P |
| Insights | — | — | — | — | — | — | — | — | Q/P |
| Platform/adapters | W | W | W | W | W | W | W | R | — |

The `W` seams are synchronous and transaction-aware where invariants require it: Egg Operations appends mortality through Flock Management, Commerce reserves/restores stock through Egg Operations, and Finance obtains a lock-aware currency snapshot from Farm. The current namespace/project coupling, universal repository visibility and
central DI list are accidental. Lifecycle validation, FIFO stock allocation, currency
settings and cross-domain atomicity are necessary business coupling.

### 3.5 Critical workflow traces and decisions

1. **Submit daily entry.** The handler loads entry/flock/scope/active grades, calls
   `DailyEntry.Submit`, creates one lot and opening egg movement per quantity, optionally
   creates mortality movement, then saves once. Keep entry, grades, lots and egg ledger in
   Egg Operations. Its use case calls Flock Management synchronously to validate and append
   mortality on the same ambient context/transaction. Any failure rolls everything back;
   concurrency remains 409 and
   a retry sees the existing 422 state contract. Publish `DailyEntrySubmitted` only after
   commit for Insights; an event must never create authoritative stock asynchronously.
2. **Confirm/void sale.** Commerce orchestrates order/allocation/payment mutation and a deep
   synchronous Egg Operations stock command (`ReserveFifo`/`RestoreReservation`) in one
   transaction. Stock owns lot mutation, ledger rows and canonical `(ProductionDate, Id)`
   locks; Commerce owns order locks and provenance. The seam returns opaque DTOs, not lots.
3. **Record feed usage.** General Inventory owns FIFO lot consumption and usage/movement
   creation. It locks the item first, then — still enlisted inside that same inventory
   transaction — calls a narrow Flock Management query `GetFlockProductionEligibility(flockId,date)`
   before reading lots. The check must stay inside the transaction, not run as a precondition
   before it opens: today's handler reads the flock and evaluates `CanRecordProductionOn` after
   the item lock is already held, which shrinks a concurrent archive/deplete race to the commit
   itself rather than closing it — the flock row is deliberately left unlocked (a same-day
   deplete racing a same-day feed is business-valid either way; the birds ate before they left).
   Moving the check outside the transaction would widen that window instead and change today's
   behavior. No event is suitable: rejection must be immediate. All inventory writes are atomic.
4. **Manager adjust/void.** Keep entry/lot/egg-ledger correction in Egg Operations and call
   Flock Management synchronously for the compensating mortality row in the same ambient
   transaction. Acquire lots in canonical order and preserve
   the client aggregate version and append compensating rows; never replace this with an
   eventually consistent event. Audit participates in the ambient transaction.
5. **Reports/exports.** Insights owns query DTOs and read-only adapters. It may execute
   documented, `AsNoTracking` cross-owner SQL through a restricted internal read session,
   including the current repeatable-read export snapshot. It cannot expose `IQueryable`,
   entities or the context, and cannot call `SaveChanges`. Events may later populate read
   models, but are not required to manufacture module purity.
6. **Identity/platform paths.** Access owns token/security state and credential epoch.
   Tenant identity resolution stays claim-only in Platform: `TenantResolutionMiddleware`
   keeps populating `TenantContext` directly from the JWT `account_id` claim, before any
   module contract runs. It must not route through Access/Farm contracts to establish tenant
   identity — those calls would either execute tenant-filtered reads before `TenantContext`
   exists or require bypassing filters mid-request, weakening the fail-closed tenant boundary.
   Farm contracts are called only after `TenantContext` is already resolved (e.g. to read farm
   settings), never to resolve it. Middleware order and fresh credential-epoch read stay
   unchanged. Idempotency wraps HTTP writes outside
   module calls. Jobs call module interfaces (`LockDueDailyEntries`, token purge), not EF.
   Seeders and simulation become explicit orchestration adapters calling module bootstrap
   interfaces; migration/health remain Platform. Bootstrap and break-glass remain Access
   commands with their current transaction, stdout-secret and fail-closed semantics.

## 4. Three credible structures

| Criterion | Capability folders in four assemblies | Assembly per module, full slices | **Hybrid module interfaces/implementations + host/platform** |
|---|---|---|---|
| Compile-time enforcement | weak; namespaces are convention | strongest but many projects | strong at public seams; practical exceptions isolated |
| Interface depth/callers | easy to keep shallow repositories | can become excessive module RPC | deep use-case interfaces; host orchestration only where atomic evidence demands |
| EF/migrations | simplest | multiple contexts/migration streams are risky | one context/migration stream, configurations assigned to owners |
| Transactions | current UoW easy | cross-context enlistment complexity | one scoped session/ambient transaction |
| Tests/build | least disruption | most projects and test reshaping | moderate, module contract tests plus existing integration suite |
| Incremental cost | low moves, low protection | high/flag-day pressure | adapters allow one seam at a time |
| Circular risk | hidden runtime circles | compile errors but pressure for shared dumping ground | DAG tests plus explicit workflow assembly |
| Extractability | low | high but over-optimizes for services | adequate; extraction is not the goal |

Folders-only loses because it cannot stop the exact direct repository/context access this
design is meant to prevent. Full vertical assemblies lose because one context, one frozen
migration history and demonstrated cross-module transactions would force infrastructure
leakage or elaborate cross-context coordination. The hybrid supplies enforcement where it
has leverage while preserving EF and transaction locality.

## 5. Target structure and dependency diagram

```text
Cluckwork.Api (HTTP/CLI/job adapters and composition root)
        |             Cluckwork.Workflows (only evidenced cross-module commands)
        +----------------------+----------------------+
                               v                      v
 Modules.{Access,Farm,FlockManagement,EggOperations,Commerce,Inventory,Finance}.Contracts
                               ^
                               | (DI only; peer implementations are invisible)
 Modules.*.Implementation -----+
             \                 /
              Cluckwork.Platform.Persistence (single AppDbContext/migrations)
                         |
                  Cluckwork.SharedKernel

Modules.Insights.Contracts <- Modules.Insights.Implementation -> read-only reporting session
```

Contracts may depend only on `SharedKernel`. Implementations depend on their own contract,
SharedKernel and narrowly approved platform contracts. `Workflows` depends only on module
contracts and transaction coordinator. The API must not reference implementation types in
endpoint source; registration extension methods are the single composition exception.

## 6. Module contracts

| Module | Responsibility / owned model | External interface and dependencies | Events / transaction | Forbidden references and fitness tests |
|---|---|---|---|
| Access | authentication, users, roles, assignments and refresh credentials; Access tables above | `IAccessModule.Login/Refresh/Logout/ChangePassword/ManageUser`, `ICredentialEpochVerifier`, assignment commands. Incoming API/CLI/jobs; outgoing Farm account existence and Flock lookup only | user/role/security events after commit; each issuance/reset/recovery is one existing transaction | no peer implementation/EF entity; security characterization for epoch 0/missing claim, reset, bootstrap, recovery; fresh-read and middleware-order guards |
| Farm | tenant account, settings, logo, currency/unit/timezone | `IFarmModule.Get/UpdateSettings/Logo`; small `IFarmContextReader` returning immutable `FarmContextSnapshot` | `FarmSettingsChanged` after commit; one aggregate transaction | no Identity or feature repo; tenant filter/stamp/timezone fail-closed tests |
| Flock Management | flock lifecycle and bird ledger | `IFlockModule` plus eligibility/name and mortality-append port | events after commit; mortality may enlist in Egg Operations transaction | no Egg Ops entities/repos; lifecycle/scope/Version/race tests |
| Egg Operations | daily capture/locking, grades, egg lots and ledger | `IEggOperationsModule`, deep `ReserveFifo`/`RestoreReservation`; calls Flock port | entry/grade/stock events after commit; submit/adjust/void include enlisted mortality | no Commerce entities/repos; Version/FIFO/balance/concurrency tests |
| Commerce | products, customers, orders/allocations/payments | `ICommerceModule`; calls Farm currency and opaque Egg Operations stock seam | sale/payment events after commit; confirm/void use one ambient transaction | no Egg Ops entities/repos; provenance/payment/parallel tests |
| General Inventory | items/lots/movements, feed and water usage | `IInventoryModule`; calls Flock eligibility and Farm currency snapshot | inventory events after commit; purchase/consume/adjust each one transaction | no Flock/Account entities or repos; FIFO, rollback and lifecycle-version race tests |
| Finance | expense categories/expenses | `IFinanceModule`; calls Farm currency and Flock reference validation | expense events after commit; one expense transaction | no Farm/Flock entities/repos; money/version/tenant tests |
| Insights | reporting, export and audit views | `IInsightsModule` returns materialized/streamed DTOs; incoming API only; read adapter depends on restricted reporting session | consumes events only for optional projections; repeatable-read export, otherwise read-only | no command/repository interfaces, `SaveChanges`, entities or `IQueryable`; SQL tenant and snapshot-consistency tests |
| Platform | persistence transaction, tenant stamping, idempotency, audit sink, jobs, health, migrations, seeding adapters | `IModuleTransaction`, clocks, audit append, registration only; adapters call contracts | audit append can enlist; operational events are not business integration events | no business policy in middleware/jobs/seeders; frozen migration, tenant, retry and idempotency guards |

Illustrative contract shapes (not implementation code):

```csharp
public interface IEggOperationsModule {
    Task<Outcome<SubmissionResult>> Submit(SubmitDailyEntry request, CancellationToken ct);
    Task<Outcome<Reservation>> ReserveFifo(StockRequest request, CancellationToken ct);
}
public interface IFlockActivityPort {
    Task<Outcome<FlockEligibility>> GetFlockEligibility(Guid flockId, DateOnly on, CancellationToken ct);
    Task<Outcome> AppendMortality(MortalityChange change, CancellationToken ct);
}
public interface IModuleTransaction {
    Task<Outcome<T>> Execute<T>(Func<CancellationToken, Task<Outcome<T>>> work, CancellationToken ct);
}
```

These DTOs contain IDs, values and versions only. They never contain an aggregate, EF
entity, repository, context, change tracker or deferred query.

## 7. Data and EF Core strategy

Retain **one `AppDbContext`** and one migration assembly. Multiple contexts sharing these
tables would duplicate tenant filters/configuration, complicate the execution strategy and
make atomic workflows fragile without solving a demonstrated runtime problem.

* Move configurations into owner-named folders/assemblies over time, discovered explicitly
  by Platform. Keep `AppDbContext` internal to Platform/implementation persistence adapters.
* codex review, PR #423 round 3: putting a cross-owner relationship in "a Platform-owned
  composition file" (this bullet's previous text) does not compile — Platform would need to
  reference the owning module's Implementation assembly for the entity type (`Expense`), while
  that module's repository needs Platform's `AppDbContext`, an unavoidable
  `Platform.Persistence` ↔ `Finance.Implementation` cycle. The actual fix is narrower: a
  cross-owner foreign key is never expressed through EF's fluent relationship API
  (`HasOne<Flock>()`) at all, by either side — that generic type parameter is exactly what
  forces the cycle. `Expense`'s own configuration declares `FlockId` as a plain scalar/shadow
  `Guid` column (already true today — `Expense`'s domain model carries only the ID, no
  navigation) and needs no reference to `Flock`'s CLR type to do so. The FK **constraint**
  itself — enforcement, cascade behavior, the index — is emitted as a raw migration operation
  (`migrationBuilder.AddForeignKey(...)` by table/column name), which needs no C# reference to
  either module's entity type either. Same posture #283's base-reference-data migrations
  already take (raw SQL over EF's data-seeding API) and consistent with §3.3's existing
  statement that cross-owner FKs are retained as database integrity constraints, not EF
  navigations. Authoring location: each owner's own configuration for its own columns; the
  migration file (already Platform-authored, per #407) for the cross-owner constraint. No
  Platform-owned composition file, no cycle, no digest change.
* Never edit/regenerate `20260801190854_InitialCreate`. Keep one ordered migration stream;
  every schema change is a new migration. Structural extraction should require no schema
  migration. Preserve the model snapshot and the migration digest guards.
* Keep cross-owner FKs and document them in `table-ownership.yml` with owner, referenced
  owner, purpose and allowed read/write adapter. Cross-owner navigations are removed from
  module-facing models only when a behavior-neutral change proves safe.
* Use the same scoped context, `TenantContext`, global filters, `TenantStampInterceptor`,
  Npgsql retry configuration and ambient transaction coordinator. `IgnoreQueryFilters`
  remains limited to reviewed startup/operational paths.
* Give Insights an internal read-only facade which materializes DTOs/streams and checks
  tenant resolution. It may query cross-owner tables; this is a read privilege, never a
  dependency from a writer to another writer's repository.
* A guard enumerates EF model table mappings and fails when a table lacks exactly one owner;
  a second guard confirms every `AccountId` entity is filtered and stampable.

## 8. Enforcement and adversarial proof requirements

Place boring tests in `tests/Cluckwork.Architecture.Tests`:

1. Parse project references and public type signatures: contracts depend only on the
   shared kernel; implementations cannot reference peer implementations. **Mutation:** add
   a peer implementation `ProjectReference`; run the focused test and record its failure.
2. Build a directed graph from project references plus an allow-list and assert acyclic.
   **Mutation:** add a reverse contracts reference and observe the cycle test fail.
3. Inspect implementation IL/source namespaces: a module may use only its own repository
   interfaces and Platform persistence adapters. **Mutation:** inject
   `IEggLotRepository` into Commerce and observe failure.
4. Inspect endpoint constructor/parameter types and source: endpoints may call a module
   contract or approved workflow, not handlers/repositories/context. **Mutation:** restore
   one direct handler parameter and observe failure.
5. Reflect every public contract signature recursively and reject namespaces/types matching
   EF, `DbContext`, `DbSet`, `IQueryable`, entity/aggregate bases, repository implementations
   or module implementation assemblies. **Mutation:** return `IQueryable<EggLot>` and
   observe failure.
6. Compare relational model table names with `docs/architecture/table-ownership.yml`;
   require exactly one owner and validate documented cross-owner FKs. **Mutation:** remove
   `Payments` ownership and observe failure.
7. Preserve existing guards for Version increments, concurrency races, tenant filters,
   idempotency/statuses, FIFO lock ordering, credential epoch, migration freeze and retry
   boundaries. Each new guard's PR includes the red mutation output; revert the mutation
   before commit. A claim without recorded mutation evidence is incomplete.

Do not use a clever custom Roslyn analyzer initially. Project-reference tests, reflection
and a small YAML ownership manifest are portable, understandable, and cheap to repair.

## 9. Rejected alternatives

* One module per aggregate: rejects depth, creates synchronous chatter and fractures
  transactions already proven atomic.
* Separate schemas/databases or microservices: no demonstrated deployment/data problem;
  worsens atomic ledger workflows and violates the single-deploy goal.
* An event bus for stock creation/adjustment: turns authoritative invariants into eventual
  consistency and changes HTTP failure semantics.
* A global public repository/UoW or public `AppDbContext`: preserves the current backdoor.
* Reporting calling every module repeatedly: shallow interfaces, poor query plans and
  inconsistent snapshots. A controlled read-side adapter is the honest seam.

## 10. Independent-review disposition

Three independent reviews were requested before finalizing implementation: domain
boundaries, EF/data ownership, and incremental delivery. Their Important findings and the
document amendments are recorded in the companion plan's review log. No implementation
phase may start while an Important finding remains open.

## 11. Owner decisions, risks and finish line

### Decisions requiring the owner

1. Confirm **Egg Operations** as the owner of DailyEntry/grades/lots/egg ledger and **Commerce** as the caller of its deep stock seam.
2. Approve Insights' controlled cross-table read privilege versus building projections now.
3. Choose whether module implementations are one assembly per module initially or grouped
   temporarily in one implementation assembly while contracts stabilize.
4. Decide whether audit remains Platform-owned or becomes an Insights-owned append-only
   store; the proposed plan keeps writes in Platform and reads in Insights.
5. Confirm acceptable delivery horizon and whether CI build-time growth from new projects
   has a measurable budget.

### Top five risks

1. Breaking an atomic ledger/cache transaction while replacing repository calls with seams.
2. Changing FIFO lock acquisition order and introducing deadlocks or allocation races.
3. Leaking EF/entity types through a convenient contract, recreating coupling under new names.
4. Weakening tenant or credential fail-closed behavior through duplicated contexts/adapters.
5. Allowing compatibility adapters to become permanent, leaving two callable architectures.

### Recommended first slice

Pilot **Finance** after characterization/guards. Its 13 application files have only three
measured outgoing feature references (Farm currency and Flock validation), no
incoming write dependency, no FIFO locks, and a small two-table model. That makes its seam
real—not isolated by convenience—while still testing cross-module reference validation,
tenant ownership, audit, money and optimistic concurrency. Access is lower business
coupling but unacceptable as a first learning experiment because its security blast radius
is high.

### Objectively testable definition of “modular monolith achieved”

All business endpoints/jobs/CLI/seed adapters call module contracts or an approved workflow;
all module implementations are inaccessible to peers; the dependency graph is acyclic;
every relational table has exactly one documented owner; no contract exposes persistence;
cross-module writes are limited to the enumerated transaction workflows; architecture
mutations fail; all existing build/unit/integration/frontend/simulation contract checks pass;
and compatibility adapters/repository registrations outside module composition are zero.

### This re-architecture will not

Change `/api/v1` contracts, user-visible behavior, database schema, migration baseline,
hosting provider assumptions, deployment/process/database count, business rules, FIFO or
credential semantics. It will not introduce network messaging, promise microservice
extraction, redesign the SPA, or combine structural moves with product features.
