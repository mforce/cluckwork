# MCP server (connected apps)

`/mcp` is the MCP endpoint a farmer's AI assistant connects to (#806, tools from #807
onward). It accepts only OAuth tokens from Cluckwork's own authorization server, bound to
`<OAuth:Issuer>/mcp`. In-process tests (`tests/Cluckwork.Api.IntegrationTests/Mcp/`) cover
the endpoint; this file covers what they cannot: discovery behind a real host, dynamic
registration, the consent screen in a browser, the token exchange and real tool calls from
real MCP clients.

## Sub-features

- Discovery: the 401 challenge, protected-resource metadata (RFC 9728) and
  authorization-server metadata, all under the issuer.
- Client registration: dynamic registration (`/api/v1/oauth/register`) and client ID
  metadata documents (#1148).
- Consent at `/connect`: the app, the scopes, "Acts as you", the role, and a mandatory
  password step-up (#798).
- Read tools (#807): `cluckwork_list_flocks`, `cluckwork_read_stock`,
  `cluckwork_list_egg_lots`, `cluckwork_list_egg_lot_movements`,
  `cluckwork_list_daily_entries`. Each reply is a page with `items`, `complete` and
  `nextOffset`.
- Scope gates: `tools/list` shows a tool only for role ∩ scope; a hidden tool's call is a
  JSON-RPC error inside a 200.
- Revocation: the farm switch (Setup › Connected apps) and Disconnect (Account › Connected
  apps).

## Prerequisites

The sim stack is plain http, and OpenIddict refuses protocol requests over http outside
Development, so the connect flow needs TLS in front of it. The proxy and both clients are
in [`tools/verify/mcp/`](../mcp/README.md).

1. In this checkout's `tools/simulation/.env.sim` (after `bootstrap.sh`), set:

   ```
   OAuth__Issuer=https://localhost:8495/
   AllowedHosts=localhost
   TRUSTED_PROXY_CIDR=172.16.0.0/12
   ```

   The issuer's host must be in `AllowedHosts`. Docker's port forward makes the bridge
   gateway, not loopback, the address the app sees, so the trusted-proxy range must cover
   the docker networks or `X-Forwarded-Proto` is ignored and every OAuth request answers
   `This server only accepts HTTPS requests.`
2. Reset a stack from this checkout (an isolated compose project if anyone else is driving
   `cluckwork-sim`), then start the TLS proxy at the issuer's port:

   ```sh
   mkdir -p ~/.tmp/mcp-verify && cd ~/.tmp/mcp-verify
   openssl req -x509 -newkey rsa:2048 -nodes -days 7 -subj /CN=localhost \
     -addext subjectAltName=DNS:localhost,IP:127.0.0.1 -addext basicConstraints=critical,CA:TRUE \
     -keyout key.pem -out cert.pem
   node <repo>/tools/verify/mcp/tls-proxy.mjs 8495 <app-port> cert.pem key.pem &
   export NODE_EXTRA_CA_CERTS=$PWD/cert.pem
   (cd <repo>/tools/verify/mcp && npm ci)
   ```

## Driving it

Run from `tools/verify/mcp/`. Tokens go to files outside the repo; credentials come from
`.sim-cast.json` and are never printed.

```sh
S=~/.tmp/mcp-verify; C="--cacert $S/cert.pem"
# 1. Discovery
curl -si $C -X POST https://localhost:8495/mcp -H 'content-type: application/json' -d '{}' | grep -i '^HTTP\|www-auth'
curl -s $C https://localhost:8495/.well-known/oauth-protected-resource/mcp
curl -s $C https://localhost:8495/.well-known/oauth-authorization-server
# 2. Full OAuth flow + tools/list, with four 1:1 consent frames
node client.ts connect owner farm:read $S/owner-read.token $S/shots
node client.ts connect restricted-worker farm:read $S/worker-read.token
node client.ts connect owner daily-entries:write $S/owner-write.token
# 3. Tool calls
node client.ts call $S/owner-read.token cluckwork_list_flocks '{"limit":2}'
node client.ts call $S/owner-read.token cluckwork_list_flocks '{"limit":2,"offset":2}'
node client.ts call $S/owner-read.token cluckwork_list_daily_entries '{"flock":"Sim House A","limit":2}'
node client.ts call $S/owner-read.token cluckwork_list_egg_lot_movements '{"lotId":"<lot from list_egg_lots>"}'
# 4. Revocation
node client.ts disconnect restricted-worker            # drives Account › Connected apps
node client.ts tools $S/worker-read.token              # now 401
# 5. Protocol 2026-07-28 (the TypeScript SDK stops at 2025-11-25)
dotnet run probe.cs -- https://localhost:8495/mcp $S/owner-read.token 2026-07-28 cluckwork_read_stock '{"limit":1}'
# 6. Optional third client and schema lint
npx -y @modelcontextprotocol/inspector --cli https://localhost:8495/mcp --transport http \
  --header "Authorization: Bearer $(cat $S/owner-read.token)" --method tools/list --strict
```

The farm switch: as the Owner, `GET /api/v1/account` for `version`, then
`PUT /api/v1/account/connected-apps` with `{"allow": false, "version": <v>}` and an
`Idempotency-Key`. Turn it back on the same way.

## What each check proves

| Check | Pass looks like | Proves |
| --- | --- | --- |
| Unauthenticated `POST /mcp` | 401, `WWW-Authenticate: Bearer resource_metadata="<issuer>/.well-known/oauth-protected-resource/mcp"` | a client can find the authorization server from the challenge alone |
| Both metadata documents, also with `Host: 127.0.0.1:8495` | `resource` `<issuer>/mcp`; `issuer` and every endpoint under `OAuth:Issuer`, whatever Host the request named | discovery never leaks the request host |
| `connect` | `registered client_id=…`, authorize with `resource=<issuer>/mcp` and S256, `/login` then `/connect`, Allow without password stays put, `code=present`, `tools/list` names the five tools | DCR, PKCE, RFC 8707, consent with step-up, token exchange |
| Session JWT on `/mcp` | 401 with the challenge | a token for another audience is refused |
| `resource=https://other.example/api` on authorize or token | `invalid_target` | the server mints tokens for `/mcp` only |
| Owner calls | pages with `complete:false` and `nextOffset`, the next page continues, a name filter narrows | paging and filters work through a real client |
| Reason redaction | seed a write-off and a void with a marker reason through REST, then grep every tool reply: zero hits for the marker, `reason`, `note`, or breed | farm-entered text stays out (#792) |
| Restricted Worker | only the assigned flock's flocks, entries, lots and stock; another flock's lot reads exactly like a missing id | flock scope reaches tools and does not leak existence |
| `daily-entries:write` token | `tools/list` empty; a call gets JSON-RPC `-32600 Access forbidden` in a 200 | scope gates `tools/list` and `tools/call` |
| Farm switch off | 401, `error="invalid_token"`, body `Auth.ConnectedAppsOff`; on again restores | the switch refuses without revoking |
| Disconnect | the Account page shows the disconnected notice; the token then gets 401 | revocation reaches `/mcp` on the next request |
| `probe.cs` at 2026-07-28 and 2025-11-25 | `negotiated=` the version asked for, five tools, a result | one endpoint serves both protocol versions |

## Gotchas

- **The SDK requests every scope the resource metadata lists** and ignores
  `clientMetadata.scope`. `client.ts` narrows the scope on the authorize URL; without that,
  every token holds both scopes and the write-only check proves nothing.
- **Playwright must match the browsers already installed** (`tools/simulation/ui` pins
  1.62.1). A newer version fails with `Executable doesn't exist`.
- **Login's password field is not an exact label match**; use the harness's non-exact
  `getByLabel`, as `fixtures.ts` does.
- **Each connected app on the Account page is a collapsed `<details>`**; open the row before
  its Disconnect button exists.
- **`probe.cs` inside the repo** must opt out of central package versions and lock files
  (its header does). A stray `packages.lock.json` beside it fails the restore with NU1005;
  delete it.
- **The fixture has no reason text.** Without seeding one, the redaction check passes
  vacuously.
- **Expected console noise on `/connect`:** one `401` (the session probe) and
  `An SSL certificate error occurred when fetching the script` (the service worker refuses
  the throwaway certificate).
- **Client ID metadata documents** need a public https URL the server can fetch. Locally,
  only the refusals can be checked: a loopback IP is refused before any fetch, and a public
  URL that answers 404 logs `Client metadata document from {Host} refused`.
- A token is a Data Protection blob, not a JWT, so its audience is proven by behavior
  (the refusals above), not by decoding it.
