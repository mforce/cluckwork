# Modular-monolith incremental implementation plan

This plan implements the companion design without a flag day. Every commit builds and is
behaviorally compatible; production refactoring begins only after owner approval. Estimates
are focused engineering days including tests, not calendar commitments.

## Delivery rules

* One structural intent per commit; never mix a move with changed behavior.
* Existing `/api/v1` DTOs/status codes and SPA callers remain the compatibility oracle.
* No schema changes are expected. If one becomes necessary, add a new migration; never edit
  `InitialCreate`.
* A compatibility adapter has an owner, deletion phase and guard preventing new callers.
* Each architecture guard is mutation-tested red before its claim is accepted; mutation is
  reverted before commit and red output is attached to review evidence.
* Full integration tests require Docker. Simulation/k6/Playwright callers are reviewed when
  a write contract changes even if tests are green; this plan intends no contract changes.

## Phase 0 — baseline and characterization (2–4 days)

| Commit | Exact likely changes | Verification and completion evidence | Rollback / risk |
|---|---|---|---|
| 0.1 Record architecture baseline | add `docs/architecture/table-ownership.yml`, `module-dependencies.yml`, generated inventory script under `tools/architecture/`; no production files | script output matches EF model, 49 handlers and endpoint inventory; `dotnet build Cluckwork.sln`; `npm run typecheck --prefix web` | delete docs/script; risk is a hand-maintained false inventory, mitigated by model enumeration |
| 0.2 Characterize seams | add integration tests for current HTTP status/idempotency behavior of submit/adjust/void/confirm/void/feed; add/locate parallel-race and FIFO assertions | focused test filters plus full `dotnet test Cluckwork.sln`; record Docker limitation if any | tests only; risk is asserting implementation rather than observable contracts |
| 0.3 Characterize security/platform | add focused tests only where missing for middleware order, fresh credential epoch, tenant filtering/stamping, job and CLI outcomes | security and migration guard filters; existing simulation verifier | revert tests; never alter security production code in this phase |

Dependencies: none. Completion means baseline commands are green and every invariant in the
design maps to an existing or newly added characterization test.

## Phase 1 — shared kernel and architecture guards (3–5 days)

| Commit | Exact likely changes | Verification / required mutation | Rollback / risk |
|---|---|---|---|
| 1.1 Add test project | add `tests/Cluckwork.Architecture.Tests/*.csproj`, solution entry, graph/contract test helpers; lock file | `dotnet restore`, locked restore, focused tests | remove project/solution entry |
| 1.2 Ownership guard | consume `table-ownership.yml`, enumerate EF relational model and cross-owner FKs | remove `Payments` owner → focused test red; restore → green | guard/docs only; risk: design-time tenant dependency, use existing factory fixture |
| 1.3 Dependency/signature guards | add project DAG, forbidden implementation reference, endpoint bypass and public-signature tests | individually mutate peer reference, reverse edge, direct handler endpoint and `IQueryable<EggLot>` return; every mutation must red | revert guards if they block unchanged baseline; do not weaken allow-list silently |
| 1.4 Create shared kernel | add `src/Cluckwork.SharedKernel`; initially link/move only stable `Result`, `Error`, `Money`, clocks/paging DTO primitives using compatibility type-forwarding where needed | build and all tests after each type; API surface snapshot unchanged | revert project refs/type forwards; risk is broad namespace churn—keep types in original namespace initially |

Dependencies: Phase 0. Completion means all six architecture mutations described in the
design have recorded red evidence and clean-tree tests are green.

## Phase 2 — Finance pilot (5–8 days)

