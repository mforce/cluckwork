# OAuth tokens run the session chain, on opted-in endpoints only (#796)

> **Rule** — the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md);
> this file is the rationale. The design it implements is in
> [`docs/plans/788-mcp-oauth/`](../plans/788-mcp-oauth/02-design.md).

**Status:** accepted
**Date:** 2026-10-08

## What happened

No incident. #795 issued OAuth reference tokens and kept them off business endpoints with
two walls: those endpoints authenticated session JWTs only, and the token carried `sub`
alone. #796 lifts the second wall on purpose so an endpoint can accept an OAuth token,
and puts every per-request check in place first. No business endpoint accepts one yet;
`/mcp` (#806) is the first planned caller.

## The rule

**One principal shape.** The authorize endpoint copies the session principal's `sub`,
`email`, `account_id`, `credential_epoch`, `role` and `must_change_password` claims into the
token. OpenIddict validation authenticates the token during `UseAuthentication`, so
`TenantResolutionMiddleware`, `FlockScopeResolutionMiddleware`, `CredentialEpochMiddleware`
(disabled user, suspended farm, epoch) and `MustChangePasswordMiddleware` run unchanged.
Authenticating later, as an authorization-time scheme, would skip all four.

**One scheme per endpoint.** The default scheme is a policy scheme. An endpoint marked
through `AcceptOAuthTokens(scopes)` authenticates with OpenIddict validation and nothing
else; every other endpoint authenticates with the session JWT scheme and nothing else. The
choice reads endpoint metadata, never the token's shape, so neither handler ever sees the
other's token. The marker type is private to `OAuthEndpoints`, so the only way to accept
OAuth tokens is the extension, which also attaches the rate limit and the scope gate.

**Scopes subtract.** `AcceptOAuthTokens` adds an authorization policy requiring one of the
named scopes. The endpoint's own role policy still applies, so effective permission is
role ∩ scope by composition. No product scope is registered yet; #798 registers the two
from #788 and #806 maps tools to them.

**Role freshness is the epoch (option 1).** A role change bumps `CredentialEpoch`, so the
token is refused on its next request and the assistant must reconnect. Option 2 (reading
roles live) would need a second freshness mechanism and a new Access contract.
Must-change-password is enforced at issuance: the authorize endpoint sits behind
`MustChangePasswordMiddleware`, and every path that changes the flag afterwards bumps the
epoch.

**Disconnect revokes the authorization.** Authorize creates an ad-hoc authorization, and
every code and token carries its id. Validation calls `EnableAuthorizationEntryValidation()`,
so a revoked authorization refuses its access token on the next request. OpenIddict skips
that check for a token that names no authorization, so an inline handler refuses such a
token. A code from a revoked authorization no longer redeems (OpenIddict's server check),
and there is no refresh grant. `TryRevokeAsync` returns false on a concurrency failure, and
OpenIddict's tables carry no `AccountId`, so the #799 Disconnect action must check the
result and that the authorization's subject is the caller (or an Owner of that farm).

**Header-only tokens.** Validation ignores tokens in a query string (they reach request
logs) or a form body (they would dodge the per-token rate-limit key).

**Rate limits** use `DistributedFixedWindowPolicy` on the shared `IFixedWindowCounter`
(#543/#544), configurable under `RateLimiting:*`:

| Policy | Applied to | Key | Default |
|---|---|---|---|
| `oauth-token` | `POST /api/v1/oauth/token` | client IP | 20 / 60 s |
| `oauth-authorize` | `GET /api/v1/oauth/authorize` | client IP | 20 / 60 s |
| `oauth-api` | every `AcceptOAuthTokens` endpoint | SHA-256 of the bearer | 120 / 60 s |

`UseRateLimiter` runs before authentication, so `oauth-api` keys on the raw bearer: the
exact slice OpenIddict extracts after `Bearer `, hashed so the store never holds a usable
token. With no refresh grant, a token is one connection. The token endpoint is mapped
through OpenIddict's passthrough so it can carry the policy and a body cap; it also
ignores any ambient session bearer, which would otherwise resolve a tenant and demand an
`Idempotency-Key`.

## What this does NOT cover

- **Junk bearers.** Each invalid token gets its own `oauth-api` bucket and costs one token
  lookup. A per-IP ceiling beside the per-token key is not built.
- **Response shape for OAuth callers.** The chain's 401s carry no `WWW-Authenticate:
  Bearer error="invalid_token"`, and a scope denial uses the role-denial body. #806 decides
  what an MCP client needs.
- **Audit attribution** of the acting client (#788 plans first-class columns).

## How it is enforced

`OAuthFailClosedTests` maps two test-only probes into the real endpoint table and drives
each check with a token from the real flow. `tools/oauth/mutation-check.sh` proves each
test can fail; the mutants and their declared failures are listed in the script.
