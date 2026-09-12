# Guards — synthesized design, #770

Per AGENTS.md: a guard whose red-mutation you cannot state is not yet a guard, and a wrong
guard is worse than none because it reads as safety. Every row names its mutation. Method
matters more than count: prefer "walk everything, exclude deliberately".

## Identity bridge

| # | Guard | Invariant | Red mutation |
|---|---|---|---|
| 1 | `McpCallContext_Throws_WhenTenantUnresolved` | the boundary type cannot be built in an unpopulated scope | delete the `TenantContext.IsResolved` clause from the factory |
| 2 | `McpCallContext_Throws_WhenActorUnresolved` | same, for the actor | delete the `CurrentUserContext.IsResolved` clause |
| 3 | `McpCallContext_Throws_WhenFlockScopeUnresolved` | same, for flock scope | delete the `FlockScope.IsResolved` clause |
| 3b | **`McpCallContext_ChecksIsResolved_NotIsUnrestricted`** | the *correct property* is read | change the clause to `FlockScope.IsUnrestricted`. **This is the plausible WRONG fix** — `IsUnrestricted` defaults to `true`, so a guard written against it passes while the hole stays open. Graft from candidate 1 (INV-MCP-2c). |
| 4 | `McpCallContext_Throws_WhenActorIsSystemActor` | an MCP call is never a system actor | delete the `UserId != Guid.Empty` clause; `ResolveSystemActor` sets `IsResolved = true` with an empty id and zero roles, which resolves to Worker, which with zero assignment rows is `FlockScopeGuard`'s account-wide case |
| 5 | `McpCallContext_Throws_WhenTenantIsNotTheRequestScopes` | the injected scope really is the HTTP request's | make the comparison value-based instead of `ReferenceEquals`. This is the only guard that detects a later `SessionMode` flip. |
| 6 | `EveryToolTakes_McpCallContext` (assembly walk) | no tool can run without the parse | add a `[McpServerToolType]` whose constructor omits it. Walks every type in the assembly — not a list. |
| 7 | `NoToolInjects_AmbientIdentityOrServiceLocator` (assembly walk) | no tool can bypass the parse | inject `TenantContext` / `CurrentUserContext` / `FlockScope` / `AppDbContext` / `IServiceProvider` / `HttpContext` / `IHttpContextAccessor` into any tool constructor or method |

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
| 19 | `HttpAndMcp_KeysDoNotAlias` | one namespace per operation | key the MCP scope on the `/mcp` path instead of the tool name |
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

## Deliberately not guarded, with reasons

- **`ScopeRequests`** — the SDK sets it under Stateless; see row 8.
- **`PerSessionExecutionContext`** — `[Obsolete]` with a DiagnosticId at 2.2.0 and
  `TreatWarningsAsErrors=true`, so *setting* it is a build error. Only read in the stateful
  branch, so `SessionMode == Stateless` subsumes it. All three candidates set it and would
  not have compiled.
- **`FlockScopeGuard`'s fail-open branch** — made unreachable from MCP, not fixed. Its own
  comment says flipping it "deserves its own issue rather than a drive-by". File separately.
