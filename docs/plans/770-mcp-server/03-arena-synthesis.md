# Arena synthesis note — #770 MCP server design

## Base: candidate 2 (fable)

Picked by the orchestrator and independently by the cross-judge (different model), on the
same reasoning: it is the only candidate that **verified the SDK** rather than reasoning from
the brief, and it was right on both facts the rest of the design rests on. Agreement between
an independent judge and the parent confirms the pick per arena Phase D.

Per-criterion (cross-judge, 0-10): C1 8/8/9/7/7/6, **C2 9/9/6/9/8/9**, C3 4/3/4/6/3/5.
C2 leads on every criterion except interface depth, which is exactly where C1 leads and
exactly what was grafted.

## Grafted from candidate 1 (opus)

1. `McpIdempotency.ScopeFor(accountId, toolName, arguments)` + argument canonicalizer, so a
   write tool never hand-builds a hash. C2's write tool hand-built `IdempotencyScope` and
   switched over `IdempotentRun` — ~25 lines of protocol plumbing per tool, which is the
   pass-through shape one layer down.
2. The write tool's **argument shape**: flock name-or-id, `FarmId`/`HouseId` derived from the
   flock aggregate, grade name-or-id with the valid set named in the error. This removes the
   phantom `houseId` from the tool schema.
3. `Money(Amount, Currency)` rendered at the boundary. C2 and C3 both put `long` minor units
   on the wire.
4. `[MirrorsRoute]` / `[MirrorsNoRoute]` parity guard using **set containment** of admitted
   `EffectiveAccountRole`, derived from the live `IAuthorizationService`. Replaces C2's
   hand-listed per-role expectations — a remembered list, which is what AGENTS.md says a
   registry guard exists to stop anyone trusting.
5. The `UserId != Guid.Empty` system-actor check. `ResolveSystemActor` sets `IsResolved = true`
   with `UserId = Guid.Empty` and zero roles, which `Roles.ResolveEffective` reads as Worker,
   which with zero assignment rows is `FlockScopeGuard`'s account-wide case. C2 lacked it.
6. The `IsUnrestricted`-instead-of-`IsResolved` mutation, added to the `FlockScope` guard row.
   This is the plausible WRONG fix and the rubric's named trap.
7. A boot guard under the sim harness's Production-shaped config asserting `/mcp` maps —
   cheap proof that #370/#565 stay untouched.

## Grafted from candidate 3 (sonnet)

Little was structural; C3 scored lowest and two of its guards cannot go red as written.
Kept, with their stated reasons:
1. The in-body money gate on `list_sales_orders`, as a comment rather than a second check —
   the reason is sound (a later widening of the coarse policy must not widen the money
   question).
2. Its open question on `Idempotency.InProgress`: MCP has no `Retry-After`, so the reply
   shape needs a decision on suggested backoff.
3. `get_stock_summary`'s optional `asOfDate` defaulting to the farm clock.

## Rejected

- **C3's "the tool layer shouldn't be a deep module".** A legitimate position — the HTTP
  endpoints are that shape — but its write tool changes nothing about the argument set and
  injects `IHttpContextAccessor` into a tool constructor. Handing a model a required GUID for
  `houseId`, an entity that does not exist until Phase 2, is the leak that decided it.
- **C1's derived-idempotency-key default.** Rejected on a hazard C1 did not state and the
  judge found: record N -> a manager edits to M in the SPA -> the caller re-sends N -> replay,
  no write, for as long as the idempotency row outlives the purge sweep. Over HTTP this cannot
  happen because the SPA mints a fresh key per submit. Kept C2's required key; derived keys
  become an explicit per-tool opt-in.
- **All three candidates' `PerSessionExecutionContext` line.** `[Obsolete]` with a
  DiagnosticId at 2.2.0 + `TreatWarningsAsErrors` = build error. Irrelevant under Stateless
  (only read in the stateful branch). Deleted, not suppressed.
- **All three candidates' guard on `ScopeRequests`.** A false-alarm guard: the SDK forces the
  value itself under Stateless, so the stated mutation (delete our line) turns the guard red
  while the product stays safe. Replaced by a guard on `SessionMode == Stateless` and
  `ConfigureSessionOptions == null`.

## Dropouts

None. All three candidates produced complete packages.

## Verification performed by the orchestrator (primary sources, not candidate summaries)

- `WithHttpTransport` v2.2.0 source: 3 `TryAddSingleton` + 1 `AddHostedService`, unconditional.
- `IdleTrackingBackgroundService.StartAsync`: returns early under Stateless -> registered but inert.
- `StreamableHttpHandler.CreateSessionAsync` L544-569: under `serveStatelessly`,
  `mcpServerServices = context.RequestServices` and `ScopeRequests = false`, set BY THE SDK.
- `HttpServerTransportOptions.PerSessionExecutionContext` L203: `[Obsolete(..., DiagnosticId)]`.
- `IdempotencyMiddleware.InvokeAsync` L92-160: all-POST + `/mcp` not exempt + tenant resolved
  => 400 on every MCP request including `initialize` and `tools/list`.
- `FlockScope.cs` L25/L27: `IsUnrestricted = true` default, separate `IsResolved`.
- `BodyReadingEndpointTests`: walks body-capable handlers; `/mcp` needs `ReadsRequestBodyAttribute`.

Claims asserted by candidates and found FALSE are listed in [`04-verified-findings.md`](04-verified-findings.md) and in the
judge report; the three that would have shipped a defect are the hosted-service claim, the
`ScopeRequests` load-bearing claim, and the obsolete-member build break.
