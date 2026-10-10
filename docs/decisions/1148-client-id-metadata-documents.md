# Accept client ID metadata documents beside registration (#1148)

> **Rule** — the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md);
> this file is the relocated rationale.

**Status:** accepted
**Date:** 2026-10-10

## What happened

No incident. This is a forward-looking choice. The MCP authorization specification,
revision 2026-07-28, says authorization servers and clients **SHOULD** support client ID
metadata documents and marks dynamic client registration (DCR) as deprecated. #797
shipped DCR only and deferred metadata documents. A client that supports only metadata
documents could not connect.

Sources read: draft-ietf-oauth-client-id-metadata-document-02 (6 July 2026), and the MCP
authorization specification 2026-07-28 with its client registration and security
considerations pages. MCP still cites draft -00; nothing below depends on a rule that
only one of the two revisions states.

## The rule

A `client_id` that starts with `https://` names the client's metadata document. Before
OpenIddict looks the client up in an authorization request, `ClientMetadataDocuments`
makes sure an ordinary OpenIddict application row exists for that URL and is no older
than the document's cache lifetime. If the row is missing or expired, it fetches the
document through `ClientMetadataFetcher`, validates it with the same rules a DCR request
meets (`ClientMetadata.ToDescriptor`), and stores or refreshes the row. Any other
`client_id` goes to the registered client, unchanged. Discovery advertises
`client_id_metadata_document_supported: true`. Break the fetcher's address check and
any anonymous visitor can make the server fetch from its own network.

## Where the seam is

The seam is an OpenIddict server event handler on `ValidateAuthorizationRequestContext`,
ordered 500 after `ValidateClientIdParameter` and so before `ValidateAuthentication`,
where OpenIddict's `ValidateClientId` calls `FindByClientIdAsync`. OpenIddict 7.7.1 has
no metadata document support of its own.

The alternative was a custom `IOpenIddictApplicationStore` whose `FindByClientIdAsync`
resolves a URL by fetching it. It lost for three reasons:

- Every caller of `FindByClientIdAsync` would fetch, including the token endpoint. Each
  of those handlers assumes a cheap lookup.
- Authorizations and tokens hold foreign keys to the application row, so a row is
  needed anyway. The store would need a second representation (an in-memory entity
  with a deterministic id) and a rule for when to write the anchor row.
- A per-process document cache with single-flight is more code than the row it would
  replace.

Only the authorization request fetches. The token endpoint redeems a code issued
against the row, which the draft allows; it requires no refetch there. Token validation
on API requests never looks the application up.

## How a metadata client is stored

It is one ordinary OpenIddict application row per document URL. `ClientId` holds the
URL. The display name, redirect URIs, client type, application type and permissions are
exactly what a DCR client with the same metadata would hold, because both come from
`ToDescriptor`. The one extra fact, when the copy expires, is stored in OpenIddict's
`Properties` column as `cimd_expires_at` (Unix seconds), so no migration was needed.