| Commit | Exact likely changes | Adapter and verification | Rollback / risk |
|---|---|---|---|
| 2.1 Contract project | add `src/Cluckwork.Modules.Finance.Contracts` with use-case commands/results and `IFinanceModule`; add contract tests | existing endpoint DTO maps 1:1 through an API adapter; signature guard | project is additive |
| 2.2 Implementation shell | add `src/Cluckwork.Modules.Finance.Implementation`; internal registration extension; wrap existing handlers behind contract without moving behavior | temporary `LegacyFinanceModuleAdapter`; handler unit tests plus module-interface tests | DI switches back to legacy handlers |
| 2.3 Move domain/application | move Expense/category model, handlers and validators mechanically; preserve namespaces first, then separate namespace-only commit; preserve the account `FOR SHARE` currency lock through a synchronous lock-aware Farm port | type forwards or narrow legacy repository adapters; build/test after each move | revert individual move; no schema/model configuration edit |
| 2.4 Move persistence adapter | move Expense's own configuration/repository behind internal Finance implementation; keep the `Expense`→`Flock` relationship via the **string-based** non-generic `HasOne("Cluckwork.Domain.Flock")...HasForeignKey("FlockId").HasConstraintName("FK_Expenses_Flocks_FlockId")` (no `HasOne<Flock>()` generic and no `typeof(Flock)` — either would force a `Finance`→`Farm`/`Platform.Persistence`↔`Finance.Implementation` reference, design §7); this reproduces the FK/index `InitialCreate` already created byte-for-byte, so it needs **no new migration** — a fresh `AddForeignKey` for the same constraint name would fail against an already-migrated database | EF model digest before/after identical (zero pending migration); integration CRUD/tenant/version/audit tests | restore old registrations/files |
| 2.5 Switch endpoints and remove adapter | change `ExpenseEndpoints` to inject `IFinanceModule`; central DI delegates to `AddFinanceModule`; remove legacy handler registrations/adapters | endpoint contract and SPA tests; guard reports zero endpoint bypasses | one-commit DI rollback |

Principal risks: currency snapshot and optional flock validation may be accidentally copied
instead of called through Farm/Flock seams; EF configuration discovery may change the
model. Completion: Finance can be tested solely through its contract, peers cannot reference
implementation, EF digest and HTTP behavior are unchanged, and compatibility adapters are
zero. Hold a retrospective before Phase 3 and adjust templates/guards.

## Phase 3 — Farm module (4–7 days)

Commits: (3.1) Farm contracts (`IFarmModule`, immutable currency/timezone/context snapshot);
(3.2) wrapper and API adapter; (3.3) mechanical Account/FarmLogo/settings moves; (3.4)
persistence configurations/repositories; (3.5) switch account/logo endpoints and clocks;
(3.6) remove adapters. Likely touched files are current Accounts domain/features,
`AccountRepository`, `FarmLogoRepository`, their configurations, account endpoints,
`FarmClock`, `TenantContext` adapter and DI extensions.

Tests: settings/logo HTTP contracts, invalid timezone fail-closed, currency-bound-row probe,
tenant query/stamp tests, EF model digest, SPA Account/Settings/FarmContext tests. Roll back
at each DI switch. Risk: tenant resolution must not depend cyclically on a tenant-filtered
Farm query; keep tenant identity in Platform and farm settings behind the resolved seam.
Effort 4–7 days. Depends on the Finance pilot lessons.

## Phase 4 — Flock Management (5–9 days)

Commits: add contracts for lifecycle commands, eligibility/scope/name lookups and mortality append; wrap existing handlers; switch flock endpoints; mechanically move `Flock`/`BirdMovement`, repositories and configuration; convert Access, Inventory and Finance callers to lookup contracts; remove adapters. Run lifecycle, scope, Version/race, tenant and API tests plus the EF model digest. Roll back each endpoint registration independently. Main risk: breaking scope checks or mortality enlistment. Depends on Farm.

## Phase 5 — Egg Operations (10–16 days)

Commits: add contracts/registration; move EggGrade behavior; move DailyEntry/lines and lock-sweep behavior; move EggLot/egg movement and stock reads; move submit/adjust/void whole using the Flock mortality transaction port; switch daily-entry/grade/stock endpoints and lock-sweep; remove adapters. Likely files are Domain DailyEntry/EggGrade/EggLot/EggInventoryMovement, Application DailyEntries/EggGrades/EggLots/Eggs, matching persistence/endpoints and the lock sweep. Test parallel submit/adjust/void, sold floors, canonical FIFO locks, cached balance=ledger, rollback after each write, tenant, HTTP and SPA contracts; EF model digest must match. Roll back per endpoint/job registration. Risks: lock drift and partial mortality/stock commit. Depends on Flock and Farm clock.

