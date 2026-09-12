# MCP server support for Cluckwork (#770) — synthesized design

Base: candidate 2. Grafts: candidate 1 (interface depth, parity guard, system-actor check),
candidate 3 (two comments and an open question). See [`03-arena-synthesis.md`](03-arena-synthesis.md).

## Problem

Expose a curated Cluckwork tool surface over MCP: reads for flocks, stock/egg lots and daily
entries; reads for customers, sales orders and balances; and one write, record daily entry.

The tool code is the easy part — a tool is the same kind of adapter a minimal-API endpoint is.
Three things make the shape non-obvious, and all three were discovered by reading source
rather than by reasoning from the SDK's documentation.

**1. Identity is scoped, and the scope is not guaranteed to be the request's.** `TenantContext`,
`CurrentUserContext` and `FlockScope` are scoped services populated by middleware acting on the
*HTTP request's* DI scope. `McpServerOptions.ScopeRequests` defaults to `true` — a fresh scope
per tool call. In a fresh scope, writes throw (`IAuditWriter` fails closed, #500) and reads go
**silently wrong**: the EF global query filters match `AccountId == Guid.Empty`, and
`FlockScope.IsUnrestricted` defaults to `true`, widening a #388-narrowed Worker to the whole
farm. The throw is an accident of which handlers audit — `RecordFeedUsage` and
`RecordWaterUsage` audit nothing, reach `FlockScopeGuard`'s documented fail-open branch, and
get account-wide access in silence. They are one tool away from this surface.

**2. The load-bearing setting is not the obvious one.** Under `SessionMode.Stateless` the SDK
*itself* sets `mcpServerServices = context.RequestServices; mcpServerOptions.ScopeRequests =
false` (`StreamableHttpHandler.CreateSessionAsync`, v2.2.0 L544-569). So setting
`ScopeRequests = false` in our own options lambda is **redundant**, and a guard on it is a
false alarm. The real regression is a later change to **`SessionMode`** — under `Stateful`,
`mcpServerServices` reverts to the root provider; under `StatefulForInitializeClients` it is
*per-request mixed*, the worst shape to debug.

**3. Unmodified, the pipeline 400s every MCP request.** `IdempotencyMiddleware` triggers on all
of POST/PUT/PATCH/DELETE, `/mcp` is not exempt, and an authenticated MCP call resolves a
tenant — so with no `Idempotency-Key` header it returns 400. MCP sends *everything* as POST,
`initialize` and `tools/list` included, and no MCP client knows Cluckwork's header contract.
MCP does not function at all until `/mcp` is exempted. This is phase-zero and blocking, even
for a read-only demo; #770's "read-only demo in days" did not account for it.

Constraints carried in from Phase A: #530/#546 single-assignment tenant; #500 audit actor as
an authorization input; #364 per-request credential-epoch read that must not be cached; #307's
claim/execute/publish protocol; #271's one-serving-instance walk; #370/#565's out-of-CI
harnesses; #684/#146 packaging.

## Usage (caller's view)

### Connecting

No new process and nothing to install. The server is the API already running.

```jsonc
{ "mcpServers": { "cluckwork": {
    "type": "http",
    "url": "https://farm.example/mcp",
    "headers": { "Authorization": "Bearer ${CLUCKWORK_TOKEN}" } } } }
```

The bearer is an ordinary Cluckwork access token from `POST /api/v1/auth/login` with a farm
code — the same one the SPA carries. Farm, role and flock assignments all come from it. There
is no MCP-specific identity, no API key, and **no new server config key**.

### Discovery is role-filtered, not merely enforced

`AddAuthorizationFilters()` removes tools a caller's role cannot reach from `tools/list`
(verified: `AuthorizationFilterSetup` calls `items.RemoveAt`, and throws
`McpProtocolException(InvalidRequest)` on an unauthorized call). A plain Worker's list has no
`list_sales_orders` at all, rather than a 403 when they try.

### Recording the morning's collection

The model has a flock name and some numbers. It has no GUIDs.

```jsonc
→ cluckwork_list_flocks {}
← { "flocks": [ { "flockId": "6b1e…", "name": "House 2 Brown", "status": "Active",
                  "birdsAlive": 1180, "acceptsProductionToday": true } ],
    "scopeNote": "You are assigned to 2 of this farm's flocks." }

→ cluckwork_record_daily_entry {
     "idempotencyKey": "agent-run-4f2c-1",
     "flock": "House 2 Brown", "date": "2026-09-12",
     "totalEggs": 1042, "crackedEggs": 18, "dirtyEggs": 7, "mortalityCount": 2,
     "grades": [ { "grade": "Large", "quantity": 780 }, { "grade": "Medium", "quantity": 237 } ] }
← { "dailyEntryId": "9c4f…", "flockName": "House 2 Brown", "date": "2026-09-12",
    "status": "Draft", "created": true, "replayed": false }
```

`farmId` and `houseId` are derived from the flock aggregate, not asked for. `houseId` is a
phantom id until Phase 2's House model; requiring a model to supply a GUID for an entity that
does not exist is the leak this avoids.

Re-sending the identical call with the same `idempotencyKey` replays: `"replayed": true`, no
second row, no second audit event. Re-using that key with **different** arguments is refused
(`Idempotency.KeyReused`) rather than silently executed or silently ignored.

### Money never crosses as minor units

```jsonc
→ cluckwork_read_receivables { "owingOnly": true }
← { "customers": [ { "customerName": "Santos Sari-Sari",
                     "outstanding": { "amount": "28250.00", "currency": "PHP" } } ] }
```

Cluckwork stores `long` minor units plus a per-account `DefaultCurrencyMinorUnit` (2 for PHP,
0 for JPY, 3 for KWD). Handing a model `{"minorUnits": 2825000, "minorUnit": 2}` invites a
100× error in a number a farmer acts on. Rendered once, at the boundary.

### The other caller: a Cluckwork developer adding a tool

```csharp
[McpServerToolType]
public sealed class WaterTools(McpCallContext call, IWaterUsageRepository water)
{
    [McpServerTool(Name = "cluckwork_read_water_usage", ReadOnly = true)]
    [Authorize]
    [MirrorsRoute("ListWaterUsage")]
    [Description("Daily water consumption per flock, newest first.")]
    public Task<IReadOnlyList<WaterUsageSummary>> ReadAsync(
        Guid? flockId = null, CancellationToken ct = default) => …;
}
```

Four things are forced by guards, none by convention:

- Take `McpCallContext`, or the assembly walk fails the build. You then *cannot* write
  `if (!tenant.IsResolved)` — there is no unresolved state to check for.
- You may **not** inject `TenantContext`, `CurrentUserContext`, `FlockScope`, `AppDbContext`,
  `IServiceProvider`, `HttpContext` or `IHttpContextAccessor` into a tool. Walked.
- Declare `[MirrorsRoute]` or `[MirrorsNoRoute(reason)]`, or parity fails — and it fails again
  if your `[Authorize]` admits a role the mirrored route does not.
- Omit `ReadOnly = true` and you are a write tool, so you must take an `idempotencyKey`.
  Fail-closed: the SDK's `ReadOnly` defaults to `false`, so forgetting the annotation makes a
  read *stricter*, never looser.

## Shape

### `McpCallContext` — a parse at the boundary, not a check inside

A scoped record carrying `AccountId`, `UserId`, `Email`, `EffectiveRole`, `IsFlockRestricted`,
`AssignedFlockIds` and the `ClaimsPrincipal`. Its factory **throws** unless all of:

1. an `HttpContext` is present;
2. the injected `TenantContext` is **reference-equal** to
   `HttpContext.RequestServices.GetRequiredService<TenantContext>()`;
3. `TenantContext.IsResolved && CurrentUserContext.IsResolved && FlockScope.IsResolved`
   — note `IsResolved`, **never** `IsUnrestricted`, which defaults to `true`;
4. `UserId != Guid.Empty` — `ResolveSystemActor` sets `IsResolved = true` with an empty id and
   no roles, which `Roles.ResolveEffective` reads as Worker, which with zero assignment rows is
   `FlockScopeGuard`'s account-wide case. An MCP call is never a system actor.

There is no `IsResolved` property on it, no partially-populated instance, no sentinel. Per
**boundary-discipline**: the boundary parses, the inside trusts the type. Because DI resolves
constructor parameters before the tool instance exists, a tool that lists `McpCallContext`
*cannot* execute in an unpopulated scope — and that holds for a future `RecordWaterUsage` with
no audit row involved, which is the case `CurrentUserContext`'s own comment asks for.

**The parameter list is not the boundary; the dependency graph is.** A tool that takes only
`McpCallContext` can still inject a *helper* that opens a scope via `IServiceScopeFactory`, copy
`AccountId` into that scope's `TenantContext`, and resolve a repository — whose new `FlockScope`
is unresolved and therefore **unrestricted**, so a #388-narrowed Worker reads every flock. The
walk must therefore resolve a tool's transitive dependencies through their actual service
*registrations* — not just screen constructor parameters — and reject opaque factory/delegate
registrations in that graph unless explicitly reviewed. `IServiceScopeFactory` is blacklisted, but
blacklisting it on the tool alone does not close the indirect route.

**And that still does not make the branch unreachable — the design deliberately no longer claims
it does.** A helper registered as `sp => new Helper(() => sp.CreateScope())` exposes only a
delegate on its constructor; a static service locator adds no edge at all; detached background
work need not touch the dependency graph. Guard 7b bounds the hazard and names the residue.

**#787 backstops only half of that residue, and the half it misses is the reads.** Flipping
`FlockScopeGuard`'s fail-open branch to fail closed protects an unresolved-actor **write** — that
is why slice 6 is blocked on it. A read escape never reaches that guard at all:
`FlockRepository.ListAsync` goes through the EF global query filter, which reads `FlockScope`
directly, and an unresolved `FlockScope` is unrestricted. So a secondary-scope read of unassigned
flocks stays possible with #787 fully done. **That residual read risk is recorded, not closed** —
closing it needs either a fail-closed read boundary of its own or a decision to accept it, and
neither belongs in this design. (Round-3 finding 1: the round-2 fix claimed #787 made the residue
"safe rather than silent", which is true for writes and false for reads — a claim contradicted by
this design's own scoping of #787.) (Adversarial review round 1
finding 1, and round 2 finding 1: the first fix asserted a guarantee its own walk could not
establish, which reads as fixed and is therefore worse than the original gap.)

Check (2) is the one that earns its keep. It is the only mechanism among the three candidates
that detects a later `SessionMode` flip: under `Stateful` the injected `TenantContext` is *not*
the request's, so the reference comparison fails loudly on the first call. `ScopeRequests`
is deliberately **not** set by us and deliberately **not** guarded — the SDK forces it under
Stateless, so a guard on it would be a false alarm whose mutation reddens the test while the
product stays safe. What is pinned instead: `SessionMode == Stateless` and
`ConfigureSessionOptions == null` (the one hook that runs after the stateless branch).

### Tools are domain questions, not route mirrors

Eight tools over thirteen routes. `cluckwork_record_daily_entry` takes a flock name-or-id and
grade names-or-ids, derives `FarmId`/`HouseId` off the flock aggregate, then calls the
**unchanged** `RecordDailyEntryValidator` and `RecordDailyEntryHandler`.

**Name resolution means exactly one accessible match, and it needs a stated ambiguity contract
because duplicate flock names are legal** — `FlockRepository` says so outright, and its
`ThenBy(f => f.Id)` tie-break exists precisely because of it. Zero matches returns not-found; two
or more returns an ambiguity error carrying the candidate ids and the fields that distinguish
them, and the follow-up write must pass an id. Taking the first match would record production
against an arbitrary flock, and the handler cannot catch it — it receives a valid GUID. (Found by
adversarial review, finding 3; the original design made name-addressing its headline ergonomic
win without saying what happens when a name is not unique.) `cluckwork_list_orders`
fuses list+get and decides `SettlementScope` by evaluating `AuthPolicies.SalesAccess` through
the real `IAuthorizationService`, never by re-deriving the money tier.

What the tool layer deliberately does **not** contain: any validation rule, any authorization
decision of its own, any tenant or flock-scope predicate. Each already exists one layer down
and is already guarded; re-expressing one here would be a second source of truth.

A 1:1 tool-per-route surface would be thirteen pass-through methods with the same arguments
and a renamed result — the red flag exactly. The counter-argument (that the tool layer should
have the same depth budget as the HTTP endpoints) is real, but the HTTP endpoints serve a SPA
that already holds the GUIDs; a model holds names.

### Idempotency: exempt `/mcp`, extract #307, do not re-implement it

`/mcp` carries `HandlesOwnIdempotencyAttribute` and is exempted by **endpoint metadata**, not a
path literal — with a guard asserting the set of bearers is exactly `{"/mcp"}`. This is
blocking: without it MCP 400s on `initialize`.

#307's claim/execute/publish protocol is **extracted** into `IIdempotentUnit` keyed by an
explicit `IdempotencyScope(AccountId, OperationKey, CallerKey, RequestFingerprint)`. The
middleware becomes the first adapter with `OperationKey = Sha256($"{Method}:{Path}")` —
byte-identical to today, including the `/api/v1/me` user-scoping special case — and the write
tool the second, with `OperationKey = "mcp:tool:cluckwork_record_daily_entry"`. Keying on the
**tool name** is what dissolves the single-route collision for this tool and every future one.
`McpIdempotency.ScopeFor(accountId, toolName, arguments)` builds it, so no tool hand-rolls a
hash. One protocol, two adapters — writing a second copy of #307 is how #307 gets reopened.

**The port must carry an explicit success/failure outcome, and this is not optional.** Today
commit eligibility is an HTTP 2xx (`IdempotencyMiddleware.cs:289`), with failure rolling back and
*releasing* the claim so a later retry can execute. An MCP tool call has no HTTP status, and the
SDK converts ordinary exceptions into `CallToolResult.IsError` inside its own dispatch, so the
`/error` handler never sees them. An implementation that published on "the delegate returned
normally" would therefore **cache a domain failure**: record against a depleted flock, get
`DailyEntry.FlockNotActive`, cache it; a manager reactivates the flock; the identical retry
replays the cached failure forever. So the port takes an explicit outcome, publishes on success
only, serializes before commit, and uses the same `AppDbContext` for the mutation and the
publication. MCP-path tests for failure release, stolen-lease rollback and ambiguous-commit
recovery are required, because guard 24 only preserves the *HTTP* suites.

`idempotencyKey` is **required**, not derived. A derived key was tempting (a model's dominant
write failure is re-issuing a call it did not register) but carries a hazard: record N, a
manager edits to M in the SPA, the model re-sends N — replay, no write, for as long as the
row outlives the purge sweep. Over HTTP this cannot happen because the SPA mints a fresh key
per submit. Derived keys stay available as an explicit per-tool opt-in.

### Transport: stateless, and what that does to #271

`SessionMode.Stateless`, pinned by a guard. `PerSessionExecutionContext` is **not set** — it is
`[Obsolete]` with a DiagnosticId at 2.2.0 and `TreatWarningsAsErrors` makes it a build error;
it is only read in the stateful branch, so Stateless subsumes it.

**#271 is touched, and the honest statement is "registered but inert".** `WithHttpTransport`
registers `StatefulSessionManager`, `StreamableHttpHandler`, `SseHandler` as singletons and
`IdleTrackingBackgroundService` as a hosted service — **unconditionally**, not gated on session
mode. `IdleTrackingBackgroundService.StartAsync` returns early under Stateless, so no timer
runs and no session state exists. The blocker list is not extended; but AGENTS.md's instruction
to re-derive it by "walking every `AddSingleton`/`AddHostedService` under `src/`" is
structurally blind to a package registration. That wording was amended in #786 (merged as part of
PR #790); this record ships no `AGENTS.md` change of its own.

Stateless also buys #364: one tool call = one authenticated HTTP request, so
`CredentialEpochMiddleware` does its fresh DB read **per tool call**. Under `Stateful` +
`PerSessionExecutionContext` the whole session would run on the principal that opened it and a
revoked credential would keep working for the session's life — #364 reopened by a transport
option, with every one of #364's own tests still green.

No new process role (#347): `MapCluckworkMcp` sits downstream of `CliDispatcher.TryRunAsync`,
so a one-shot verb returns before reaching it, and nothing here fails a boot. No new required
config key, so `bootstrap.sh`, `docker-compose.sim.yml`, `verify-harness.sh` and the AppHost
are untouched (#370/#565) — pinned by a boot guard under the harness's Production-shaped config.

### An existing guard that starts applying on day one

`BodyReadingEndpointTests` walks every endpoint whose handler can reach the raw body
(`HttpContext`, `HttpRequest`, `Stream`, `PipeReader`) and demands either `IAcceptsMetadata.
RequestType`, or `ReadsRequestBodyAttribute`, or a reasoned entry in
`ReviewedAsNotReadingTheBody`. `MapMcp` maps `HandlePostRequestAsync(HttpContext)` with
`AcceptsMetadata` carrying no `RequestType` — so `/mcp` needs the marker or the suite goes red.
This is AGENTS.md's "a guard that walks everything starts applying the moment you commit"
pattern, and it is the kind of thing that costs an implementer a stop one increment from done.

**That attribute is NOT the body cap, and the two were originally conflated here.**
`ReadsRequestBodyAttribute` only classifies binding errors for the guard above;
`UseCluckworkRequestBodyLimit` (#309) reads `MaxRequestBodyBytesMetadata`, a different type set by
`WithMaxRequestBodyBytes(...)`. `/mcp` needs **both**, and without the second it has no cap at all.

### Bounded results, and a budget for the one route that carries everything

Neither was in the original design, and both matter more on `/mcp` than on a REST route.

**Every read tool states a bound.** Reusing a repository does not inherit the HTTP endpoint's
limit — the order cap lives in `SaleEndpoints`, not the repository, and `PaymentRepository`
materializes every matching customer group. An unbounded `read_receivables` on a large farm
serializes the whole book into a model's context window. Each reply therefore carries explicit
completeness and continuation fields: a silently truncated first page is worse than an error,
because it is an apparently complete wrong answer, and the model cannot tell.

**`/mcp` carries a rate-limit policy**, because #143's limiters are opt-in per endpoint and `/mcp`
is one route carrying every operation — so an unlimited `/mcp` is an unlimited everything. It must
key on the shared `IFixedWindowCounter` (#543/#544), never a process-local limiter: that is
precisely the #271 blocker shape the walk twice derived wrongly. An execution deadline belongs
here too.

(All of the above: adversarial review, finding 4.)

### Red-flag self-assessment

- **Shallow module** — the worst offender would be the 1:1 route mirror, hence eight domain
  questions over thirteen routes. `McpCallContext` is small but not shallow: its capability is
  *refusing to exist*, which makes a class of bug unrepresentable.
- **Information leakage** — no transport type on the author-facing or caller-facing surface: no
  HTTP status, no `IResult`, no JSON-RPC envelope, no minor units, no `HttpContext`.
  `Error.Code` crosses deliberately; it is domain vocabulary the SPA already branches on.
- **Temporal decomposition** — `IIdempotentUnit` is literally claim→execute→publish. It
  survives because it owns a *decision* (what "the same operation" means) rather than a stage:
  both callers hand it their own answer and it owns nothing else.
- **Pass-through** — the write tool changes the argument set (name→id, derived farm/house),
  adds idempotency at a different granularity and re-projects the result. `read_stock` is the
  thinnest and closest to one; it earns its place as the question a caller actually asks.

## Synthesis decision

See [`03-arena-synthesis.md`](03-arena-synthesis.md). Base candidate 2 on verified-SDK correctness and invariant encoding;
grafted candidate 1's interface depth, parity guard, system-actor check and `IsUnrestricted`
mutation; rejected candidate 1's derived-key default and all three candidates'
`PerSessionExecutionContext` line and `ScopeRequests` guard.

## Tradeoffs accepted

- **We accept refactoring #307's middleware in exchange for one claim protocol instead of
  two — but see the loopback alternative under *Alternatives considered*, which avoids the
  refactor entirely and should be priced before anyone starts.** This is the largest implementation risk here and the place a reviewer should look
  hardest. Mitigation is a rule, not a hope: `AtomicIdempotencyProtocolTests` and the other
  #307 suites must pass **unedited**, and an edit to one in the implementing PR is a stop-and-review.
- **We accept declaring tool RBAC twice — on the tool and on the route — in exchange for tools
  that call the Application layer directly like every endpoint does.** The parity guard is
  what makes it safe, and it goes red when the *route* changes and nobody touches `Mcp/`.
- **We accept no server-initiated notifications** in exchange for statelessness, which keeps
  #271 inert and buys the per-call #364 epoch read.
- **We accept a required idempotency key** rather than a derived one, trading ergonomics under
  the failure mode models actually have for correctness under concurrent SPA edits.
- **We accept English-only tool descriptions** while the SPA ships three locales. They are a
  model prompt, not user-facing UI; putting them in the i18n catalogues would drag #688's
  help-label pairing problem into a surface no locale reviewer reads.

## Alternatives considered

- **Keep the SDK's fresh scope and re-resolve tenant + actor from the `ClaimsPrincipal`.**
  Rejected: a second copy of `TenantResolutionMiddleware` + `FlockScopeResolutionMiddleware`,
  so #546's single-assignment rule, #388's assignment read and #364's ordering each acquire a
  second implementation no existing guard covers. Textbook information leakage.
- **`SessionMode = Stateless` and nothing else**, trusting the SDK's own scope wiring.
  Rejected: correct today, but it makes a third-party library's internal decision the whole
  safety property, and a `SessionMode` change is a one-word edit with a silent, mixed-mode
  failure. `McpCallContext` converts that to a throw on the first call.
- **A tool-per-endpoint surface generated from the OpenAPI document.** Tempting — mechanical,
  cannot drift, 107 routes for free. Rejected on interface depth: every generated tool is a
  pass-through whose arguments are the HTTP request body, so the write demands four GUIDs
  including the phantom `houseId`, and money arrives as minor units. It exposes Cluckwork's
  *transport* as the tool vocabulary, and has nowhere to put the identity parse.
- **stdio transport as a separate process.** Rejected for #770's own reasons (#347/#370/#565)
  plus one the issue does not give: a stdio process has no `HttpContext`, so tenant, actor,
  flock scope, credential epoch and must-change-password all need non-HTTP resolvers. That is
  the crux, permanently, for every tool.
- **Reuse `IdempotencyMiddleware` unchanged with an `Idempotency-Key` header on the MCP POST.**
  Rejected on evidence: it 400s `initialize`, and even past that the request hash covers a
  JSON-RPC envelope whose per-call `id` turns a genuine retry into a refusal.
- **Have the write tool call the existing HTTP endpoint over loopback**, with the caller's bearer,
  a stable command JSON body and a tool-namespaced `Idempotency-Key` header. **This avoids
  extracting #307 entirely** — only the outer `/mcp` exemption is needed, and the claim protocol,
  its `/me` special case, its #269 single-attempt execution and its #345 re-entry skip all keep
  running exactly as they do today, untouched and already guarded. The cost is an extra HTTP round
  trip per write and a second place where a bearer is presented. **This deserved to be the primary
  recommendation and was not considered in the original design** — the "Alternatives considered"
  section rejected hashing the *envelope*, which is a different idea. Raised by adversarial review
  (finding 2). Given that the extraction is this design's self-declared largest risk, whoever picks
  up slice 1 of the epic should price this shape first and only extract #307 if it loses on
  evidence. **Two gaps to price with it, neither fatal** (round-2 finding 4): a tool-prefixed
  `Idempotency-Key` is *not* a server-enforced namespace — the HTTP path accepts any non-blank key
  and hashes it, so an ordinary HTTP caller can send the identical header and collide; and the
  existing middleware's replay path returns the cached status and body with **no replay
  discriminator**, so a loopback tool cannot honestly report `"replayed": true`. Either keep
  server-enforced separation, or document the weaker prefix contract and drop that field from the
  reply — but do not advertise a contract the mechanism cannot keep.
- **Flip `FlockScopeGuard`'s fail-open branch to fail closed.** Probably correct eventually,
  but it is an authorization default change affecting both seeders, four one-shot verbs and
  every non-HTTP caller; the guard's own comment says it "deserves its own issue rather than a
  drive-by". This design **narrows** the routes to that branch from MCP — it does not make it
  unreachable (see the residue above) — and leaves its fate to that issue.

## Open questions and risks

- **Token lifetime, and it may be a blocker.** MCP clients expect OAuth discovery and a
  refreshable token. Cluckwork's refresh is an HttpOnly cookie and the access token is short-
  lived, so a real client may drop mid-session with no way to re-auth. Do we (a) ship phase 1
  for clients that can inject a fresh bearer, (b) add the SDK's `McpAuthenticationHandler` and
  protected-resource metadata, or (c) issue a longer-lived MCP-scoped credential? This is a
  product decision and it gates real-world usability, not correctness.
- ~~Should the fail-open branch of `FlockScopeGuard` get its own issue?~~ **Resolved: filed as
  #787.** The guard's comment has been asking since #500, and
  `RecordFeedUsage`/`RecordWaterUsage` still reach it from any future non-HTTP caller. This
  design narrows the routes to that branch from MCP rather than fixing it, and does not close the
  secondary-scope read escape at all.
- Is an `Mcp:Enabled` kill switch worth one config key? Left out to keep #370/#565 at zero
  files, on the reasoning that the route is authenticated — but an owner wanting MCP off on a
  deployment currently has no lever but a reverse proxy.
- `Idempotency.InProgress` has no MCP equivalent of `Retry-After`. Should the tool reply carry
  a suggested backoff, or just the code?
- Should `record_daily_entry` also be able to **submit** the entry? Recording leaves a draft
  whose eggs are not in stock, so a model that records and stops has done half the job the user
  asked. Submitting is a second transition with its own audit and lot generation, and the
  owner locked the surface to one write — a scope question, not a design one.
- Risk: `AddAuthorizationFilters()` filtering `tools/list` is SDK behaviour. The parity guard
  proves our policies are right; only one integration test proves the *list filtering*. If a
  future SDK version changes it, that test is all that stands between a ReadOnly user and a
  visible write tool.
- Risk: extracting `IIdempotentUnit` is the one part I would not merge without an adversarial
  pass on the diff. The #269 `SingleAttemptExecution` interaction and the steal-loss rollback
  are both easy to preserve *almost* correctly.

## Next implementation step

Write `McpCallContext` and its four throwing cases, plus the `/mcp` idempotency exemption —
with no tools and no `MapMcp` yet. The exemption is what makes any MCP request possible at all,
and the four throws are the design's load-bearing claim; both are testable before a single tool
exists.