- **Cache and replicas (#271).** The row is the cache. It lives in Postgres, so every
  replica reads the same copy, and one fetch serves the whole fleet for a lifetime.
  OpenIddict's own application cache is scoped to the request.
- **Lifetime.** `Cache-Control: max-age` is followed between a floor of 5 minutes and a
  ceiling of 24 hours, with 1 hour when absent. `no-store` and `no-cache` get the floor,
  because the row must exist for the foreign keys and the floor bounds the fetch rate.
- **Errors are never stored** (draft §5.2). A failed fetch or an invalid document
  creates no row and changes no row. An expired row whose refresh fails refuses the
  request and stays as it was.
- **Isolation.** The refresh runs in a child DI scope with its own `AppDbContext`. A
  failed insert then cannot stay tracked in the request's context, where the consent
  transaction would save it again.
- **Races.** Two requests may both find no row. The loser's insert hits the unique index
  on `ClientId`, or its update hits OpenIddict's concurrency token, and it accepts the
  winner's copy. Both copies passed the same validation.
- **Purge (#797).** Unchanged. A metadata client nobody approved is deleted after
  `OAuthPurgeSweep.UnapprovedWindow` like a registration, and the next request fetches it
  again. An approved row keeps its authorization, so it stays.
- **Downstream.** Connected apps (#799), audit provenance (#800), the farm switch (#1146)
  and the fail-closed checks and rate limits (#796) read the row and the token claims,
  so they treat a metadata client exactly like a registered one. The token carries the
  URL as `client_id`.

Unlike DCR, an anonymous authorization request can create a row. Rows are bounded by
the fetch budget below, each needs a URL serving a valid document that names itself,
and the purge removes unapproved rows after a day.

## The fetcher

`ClientMetadataFetcher` is the server-side request forgery boundary. One
`SocketsHttpHandler`, built once:

- **Resolve once, check every address, connect to the checked one.** The handler's
  `ConnectCallback` resolves the host, refuses the host if any returned address is
  special-use, and dials the first vetted address. Nothing resolves the name a second
  time, so DNS rebinding has nothing to change. TLS runs over that connection with the
  original host name, so the certificate is still checked against it.
- **Special-use addresses.** IPv4: the RFC 6890 and IANA special-purpose blocks, among
  them private, CGNAT, loopback, link-local (cloud metadata), documentation, benchmarking,
  multicast and reserved. IPv6 is an allow-list: only global unicast `2000::/3`, minus its
  special blocks (`2001::/23`, `2001:db8::/32`, `2002::/16`, `3fff::/20`). So loopback,
  unspecified, link-local, unique-local, multicast, NAT64 and IPv4-mapped never qualify.
  IPv4-mapped addresses are refused explicitly before any range check, because .NET 10's
  `IPNetwork.Contains` reports `::ffff:10.0.0.1` as inside `2000::/3`. A test found that.
- **No redirect** (draft §5): a 3xx is an error. **Only 200** is accepted.
- **`application/json` only**, parameters such as a charset allowed. The draft also
  permits more specific JSON types; this server does not, on the brief's instruction.
- **Size.** 5 KB (the draft's recommendation). A larger `Content-Length` is refused from
  the headers; otherwise the body is read into a 5 KB + 1 buffer and refused when it
  fills, so a chunked body cannot exceed it either.
- **Time.** One 5-second deadline covers resolving, connecting, the headers and the
  whole body, plus a 3-second connect timeout. `HttpClient.Timeout` alone would not cover
  a body read after `ResponseHeadersRead`.
- **No ambient state.** No cookies, no credentials, no proxy, no decompression. The
  request carries only `Host`, `Accept: application/json` and the fixed User-Agent
  `Cluckwork-OAuth-ClientMetadata/1.0`.
- **No proxy, on purpose.** A proxy would resolve the name itself, after the address
  check. A deployment whose egress needs a proxy turns metadata documents off (below).

The test seam is the internal `FetchTransport` record (resolve, dial, certificate
check). Production uses `FetchTransport.System`: the system resolver, a plain socket and
the default certificate validation. No configuration reaches it.

## The fetch budget

A fetch is spent only on a missing or expired row; a fresh row costs nothing. On the
shared `IFixedWindowCounter` (#544): at most 10 fetches of one URL per 5 minutes, then at
most 60 fetches in total per minute. The per-URL key is a SHA-256 hash, so no
client-chosen text reaches the shared store. An exhausted budget refuses with
`temporarily_unavailable` and serves no stale copy. The per-IP `oauth-authorize` limit
still sits in front.

## Which URLs qualify

The draft requires `https`, a path, no user info, no fragment and no dot segments, and
compares client ids as plain strings. This server is stricter so that every URL has one
spelling: ASCII without `%`, a DNS host (no IP literal), a path other than `/`, no query,
at most 100 characters, and `new Uri(id).AbsoluteUri == id`, which refuses an upper-case
host, dot segments and an explicit `:443` in one clause.

The 100-character cap is load-bearing. OpenIddict stores `ClientId` as `varchar(100)`,
and `AuditEvent.MaxConnectedAppClientIdLength` is 100, so a longer URL would fail at
insert or at the first audited write. Known metadata document URLs are far shorter.
Widening both columns is a migration nobody needs yet.

## What the document may say

Every field is untrusted. The document must be a JSON object with no duplicate keys
whose `client_id` is a string equal, ordinally, to the URL (draft §4). It must not carry
`client_secret` or `client_secret_expires_at` (draft §4.1). The rest goes through
`ToDescriptor`, the #797 rules: a public client, `authorization_code` only
(`refresh_token` accepted and dropped), `response_types` of `code`,
`token_endpoint_auth_method` of `none` (so `private_key_jwt` is refused), one to ten
redirect URIs that are `https` or `http` loopback, a loopback-only client stored as
native with port-less URIs, and the name sanitised and capped at 100. OpenIddict's own
validation still runs when the row is created or updated, so a reserved `iss` parameter
in a redirect URI is refused here as it is for DCR. Nothing else in the document is
stored, including `logo_uri` and `client_uri`.

## The consent screen

The consent payload and the anonymous sign-in preview gain `verifiedDomain`, the
authority of the client id (host, plus a port if it has one), derived from the id and
never stored. It is null for a registered client. The screen shows **Verified domain**
with it in place of **Unverified app**, and Details says what was checked: the details
came from that website; the name is still the app's own choice. When the redirect goes
to this computer, Details adds that the domain cannot vouch for which program receives
it, as MCP's localhost guidance asks.

## Configuration

Both keys are optional, so neither the sim harness (#370) nor the AppHost (#565) needs
them.

- `OAuth:ClientMetadata:Enabled` (default `true`). A deployment without outbound https
  turns it off. Discovery then stops advertising support, and an `https` client id is
  refused before any fetch, so a copy stored earlier stops working as well.
- `OAuth:ClientMetadata:PrivateHosts` (default empty) names hosts that may resolve to a
  private network (`10/8`, `100.64/10`, `172.16/12`, `192.168/16`, `fc00::/7`), for a
  document served inside a test stack or an internal network. It is a list of exact
  names, never an address switch, and it never admits loopback, link-local or multicast.

## Why the code is in a Platform namespace

`ClientMetadata`, `ClientMetadataFetcher` and `ClientMetadataDocuments` live in
`Cluckwork.Infrastructure.OAuth`, not in Access's `Infrastructure.Modules.Access.OAuth`.
DCR's `Register` endpoint and the metadata resolver share `ToDescriptor`. An endpoint
naming an Access type outside its contract fails CW1004 (#1116). Contracts live only in
Domain and Application, and these rules return OpenIddict descriptors. `OAuthPurgeSweep`
in `Infrastructure.Jobs` is the precedent for OAuth code in the Platform hub.

## The disconnect route

`DELETE /me/connected-apps` and `DELETE /users/{id}/connected-apps` now take the client
id as a `clientId` query parameter instead of a path segment. A URL client id contains
`/`. Kestrel leaves `%2F` encoded in a route value, and proxies differ in whether they
decode it, so a path segment could not name a metadata client reliably.

## What this does NOT cover

- `private_key_jwt` and `jwks`: refused; this server accepts public clients only.
- Prompting again when a document changes (draft §8.4). A changed name or redirect URI
  takes effect at the next refresh; an existing approval stays, and every new code still
  needs the password (#798).
- A document taken down does not disconnect existing connections. The user or an Owner
  does that, as for any app.
- `logo_uri` prefetching (draft §8.8). No logo is shown.
- Domain allow-lists or reputation (draft §8.9).
- The 100-character cap: a longer URL is refused, not stored.

## How it is enforced

- `ClientMetadataFetcherTests` (no database) runs every fetcher rule over a real TLS
  connection to `MetadataDocumentServer`, a loopback Kestrel reached only through
  `FakeNetwork`'s scripted resolver and dialler. It covers each refused address class,
  a mixed answer, the DNS-rebinding pin, `PrivateHosts`, redirect, status, content type,
  the streamed and declared size caps, slow body, slow connect, headers and cookies,
  handler settings, the lifetime clamp and the address table.
- `ClientMetadataTests` pins the URL shape, the 100-character cap, `verifiedDomain`, and
  the document rules against literal inputs.
- `OAuthFailClosedTests.ClientMetadataDocuments` runs the flow on Postgres: connect,
  list, audit and disconnect, the farm switch, consent and preview, the cache, refresh,
  unusable documents stored nowhere, the per-URL and global budgets, the insert race,
  discovery, turning it off, and the purge.
- `tools/oauth/mutation-check.sh` applies one mutation per claim and requires the named
  test to fail.