## Phase 6 — Commerce and deep stock seam (10–18 days)

Commits: add Commerce contracts; mechanically move catalog/customer/order draft/payment behavior; add opaque Egg Operations `ReserveFifo`/`RestoreReservation`; move confirm in one ambient transaction; mutation-fail after lot allocation, egg movement, allocation and order confirmation to prove rollback; move void with order-first then canonical lot locks, payment gate, provenance release, compensating ledger and audit; switch endpoints; remove adapters. Likely files are Domain Catalog/Sales, related Application, persistence/endpoints, stock port and transaction coordinator. Run product snapshots, order Version, parallel confirm/void, FIFO, insufficient-stock rollback, payments, provenance, tenant, retry and SPA contracts. No event performs authoritative writes. Roll back through the prior endpoint adapter. Risks: partial commit, lock inversion and replay. Depends on Egg Operations and Farm currency.

## Phase 7 — General Inventory (6–10 days)

1. Contract/wrapper; 2. item/purchase/adjust FIFO moves; 3. water moves; 4. feed usage with
Flock eligibility seam; 5. persistence/endpoints switch; 6. adapters removed.
Likely files are Domain/Application Inventory, inventory configuration/repositories,
Inventory/Water endpoints and DI. Tests include backdated FIFO, insufficient rollback,
currency, concurrent lot consumption, flock eligibility behavior, tenant/version, API and SPA.
Retain one inventory transaction. Roll back per endpoint registration. Risk: importing Flock entities/repositories instead of the read-only eligibility decision. Effort 6–10 days; depends on Farm and Flock contracts.

## Phase 8 — Access (10–18 days, security hold point)

After all lower-risk module mechanics are proven:

1. Add Access contracts around existing use cases without rewriting Identity.
2. Switch users/me endpoints and flock assignment through contracts.
3. Switch auth endpoints while preserving cookies, JWT claims and error/timing behavior.
4. Switch credential middleware to `ICredentialEpochVerifier`; prove fresh DB read per request.
5. Move bootstrap/recover services and refresh purge behind Access; CLI/job adapters call it.
6. Internalize Identity implementation and remove legacy `IIdentityProvider` exposure.

Likely touched: Domain Accounts role assignment, Application Users/common identity ports,
Infrastructure Identity, user repository/config, Auth/Users/Me endpoints, credential and
must-change middleware, bootstrap/recovery CLI and DI. Run every security integration test,
retry-boundary test, timing/lockout tests, bootstrap/break-glass drills and tenant tests.
Require a dedicated security review before switching DI. Roll back at adapter switches;
never dual-issue tokens. Principal risk is credential revocation regression. Effort 10–18
days; depends on stable Production assignment and Farm account contracts.

## Phase 9 — Insights and audit (6–10 days)

1. Add Insights contract/materialized DTOs and restricted read-session abstraction.
2. Move report queries without changing SQL semantics.
3. Move repeatable-read export snapshot/streaming.
4. Expose audit reads; leave append sink in Platform and document ownership.
5. Switch report/export/audit/dashboard endpoints and remove old query registrations.

Likely files: Application Reports/Export/Audit query contracts, Infrastructure ReportQueries,
ExportQueries/Audit repository, corresponding endpoints and SPA API wrappers only if imports
move (URLs/types do not). Tests: report totals, tenant isolation, export exact datasets and
repeatable snapshot, cancellation/stream failure, query guard forbidding writes/IQueryable.
Rollback via DI. Risk: attempting purity through N+1 module calls; retain controlled SQL.
Effort 6–10 days; depends on ownership manifest, not all write moves.

## Phase 10 — Platform adapters, seeding and cleanup (7–12 days)

