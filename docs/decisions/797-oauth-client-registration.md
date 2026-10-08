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
(#795). Redirect URIs must be `https`, or `http` on `localhost`, `127.0.0.1` or `[::1]`,
with no fragment and no user info. A client whose redirect URIs are all `http` loopback is
native, and may then authorize on any port. The client's name is untrusted display text. Discovery
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
- **Redirect URIs.** One to ten, each at most 2048 characters. Plain `http` is allowed
  only on `localhost`, `127.0.0.1` and `[::1]`. Custom schemes such as `cursor://` are
  refused. OpenIddict's own validator still runs on what is stored, and a URI it refuses,
  such as one carrying a reserved `iss` query parameter, comes back as
  `invalid_redirect_uri` rather than a 500.
- **Ports on loopback.** RFC 8252 §7.3 lets a native app listen on whatever port it gets.
  OpenIddict allows that only for an application of type `native` whose stored loopback
  URI names no port. So when every redirect URI is `http` loopback, the client is
  registered as native and its URIs are stored without the port. Scheme, host, path and
  query must still match. A client that also lists an `https` URI stays a web client and
  every URI matches exactly, port included.
- **The response.** `redirect_uris` in the 201 lists the strings OpenIddict stored and
  compares ordinally, so a client can send them back unchanged.
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
A client whose id was deleted gets `invalid_client` and registers again.

OpenIddict keeps no creation time for an application, so this slice adds `CreatedAtUtc`
as a shadow property and stamps it the way #819 stamps every created-only record. The
migration attaches #819's existing `StampCreatedBusinessRecord` function as
`TR_OpenIddictApplications_BusinessRecordTimestamps`. The trigger overwrites any supplied
value on insert and keeps the old one on update, so OpenIddict's own updates, raw SQL and
bulk updates cannot move it. The EF property uses `BusinessRecordModel.ConfigureCreatedTimestamp`,
so EF never sends a value and reads back the database's. Rows that existed before the
migration get #819's unknown sentinel, `1970-01-01`, because nothing records when they
registered. An unapproved one expires at the next sweep, and only non-Production databases
can hold any. The application is still a framework type that cannot implement
`ICreatedRecord`, so #819's upgrade guard now compares every mapped `CreatedAtUtc` column
with every stamping trigger, rather than counting `ICreatedRecord` types.

The window is measured on the database's clock: the delete compares `CreatedAtUtc` with
`now()` minus the window, because the database stamped it. Token and authorization pruning
stays on the API's clock, because OpenIddict stamps those rows from the API.

## The sweep

One sweep, under the leader gate (#271), like `RefreshTokenPurgeSweep`. OpenIddict's
Quartz.NET integration was not added, because it would be a second scheduler outside that
gate. The order is fixed by the foreign keys, which have no cascade:

1. `IOpenIddictTokenManager.PruneAsync` removes tokens created before the threshold that
   are redeemed, revoked, expired, or under an authorization that is not valid.
2. `IOpenIddictAuthorizationManager.PruneAsync` removes authorizations created before the
   threshold that are not valid or are ad hoc, and have no tokens left.
3. One `DELETE` removes applications older than the window, by the database's clock, with
   no authorization and no token.

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

## Why `localhost`, and why not custom schemes

RFC 8252 §8.3 prefers the literal loopback addresses over `localhost`, because a name can
resolve elsewhere. It discourages `localhost` and does not forbid it. The first version of
this slice refused it. Round-one review found that clients #797 expects register it:
Claude Code documents `http://localhost:PORT/callback`, the MCP Inspector's web UI uses
`localhost`, and Cursor's support forum confirms a `localhost` callback. The owner chose
to accept `localhost` alongside the two literal addresses, with the native port rule
above.

Custom URI schemes stay refused. Cursor's legacy `cursor://` callback is one, and so are
the private-use schemes the OAuth 2.1 draft recognises but ranks below loopback and
claimed `https`. Admitting one would need a deliberate per-scheme exception, not a
relaxed predicate.

## DCR in today's clients

Despite the deprecation in 2026-07-28, current first-party documentation for Claude Code,
Claude's hosted connectors, VS Code and ChatGPT still describes DCR support. That is why
metadata documents stay deferred.

## A farm switch for app connections

Not built. Registration happens before anyone signs in, so it has no farm; a switch could
only refuse consent, which is #798's screen. Every connection already needs a user who
approves it with their password, and an Owner can see and revoke any connection (#799).
If a switch is wanted, it belongs at consent and in account data, as #788 says, not here.

## How it is enforced

- `OAuthClientRegistrationTests` registers a client over HTTP and completes the flow with
  it, authorizes a loopback client on a port it did not register, refuses a change of
  path, host, query or scheme, completes the flow with each redirect URI the response
  returned, turns a reserved `iss` parameter into `invalid_redirect_uri`, checks the
  discovery entry, refuses each wider redirect URI and grant, pins the name
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
