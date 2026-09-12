# Orchestrator verification of contested candidate claims

Verified against the csharp-sdk **v2.2.0 tag** source and the shipped 2.2.0 nupkg, by the
orchestrator, after the candidates disagreed. These supersede any candidate assertion.

## FINDING 1 — #271 is touched. Candidates 1 and 3 are WRONG; candidate 2 is RIGHT.

`HttpMcpServerBuilderExtensions.WithHttpTransport` (v2.2.0) registers, UNCONDITIONALLY,
regardless of session mode:

```csharp
builder.Services.TryAddSingleton<StatefulSessionManager>();
builder.Services.TryAddSingleton<StreamableHttpHandler>();
builder.Services.TryAddSingleton<SseHandler>();
builder.Services.AddHostedService<IdleTrackingBackgroundService>();
```

Three singletons and one hosted service. Candidate 1's DESIGN.md says "No hosted service, no
singleton with mutable state"; candidate 3 says "no new in-process session state, so #271 is
untouched". Both are factually wrong about what gets registered.

**But the conclusion survives, for a different reason than either gave.**
`IdleTrackingBackgroundService.StartAsync` short-circuits under Stateless:

```csharp
// In stateless mode there are no sessions to track, so skip starting the periodic timer entirely.
if (_options.Value.SessionMode is HttpServerSessionMode.Stateless)
    return Task.CompletedTask;
```

So under `SessionMode.Stateless` the hosted service is registered but **inert**: no timer, no
prune loop, and `StatefulSessionManager` holds no sessions because none are ever created.
#271's invariant is not substantively violated — but it is now load-bearing on a session-mode
setting, which is exactly the kind of thing that gets flipped later.

**The meta-finding, which matters beyond this issue.** AGENTS.md instructs re-deriving the
#271 blocker list by "walking every `AddSingleton`/`AddHostedService` under `src/`". That walk
is structurally blind to a hosted service registered inside a NuGet package. AGENTS.md already
records that this list "was twice derived wrongly"; this is a third shape of wrongness, and
per its own "two misses of the same shape mean the METHOD is wrong" rule, the walk's SCOPE is
the defect, not any individual derivation. Raised as #786 and fixed in PR #790: `AGENTS.md`'s walk
now reaches into package `Add*`/`With*` extensions, and `docs/decisions/271-single-serving-instance.md`
carries the narrative as the third shape.

## FINDING 2 — The SDK already sets `ScopeRequests = false` under Stateless.
## Candidate 3's "single load-bearing line" framing is WRONG.

`StreamableHttpHandler.CreateSessionAsync` (v2.2.0, lines 544-569):

```csharp
var mcpServerServices = applicationServices;
if (serveStatelessly || HttpServerTransportOptions.ConfigureSessionOptions is not null || configureOptions is not null)
{
    if (serveStatelessly)
    {
        mcpServerServices = context.RequestServices;   // <-- the HTTP request's scope
        mcpServerOptions.ScopeRequests = false;        // <-- set BY THE SDK
    }
    ...
}
var server = McpServer.Create(transport, mcpServerOptions, loggerFactory, mcpServerServices);
```

Under Stateless the SDK hands tools `context.RequestServices` — the very scope middleware
resolved — and turns `ScopeRequests` off itself. Setting `ScopeRequests = false` in our own
options lambda is therefore **redundant** under Stateless. Harmless and arguably good
self-documentation, but it is NOT the thing standing between this design and the hazard, and
candidate 3 builds its entire safety argument on that line being load-bearing.

**Consequence, and it changes what the design must protect against.** The realistic regression
is NOT "someone deletes `ScopeRequests = false`". It is **"someone changes `SessionMode`"** —
to `Stateful`, or to `StatefulForInitializeClients`. At that point `serveStatelessly` is false
for at least some requests, `mcpServerServices` reverts to the ROOT application services, and
every tool silently gets a fresh scope with unresolved tenant, actor and flock scope. Under
`StatefulForInitializeClients` this is per-request MIXED — some calls correctly scoped, some
not — which is the worst possible failure shape to debug.

This makes the ENFORCEMENT mechanism the real safety property, and it discriminates sharply
between the candidates:
- Candidate 2's `McpCallContext` checks **reference-equality** between its injected
  `TenantContext` and the one resolved from `HttpContext.RequestServices`. That check FAILS
  under a Stateful flip — it is the only proposed mechanism that actually detects this
  regression.
- Candidate 1's `FarmSession` throws when the three primitives are unresolved. Under a
  Stateful flip they WOULD be unresolved, so it fires too — slightly less directly, but it
  fires.
- Candidate 3 has no mechanism; it would regress silently.

A guard must therefore pin `SessionMode` itself, not `ScopeRequests`.

## FINDING 3 — corroborating detail

`StreamableHttpHandler` confirms `IsStatelessOnly => SessionMode is Stateless`, and
`StartNewSessionAsync(context, serveStatelessly: IsStatelessOnly)` — so the stateless path is
selected per request, and the 2026-07-28+ protocol revision path also serves statelessly
(line 456-457) even on a stateful-configured server. Mixed-mode is real, not hypothetical.

## FINDING 4 — Unmodified, the idempotency middleware 400s EVERY MCP request.
## Candidate 2 is RIGHT and the severity is higher than candidates 1 and 3 stated.

Verified by reading `IdempotencyMiddleware.InvokeAsync` (src/Cluckwork.Api/Middleware/
IdempotencyMiddleware.cs, ~line 92-160). The gate, in order:

1. exception-handler re-entry (#345) -> skip. Not our case.
2. `if (!IsPost && !IsPut && !IsPatch && !IsDelete) -> skip`.
   **MCP Streamable HTTP sends EVERYTHING as POST** — `initialize`, `tools/list`,
   `tools/call` for a pure read, all of it. So nothing skips here.
3. `ResponseNotCacheable.Any(path.StartsWithSegments)` -> skip. `/mcp` is not in that list.
4. `if (!tenant.IsResolved) -> skip`. An authenticated MCP request DOES resolve a tenant,
   so this does not skip either. (Note the irony: it would only skip while broken.)
5. `if (no Idempotency-Key header) -> 400 "Idempotency-Key header is required for write
   requests."`

An MCP client has no knowledge of Cluckwork's `Idempotency-Key` header contract and no way to
attach one per JSON-RPC call. So on an unmodified pipeline **every MCP request 400s**, and the
server never completes `initialize`. This is not a key-collision nicety — MCP does not
function at all without the exemption.

Practical consequence for phasing: the `/mcp` idempotency exemption is **phase-zero, blocking,
and required even for a pure read-only demo**. #770's "read-only demo in days" estimate did
not account for it. The `EndpointHash` collision that candidates 1 and 3 lead with is a real
but strictly SECOND-order problem, only reachable after the exemption exists.

Also confirmed while reading: `endpointHash = Sha256($"{Method}:{Path}")` — identical for every
tool on one `/mcp` route, as the rubric states; and the `/api/v1/me` user-scoping special case
means the key-scoping rule is already path-dependent, which an extracted port must preserve.