1. Move idempotency, transaction, tenant, persistence and health registration into explicit
   Platform composition; preserve middleware ordering.
2. Convert daily lock/token purge jobs to contract callers; retain durable worker behavior.
3. Convert demo/simulation seeders to module bootstrap/orchestration contracts one dataset at
   a time; preserve durable anchor, exact counts and fail-closed validation.
4. Convert CLI verbs to contracts; migrate remains direct Platform migration operation.
5. Delete generic repository/UoW and legacy feature registrations once `rg` proves no caller.
6. Tighten architecture allow-list and document final graph/table ownership.

Run full solution tests, locked restore/build, frontend typecheck/tests/build, simulation
verification and (when available) k6/Playwright. Because production boot guards/config are
not changed, simulation manifests should not need configuration changes; any discovered
change updates all harness files in the same commit. Rollback each adapter separately.
Risks are seed drift and compatibility adapters surviving. Effort 7–12 days.

## Verification command set

Run as applicable after every commit; full set at phase boundaries:

```bash
dotnet restore Cluckwork.sln
dotnet restore Cluckwork.sln --locked-mode
dotnet build Cluckwork.sln --no-restore
dotnet test tests/Cluckwork.Architecture.Tests/Cluckwork.Architecture.Tests.csproj
dotnet test tests/Cluckwork.Domain.Tests/Cluckwork.Domain.Tests.csproj
dotnet test tests/Cluckwork.Application.Tests/Cluckwork.Application.Tests.csproj
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj
npm --prefix web run typecheck
npm --prefix web test -- --run
npm --prefix web run build
bash tools/simulation/verify-harness.sh
git diff --exit-code -- src/Cluckwork.Infrastructure/Persistence/Migrations/20260801190854_InitialCreate.cs
```

For every endpoint write, also search non-CI callers before changing a command:
`rg -n 'daily-entries|sales|inventory|expenses|payments' web tools src tests`.

## Independent review log and amendment gate

Before implementation, obtain three reviews against both documents:

| Review | Required questions | Important-finding disposition |
|---|---|---|
| Domain boundaries | Independent review found DailyEntry/grades/lots/egg ledger too coupled to split and Sales needs a deep stock seam. | **Resolved:** Egg Operations now owns that invariant cluster; Flock is a synchronous participant and Commerce uses opaque reserve/restore contracts. |
| EF/data ownership | Review required one scoped context/migration stream, centralized tenant/retry policy, physical FKs/locks, and privileged EF composition. | **Resolved:** one context, ownership manifest, ambient transactions and read-only Insights are explicit; CLR visibility remains an owner decision. |
| Incremental delivery | Review required Expenses HTTP characterization/facade first, lock-aware currency, adapter deletion and no broad test friend access. | **Resolved:** Phases 0–2 and delivery rules now require these constraints. |

All Important findings from the 2026-08-04 independent reviews are resolved in these documents. New Important findings re-block implementation; record evidence, amendment and resolution, then repeat review. This is a hard gate, not a ceremonial checklist.

## Rollout/rollback policy

This is an in-process structural rollout: deploy after any green phase if desired. There is
no data backfill or dual-write. The rollback unit is the last endpoint/job/CLI DI switch;
retain the old adapter for exactly one subsequent commit, then delete it after production
confidence. If EF model digest, SQL, route snapshot or response characterization changes,
stop and split the behavioral difference into a separately authorized change.

## Completion evidence

The re-architecture is complete only when the objective finish line in the design passes,
the independent review log has no Important item, full verification is green, source search
finds no endpoint/job/CLI direct handler/repository/context use, the allow-list contains only
documented workflow/read exceptions, every table has one owner, and no temporary adapter
remains.

It does **not** create services, databases/schemas, new endpoints, migrations, product
behavior, provider configuration or asynchronous authoritative writes. The recommended
first authorized implementation slice is Phases 0–2 (characterization, mutation-proven
guards and Finance pilot), with an owner review before proceeding to Flock Management or Egg
Operations.
