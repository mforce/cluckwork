# The MCP endpoint: OAuth only, bound to /mcp, stateless (#806)

> The design is [`docs/plans/770-mcp-server/`](../plans/770-mcp-server/01-design.md); its
> guard table is [`02-guards.md`](../plans/770-mcp-server/02-guards.md). This file records
> what #806 decided while mapping the endpoint.

**Status:** accepted
**Date:** 2026-10-10

## What happened

No incident. Milestone 10 (#788) built the authorization server and left four MCP-shaped
questions to this slice: which tokens `/mcp` accepts, how a client discovers where to
authenticate, how scopes reach individual tools, and what the per-token rate limit should
be. The decisions on #789 (2026-10-10) settled the protocol version, audience binding and
the rate figure. The rest is recorded here.

## The rule

**`/mcp` exists only beside the authorization server.** `Program.cs` maps it inside
`if (identity.OAuthServer)`. Without an issuer the OAuth scheme is not registered, and
`/mcp` accepts no other token. The SDK is `ModelContextProtocol.AspNetCore` 2.2.0, and one
endpoint serves MCP 2026-07-28 and 2025-11-25 clients.

**Stateless, pinned.** `SessionMode = Stateless` makes the SDK run each tool in the HTTP
request's own services, so `McpCallContext` (#805) sees the request's tenant, actor and flock
scope, and each tool call is one authenticated request that `CredentialEpochMiddleware`
re-checks (#364). The SDK's idle-session sweep is registered but never starts. Stateless
serves no GET stream, so `GET` and `HEAD /mcp` answer 405, as the MCP spec asks; otherwise
the SPA fallback would answer them with the app's HTML.

**Tokens are bound to `/mcp` (RFC 8707).** The resource is `<OAuth:Issuer>/mcp`, never the
request's Host. The authorization server registers it as its only resource, so an authorize
or token request naming another resource gets `invalid_target`. Consent stamps the named
resource on the code, and so on the token; a client that names none gets `/mcp`, the only
resource there is. Validation adds `/mcp` as the required audience, so a token minted before
this change, which carries none, is refused.

**Discovery is the SDK's `McpAuthenticationHandler`.** The bearer selector's OAuth branch is
now the MCP scheme. That scheme authenticates through OpenIddict validation, answers a
challenge with `WWW-Authenticate: Bearer resource_metadata="<issuer>/.well-known/oauth-protected-resource/mcp"`,
and serves that document (RFC 9728), naming the issuer as the authorization server and the
two scopes. Both URLs come from `OAuth:Issuer`. The handler only answers a metadata request
whose Host matches the issuer's.

**Scopes reach tools as policies.** `OAuthScopeRequirement` replaces the inline assertion in
`AcceptOAuthTokens` and backs two named policies, `AuthPolicies.FarmReadScope` and
`AuthPolicies.DailyEntriesWriteScope`. Each tool carries a role `[Authorize]` and a scope
`[Authorize]`; the SDK's `AddAuthorizationFilters()` ANDs them, so `tools/list` shows a tool
only for role ∩ scope and `tools/call` refuses the rest. `McpToolSurfaceTests` fails a tool
missing either gate, and one carrying `[AllowAnonymous]`, because the SDK runs such a tool
for anyone. `AcceptOAuthTokens(ReadFarm, WriteDailyEntries)` stays on the mapping as the
outer gate.

**A missing scope says which one.** When only scope requirements failed,
`ForbiddenProblemResultHandler` answers 403 with
`WWW-Authenticate: Bearer error="insufficient_scope", scope="...", resource_metadata="..."`
(RFC 6750 §3.1). When the role failed too, a new token would not help, and the role body
answers as before.

**Idempotency is exempt by endpoint metadata.** Every MCP message is a POST and no MCP
client sends an `Idempotency-Key`, so `IdempotencyMiddleware` skips an endpoint carrying
`HandlesOwnIdempotencyAttribute`. Only `/mcp` carries it, pinned by
`McpEndpointTests.IdempotencyExemption_IsCarriedByTheMcpPostAlone`. The write tool keys its
own claim (#809).

**Budget: 300 messages per minute per token.** `oauth-api` rose from 120. Measured with the
SDK's own client against this endpoint, a session spends 3 POSTs to its first tool result on
2026-07-28 (`server/discover`, `tools/list`, `tools/call`) and 4 on 2025-11-25 (`initialize`
and its notification replace discovery); each later call is one POST. No account-wide
ceiling: each token costs a step-up password entry (#798), which the password limiter
already bounds. A 429 on `oauth-api` is a JSON-RPC error with a null id, because the limiter
runs before the body is read, and its text names the connection, not an address.

**The body cap is 32 KiB** (`WithMaxRequestBodyBytes`), beside `ReadsRequestBodyAttribute`,
which only classifies binding errors.

**The adapter tier row grants nothing.** `Cluckwork.Api.Mcp`'s privilege is now
`ContractOnly`: CW1004 holds tool classes to module contracts as it holds endpoints. The old
`DirectRepository` name and its reason ("inject repositories by design") were wrong.

## What this does NOT cover

- **401s from the shared request checks carry no challenge header.** A disabled user, a
  suspended farm, a superseded credential or the farm switch writes its 401 directly
  (#796). An MCP client then probes `/.well-known/oauth-protected-resource/mcp` itself, as
  the MCP authorization spec requires, and that document is served.
- **A tool-level scope denial is a JSON-RPC error, not an HTTP 403.** The SDK refuses a
  `tools/call` for a tool the caller cannot see with `InvalidRequest` inside a 200. Only the
  endpoint gate answers `insufficient_scope`. A client that follows `tools/list` never calls
  a hidden tool.
- **Route parity for tools** (guard rows 13 to 15) arrives with the first tool (#807).

## How it is enforced

`tests/Cluckwork.Api.IntegrationTests/Mcp/`: `McpEndpointTests` reads the endpoint table and
services (rows 8 to 12, 17, 18, 29, 30), and `McpAuthorizationTests` drives the real OAuth
flow and the SDK's client against `/mcp` with probe tools that exist only in the test host.
`tools/oauth/mutation-check.sh` proves each test can fail; the #806 mutants are listed there.
