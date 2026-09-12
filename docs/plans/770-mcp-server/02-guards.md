# Guards — synthesized design, #770

Per AGENTS.md: a guard whose red-mutation you cannot state is not yet a guard, and a wrong
guard is worse than none because it reads as safety. Every row names its mutation. Method
matters more than count: prefer "walk everything, exclude deliberately".

## Identity bridge

| # | Guard | Invariant | Red mutation |
|---|---|---|---|
| 1 | `McpCallContext_Throws_WhenTenantUnresolved` | the boundary type cannot be built in an unpopulated scope | delete the `TenantContext.IsResolved` clause from the factory |
| 2 | `McpCallContext_Throws_WhenActorHasNoIdentity` | the actor requirement, tested honestly as ONE clause | delete **both** the `CurrentUserContext.IsResolved` clause and the `UserId != Guid.Empty` clause (row 4). **Deleting only `IsResolved` leaves this green** — an unresolved `CurrentUserContext` has `UserId == Guid.Empty`, so row 4's check still throws, and `Resolve` sets both properties together so no ordinary state isolates the clause. The original formulation was a guard that could not go red; treat `IsResolved` here as redundant defence and test the combined requirement. (#770 adversarial review, finding 5.) |
| 3 | `McpCallContext_Throws_WhenFlockScopeUnresolved` | same, for flock scope | delete the `FlockScope.IsResolved` clause |
| 3b | **`McpCallContext_ChecksIsResolved_NotIsUnrestricted`** | the *correct property* is read | change the clause to `FlockScope.IsUnrestricted`. **This is the plausible WRONG fix** — `IsUnrestricted` defaults to `true`, so a guard written against it passes while the hole stays open. Graft from candidate 1 (INV-MCP-2c). |
| 4 | `McpCallContext_Throws_WhenActorIsSystemActor` | an MCP call is never a system actor | delete the `UserId != Guid.Empty` clause; `ResolveSystemActor` sets `IsResolved = true` with an empty id and zero roles, which resolves to Worker, which with zero assignment rows is `FlockScopeGuard`'s account-wide case |
| 5 | `McpCallContext_Throws_WhenTenantIsNotTheRequestScopes` | the injected scope really is the HTTP request's | make the comparison value-based instead of `ReferenceEquals`. This is the only guard that detects a later `SessionMode` flip. |
| 6 | `EveryToolTakes_McpCallContext` (assembly walk) | no tool can run without the parse | add a `[McpServerToolType]` whose constructor omits it. Walks every type in the assembly — not a list. |
| 7 | `NoToolInjects_AmbientIdentityOrServiceLocator` (assembly walk) | no tool can bypass the parse | inject `TenantContext` / `CurrentUserContext` / `FlockScope` / `AppDbContext` / `IServiceProvider` / `IServiceScopeFactory` / `HttpContext` / `IHttpContextAccessor` into any tool constructor or method |
| 7b | `NoToolReachesASecondaryScope` (dependency walk over a CLOSED registration model + causal tests) | a tool's data access happens in the request scope | **two mutations, and the second is the one that matters.** (a) inject a helper taking `IServiceScopeFactory` directly — caught by a constructor walk. (b) register that helper as `sp => new Helper(() => sp.CreateScope())` — **the constructor exposes only a delegate, so a constructor walk sees nothing**, and a static service locator adds no edge at all. The walk must therefore resolve interfaces through actual service *registrations*, reject opaque factory/delegate registrations in a tool's transitive graph unless explicitly reviewed, and define where traversal stops. **This guard bounds the hazard; it does not eliminate it** — see the honesty note below. Feasibility was checked before specifying it and the walk IS implementable — the factory/delegate-shaped registrations in `src/Cluckwork.Api` and `src/Cluckwork.Infrastructure` number in the low tens, not the hundreds, so a reviewed-exemption list is tractable. **No count is stated here on purpose**: two independent counts of it disagreed (9 vs 10) depending on the lambda parameter name matched, which is precisely how a bare count in this repo has gone stale before. Walk `ServiceDescriptor`s from the built collection rather than grepping, and stop at framework boundaries. (#770 adversarial review rounds 1 and 2.) |

## Transport and blast radius

| # | Guard | Invariant | Red mutation |
|---|---|---|---|
| 8 | `SessionMode_IsStateless` | the actual load-bearing setting | set `SessionMode = Stateful` or `StatefulForInitializeClients`. **Note what is deliberately NOT guarded:** `ScopeRequests`. The SDK forces it under Stateless, so a guard on it reddens on a mutation that leaves the product safe — a false alarm, which all three candidates proposed. |
| 9 | `ConfigureSessionOptions_IsNull` | the one hook that runs *after* the SDK's stateless branch cannot re-enable a fresh scope | assign a `ConfigureSessionOptions` callback |
| 10 | `McpEndpoint_CarriesReadsRequestBodyAttribute` | **existing** `BodyReadingEndpointTests` starts applying the moment `/mcp` is mapped | map `/mcp` without `.WithMetadata(new ReadsRequestBodyAttribute())`. `MapMcp` maps `HandlePostRequestAsync(HttpContext)` with no `IAcceptsMetadata.RequestType`, so it is body-capable and unclassified. Found by reading the guard, not by recall. |
| 11 | `McpBoots_UnderProductionShapedConfig` | #370/#565 stay untouched — no new required config key | add any required config key without teaching `bootstrap.sh`, `docker-compose.sim.yml`, `verify-harness.sh` and the AppHost |
| 12 | `Mcp_AddsNoLiveHostedServiceUnderStateless` | #271's blocker list is not extended | set `SessionMode = Stateful`, which makes `IdleTrackingBackgroundService.StartAsync` run its prune timer instead of returning early. **Asserts inertness, not absence** — the service IS registered unconditionally by `WithHttpTransport`; a guard asserting zero `IHostedService` descriptors would be red on the correct configuration. Candidate 1 proposed exactly that wrong guard. |

## RBAC

| # | Guard | Invariant | Red mutation |
|---|---|---|---|
| 13 | `EveryTool_DeclaresMirrorsRouteOrMirrorsNoRoute` (walk) | no tool escapes parity review | add a tool with neither attribute |
| 14 | `ToolAdmittedRoles_AreSubsetOfMirroredRouteRoles` | the tool cannot admit a role its route denies | tighten `/api/v1/customers` to `AdminOnly` in `Program.cs` and change nothing under `Mcp/` — **proves drift detection fires from outside the MCP code.** Sets are derived from the live `IAuthorizationService`, keyed by `WithName()` (what the code says about itself, per #632), never by URL or `file:line`. Set containment, not policy-name equality: `ProductionWrite` = {Owner,Manager,Worker} and `SalesAccess` = {Owner,Manager,Sales} — neither contains the other. |
| 15 | `MirrorsRoute_NamesResolve` | the registry stays honest | rename a route's `WithName()` and leave the attribute stale |
| 16 | `ToolsList_IsFilteredByRole` (integration, real request) | unauthorized tools are invisible, not merely refused | remove `AddAuthorizationFilters()`. Single point of proof for SDK list-filtering behaviour — flagged as a risk in [`01-design.md`](01-design.md). |

## Idempotency

| # | Guard | Invariant | Red mutation |
|---|---|---|---|
| 17 | `IdempotencyExemptions_AreExactly_Mcp` (walk `EndpointDataSource`) | exemption by metadata, and exactly one bearer | add `HandlesOwnIdempotencyAttribute` to a second endpoint |
| 18 | `McpRequest_WithoutIdempotencyKeyHeader_Succeeds` | the blocking phase-zero fix | remove the exemption; `initialize` and `tools/list` 400 immediately |
| 19 | `HttpAndMcp_KeysDoNotAlias` | one namespace per operation | replace the MCP operation key with the **HTTP adapter's computed `endpointHash`** — i.e. `Sha256($"{Method}:{Path}")` for the mirrored endpoint, using its exact method and path *including trailing-slash spelling*, with the same account and caller key. **Stating the route TEXT is not enough**: HTTP hashes it, so `OperationKey = "POST:/api/v1/daily-entries"` leaves the namespaces distinct and the test green. (Round-2 finding 3.) |
| 20 | `TwoDifferentTools_SameCallerKey_DoNotCollide` | per-tool namespace | give two tools the same `OperationKey`. Must exercise *two real tools*, not a test double that keeps its own key — candidate 3's version could not go red. |
| 21 | `EveryWriteTool_TakesAnIdempotencyKey` (walk, keyed on `McpServerToolAttribute.ReadOnly == false`) | writes cannot forget the key | add a write tool without the parameter. Fail-closed: `ReadOnly` defaults to `false`, so forgetting the annotation makes a read *stricter*, never looser. |
| 22 | `SameKey_DifferentArguments_IsRefused` | key reuse is refused, not silently executed or ignored | drop the request-fingerprint comparison |
| 23 | `Replay_WritesNoSecondAuditRow` | replay is a true no-op | drop the publish guard. **Asserts the absence of a second `DailyEntryUpdate` audit row, NOT the entry count** — the handler upserts on `(account, farm, house, flock, date)`, so a count assertion passes with idempotency entirely removed. Graft from candidate 1. |
| 24 | `AtomicIdempotencyProtocolTests` + the other #307 suites | the extraction changed no behaviour | **these must pass UNEDITED.** An edit to a #307 test in the implementing PR is a stop-and-review, not a fix. This is the mitigation for the design's largest risk. |

## Domain surface

| # | Guard | Invariant | Red mutation |
|---|---|---|---|
| 25 | `WriteTool_CallsUnmodifiedHandlerAndValidator` | no parallel validation path | inline a validation rule into the tool |
| 26 | `CrossFarmBearer_ReadsNothing` (integration, two seeded farms) | the whole point | any weakening of the identity bridge |
| 27 | `WorkerNotAssigned_CannotRecord` | `FlockScopeGuard` still runs for MCP | bypass the handler |
| 28 | `Money_NeverSerializesMinorUnits` (walk tool reply types) | no 100× error reaches a model | expose a `long …MinorUnits` on any tool reply |

| 29 | `McpEndpoint_HasAnExplicitBodyCap` | `/mcp` refuses an oversized body | remove `WithMaxRequestBodyBytes` from the `/mcp` mapping. **`ReadsRequestBodyAttribute` does NOT activate the #309 cap** — that middleware reads `MaxRequestBodyBytesMetadata`, a different type; the attribute only classifies binding errors for `BodyReadingEndpointTests`. The design originally conflated the two, so `/mcp` had no cap at all. (#770 adversarial review, finding 4.) |
| 30 | `McpEndpoint_HasARateLimitPolicy` | `/mcp` carries a budget | remove `RequireRateLimiting` from the `/mcp` mapping. #143 limiters are **opt-in per endpoint**, and `/mcp` is one route carrying every operation, so an unlimited `/mcp` is an unlimited everything. Must use the shared `IFixedWindowCounter` (#543/#544), never a process-local limiter — that is the exact #271 blocker shape twice derived wrongly. |
| 31 | `ReadTools_ReturnBoundedResponses` | every read tool's **reply** is bounded and says so | remove the page cap from a read tool; the reply grows past it and carries no completeness field. |
| 31b | `BalanceReads_AreBoundedInTheDATABASE` (row-consumption assertion) | the **query** is bounded, not just the reply | keep the reply's `Take(cap)` and remove the bound from the query. Row 31 stays GREEN — every response is correctly capped with correct continuation metadata — while `ListCustomerBalancesAsync` materializes the entire farm's balance book first. `PaymentRepository` takes no paging arguments and materializes both grouped queries before building its list, so **reusing it at all is the defect**; this needs a paged balance query, and the test must assert rows consumed from the database, not items returned. A signature walk cannot establish this. **Measure rows CONSUMED, not SQL shape.** `ReportQueryBoundingTests`' `SqlCaptureInterceptor` is the right harness shape and the right precedent (#311 was the identical "loaded the whole ledger, walked it in memory" defect), but it records `(Sql, Parameters)` only — so an implementation that fetches every page in a loop and accumulates the book emits perfectly paged SQL and passes. Keep SQL inspection as *supporting* evidence and count consumption across the whole operation: wrap the returned `DbDataReader` and count successful `Read`/`ReadAsync` calls, covering both aggregate queries and any page-fetch loop. (Round-3 finding 3 — the round-2 fix named a mechanism that cannot measure what the row requires.) (Round-2 finding 2 — row 31 alone claimed an invariant its mutation did not test.) |
| 32 | `NameResolution_IsExactlyOneAccessibleMatch` | a name-addressed write cannot hit the wrong row | make resolution take the first match. **Duplicate flock names are legal** (`FlockRepository.cs` says so, and its `ThenBy(f => f.Id)` tie-break exists because of it), so first-match records production against an arbitrary flock and `Single` throws on valid farm data. Zero matches → not-found; two or more → an ambiguity error listing candidate ids and distinguishing fields, and the follow-up write must pass an id. Test two same-name flocks in one accessible scope. (#770 adversarial review, finding 3.) |

## Where these guards stop, stated plainly

Row 7b **bounds** the secondary-scope hazard; it does not eliminate it. A closed registration model
catches constructor-injected and factory-registered escapes, but detached background work need not
change the dependency graph at all, and a static service locator adds no edge. The design must not
claim the fail-open branch is *unreachable* from MCP — only that the known routes to it are guarded
and the residual ones are named. #787 (flipping that branch fail-closed) backstops the **write** half
of the residue, which is why slice 6 is blocked on it — but a secondary-scope **read** never calls
`FlockScopeGuard` at all, so #787 does not cover it. That read residue is recorded as open risk.

Saying this here is the point: a guard described as proving more than it proves is worse than no
guard, because it reads as safety. Round 1 found three such rows, round 2 found two more in the
fixes for round 1.

## Deliberately not guarded, with reasons

- **`ScopeRequests`** — the SDK sets it under Stateless; see row 8.
- **`PerSessionExecutionContext`** — `[Obsolete]` with a DiagnosticId at 2.2.0 and
  `TreatWarningsAsErrors=true`, so *setting* it is a build error. Only read in the stateful
  branch, so `SessionMode == Stateless` subsumes it. All three candidates set it and would
  not have compiled.
- **`FlockScopeGuard`'s fail-open branch** — its routes from MCP are narrowed, not closed, and
  #787 backstops only the WRITE half (a read escape never calls the guard). Not fixed here. Its own
  comment says flipping it "deserves its own issue rather than a drive-by". File separately.
