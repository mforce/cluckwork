# Let OAuth clients register themselves, and sweep dead OAuth rows (#797)

> **Rule** — the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md);
> this file is the relocated rationale. The design it implements is in
> [`docs/plans/788-mcp-oauth/`](../plans/788-mcp-oauth/00-README.md).

**Status:** accepted
**Date:** 2026-10-08

## What happened

No incident. This is a forward-looking choice. An assistant that has never seen this
server needs a client id before it can send a user to approve it. #788 chose dynamic
client registration (RFC 7591), which means an endpoint anyone on the internet can call.
#795 shipped OpenIddict with no registration endpoint and no cleanup of its tables.

## The rule

`POST /api/v1/oauth/register` is anonymous. It registers a public client allowed the
authorization-code grant and nothing else; PKCE with S256 is already required server-wide
(#795). Redirect URIs must be `https`, or `http` on `127.0.0.1` or `[::1]` with any port,
with no fragment and no user info. The client's name is untrusted display text. Discovery
advertises the endpoint as `registration_endpoint`. The `oauth-register` policy limits it
per client IP on the shared `IFixedWindowCounter`. `OAuthPurgeSweep` runs under
`DurableJobWorker`'s leader gate and removes dead tokens, dead authorizations and
applications nobody approved.

## Why leaving registration open is safe

A registered client can do nothing on its own. It can send a user to the consent screen
(#798), and nothing is issued until that user approves with their password. The risk is
rows, not access, so the controls are about rows: a per-IP budget of 10 registrations an
hour, and deletion of an application nobody approves within a day. The endpoint carries
this reasoning in a comment, because "anyone can POST here" reads as alarming without it.

## What the validator accepts

- **Grants.** `authorization_code` only. `refresh_token` is accepted in `grant_types` and
  dropped, because access tokens last until revoked (#788) and MCP clients routinely ask
  for it; RFC 7591 §3.2.1 lets a server register less than it was asked for, and the
  response says what was registered. Any other grant, a response type other than `code`
  and a `token_endpoint_auth_method` other than `none` are refused with
  `invalid_client_metadata`.
- **Redirect URIs.** One to ten, each at most 2048 characters. `localhost` is refused
  along with every other host name on plain `http`, and so are custom schemes. See the
  open risk below.
- **Name.** Normalised to NFC; every control, format, private-use or unassigned code
  point becomes a space, which covers the bidi overrides and isolates that could reorder
  the consent screen's text; runs of spaces collapse; the result is capped at 100 UTF-16
  units, cut on a grapheme boundary so a stack of combining marks cannot exceed it. An
  empty result is stored as no name. The consent screen (#798) must still say that the
  app chose its own name.
- **Everything else.** `logo_uri`, `client_uri`, `contacts` and the rest are not bound,
  so nothing else a client says about itself is stored or shown.

## Approval, and what "unapproved" means

OpenIddict records approval as an authorization row. An application with no
authorization and no token is one nobody has approved, or one whose approval was revoked
and pruned. Either way it grants nothing, so the sweep deletes it once it is a day old.
A client whose id was deleted gets `invalid_client` and registers again. OpenIddict keeps
no creation time for an application, so this slice adds `CreatedAtUtc`, stamped by
Postgres, as a shadow property.

## The sweep

One sweep, under the leader gate (#271), like `RefreshTokenPurgeSweep`. OpenIddict's
Quartz.NET integration was not added, because it would be a second scheduler outside that
gate. The order is fixed by the foreign keys, which have no cascade:

1. `IOpenIddictTokenManager.PruneAsync` removes tokens created before the threshold that
   are redeemed, revoked, expired, or under an authorization that is not valid.
2. `IOpenIddictAuthorizationManager.PruneAsync` removes authorizations created before the
   threshold that are not valid or are ad hoc, and have no tokens left.
3. One `DELETE` removes applications created before the window with no authorization and
   no token.

The threshold is OpenIddict's default, 14 days, so a replayed code or a revoked token
still reads as such for a while instead of as unknown. A live connection holds a valid
access token with no expiry, under a valid authorization, so neither is ever eligible
and its application keeps both children. The sweep resolves `IOAuthPurge`, registered
only with the OAuth server, so in Production it does nothing until #798.

## Registration mechanisms, checked against the MCP specification

This is a reference point, not a protocol-version commitment; choosing the MCP version is
the MCP milestone's decision (#789, #806). Checked 2026-10-08 against the authorization
specification revision **2026-07-28**, the current one:

- Client ID Metadata Documents are what authorization servers and clients **SHOULD**
  support.
- Dynamic Client Registration is **MAY**, and that revision marks it **deprecated**,
  kept for backwards compatibility with servers that lack metadata documents. The
  2025-11-25 revision, which the #797 correction cites, called it optional but did not
  deprecate it.
- Clients choose pre-registration first, then metadata documents when the server
  advertises `client_id_metadata_document_supported`, then registration when it
  advertises `registration_endpoint`.

**This slice supports DCR only. Client ID Metadata Documents are deferred.** Discovery
does not advertise `client_id_metadata_document_supported`, so a client that implements
only metadata documents cannot connect. If the MCP milestone targets a revision that
requires them, adding them is OAuth-side follow-up work: fetching a client's document over
HTTPS with SSRF protections, caching, and validating redirect URIs against it.

## Open risk: the redirect rule refuses common local clients

Native and CLI clients commonly register `http://localhost:<port>/callback`; the
specification's own metadata-document example lists one. This rule refuses `localhost`,
as RFC 8252 §8.3 advises, and refuses custom URI schemes. A client that registers only
such URIs cannot connect until it changes or this rule widens. Which exact URIs each
expected client sends was not verified here.

## A farm switch for app connections

Not built. Registration happens before anyone signs in, so it has no farm; a switch could
only refuse consent, which is #798's screen. Every connection already needs a user who
approves it with their password, and an Owner can see and revoke any connection (#799).
If a switch is wanted, it belongs at consent and in account data, as #788 says, not here.

## How it is enforced

- `OAuthClientRegistrationTests` registers a client over HTTP and completes the flow with
  it, checks the discovery entry, refuses each wider redirect URI and grant, pins the name
  sanitiser against literal inputs and outputs, proves a session bearer is ignored, and
  limits a client IP behind a trusted proxy on the shared counter while another IP
  registers.
- `OAuthPurgeTests` ages rows by rewriting their creation time, then proves dead rows past
  the threshold go, dead rows inside it stay, a live connection survives, an unapproved
  application goes after its window and not before, and only the leader runs the sweep.
- `OAuthServerProductionTests` asserts the registration endpoint answers 404 in
  Production and `IOAuthPurge` does not resolve.
- `tools/oauth/mutation-check.sh` applies one mutation per claim and requires the named
  test to fail.
