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
- Commerce read tools (#808): `cluckwork_list_customers` and `cluckwork_list_sales_orders`
  for every role but ReadOnly, `cluckwork_list_order_payments` and
  `cluckwork_list_customer_balances` for Owner, Manager and Sales. Amounts are
  `{"amount": "<decimal>", "currency": "<code>"}`; a Worker's orders carry
  `outstanding: null`.
- Scope gates: `tools/list` shows a tool only for role ∩ scope; a hidden tool's call is a
  JSON-RPC error inside a 200.
- Revocation: the farm switch (Setup › Connected apps) and Disconnect (Account › Connected
  apps).

## Prerequisites

**This run needs exclusive use of the shared `cluckwork-sim` stack.** It changes three
values in `.env.sim` and resets the stack, so agree it with whoever else drives
`cluckwork-sim` first (see `../SKILL.md`, Isolation). `reset.sh` always runs `down -v` on
`cluckwork-sim` and binds 8081. **Never run `reset.sh` for another compose project or with a
renamed compose file:** it does not isolate anything and destroys the shared stack. This
repo has no isolated-stack recipe.

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
2. Reset (`sg docker -c 'bash tools/simulation/reset.sh'`), then start the TLS proxy at the
   issuer's port and install the clients:

   ```sh
   mkdir -p ~/.tmp/mcp-verify && cd ~/.tmp/mcp-verify
   openssl req -x509 -newkey rsa:2048 -nodes -days 7 -subj /CN=localhost \
     -addext subjectAltName=DNS:localhost,IP:127.0.0.1 -addext basicConstraints=critical,CA:TRUE \
     -keyout key.pem -out cert.pem
   node <repo>/tools/verify/mcp/tls-proxy.mjs 8495 8081 cert.pem key.pem &
   export NODE_EXTRA_CA_CERTS=$PWD/cert.pem MCP_TLS_CERT=$PWD/cert.pem
   cd <repo>/tools/verify/mcp && npm ci
   ```

   The browser comes from `tools/simulation/ui/src/browser.ts` (`CLUCKWORK_E2E_CHROMIUM`, a
   system Chromium, or Playwright's download). On a fresh host with none of them, run
   `npx playwright install chromium` here first.
3. When finished, restore `OAuth__Issuer=https://cluckwork-sim.local/`,
   `AllowedHosts=cluckwork-sim.local` and `TRUSTED_PROXY_CIDR=127.0.0.1/32`, stop the proxy,
   and reset again so the next user gets the stock stack.

## Driving it

Run every block from `tools/verify/mcp/` in one shell. Tokens go to files outside the repo;
credentials come from `.sim-cast.json` and are never printed. `jq` reads the replies.

```sh
S=~/.tmp/mcp-verify; C="--cacert $S/cert.pem"; MCP=https://localhost:8495; APP=http://127.0.0.1:8081
CAST=../../simulation/.sim-cast.json; T=$S/owner-read.token; W=$S/worker-read.token; WO=$S/owner-write.token
login() { jq "$1" $CAST | curl -s $APP/api/v1/auth/login -H 'content-type: application/json' -d @- | jq -r .accessToken; }
JWT=$(login '{farmCode:"default-farm",email:.owner.email,password:.owner.password}')
WJWT=$(login '{farmCode:"default-farm"} + ([.cast[]|select(.role=="Worker")][0]|{email,password})')
rpc() { curl -si $C -X POST $MCP/mcp -H "Authorization: Bearer $1" -H 'content-type: application/json' \
  -H 'accept: application/json, text/event-stream' -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' | grep -i '^HTTP\|^www-auth\|^{'; }
```

**1. Discovery.**

```sh
curl -si $C -X POST $MCP/mcp -H 'content-type: application/json' -d '{}' | grep -i '^HTTP\|www-auth'
curl -s $C $MCP/.well-known/oauth-protected-resource/mcp | jq '{resource, authorization_servers}'
curl -s $C $MCP/.well-known/oauth-authorization-server | jq '{issuer, authorization_endpoint, token_endpoint, registration_endpoint}'
curl -sk https://127.0.0.1:8495/.well-known/oauth-authorization-server | jq -r .issuer
```

Expect 401 with `WWW-Authenticate: Bearer resource_metadata="https://localhost:8495/.well-known/oauth-protected-resource/mcp"`;
`resource` `https://localhost:8495/mcp`; every URL under `https://localhost:8495/`, also
when the request named `Host: 127.0.0.1:8495`.

**2. Connect three tokens through consent, with four 1:1 frames.**

```sh
node client.ts connect owner farm:read $T $S/shots
node client.ts connect restricted-worker farm:read $W
node client.ts connect owner daily-entries:write $WO
```

Expect `registered client_id=…`; an authorize URL with the asked `scope`,
`resource=https://localhost:8495/mcp` and `code_challenge_method=S256`; `sign-in at /login`
then `consent at /connect` listing the app, the scope, "Acts as you, with only your access"
and the role; `form validation: … true`; `code=present`; `tools/list` naming the five read
tools for `farm:read` and none of them for `daily-entries:write` (`(none)` before #809,
then only `cluckwork_record_daily_entry`).

**3. Audience and resource refusals.**

```sh
rpc "$JWT"                                     # a session JWT, audience cluckwork-api
CID=$(curl -s $C $MCP/api/v1/oauth/register -H 'content-type: application/json' -d '{"client_name":"probe",
  "redirect_uris":["http://127.0.0.1/cb"],"grant_types":["authorization_code"],"response_types":["code"],"token_endpoint_auth_method":"none"}' | jq -r .client_id)
Q="client_id=$CID&response_type=code&redirect_uri=http%3A%2F%2F127.0.0.1%2Fcb&scope=farm%3Aread&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256"
OTHER=https%3A%2F%2Fother.example%2Fapi; MINE=$(jq -rn --arg u $MCP/mcp '$u|@uri')
curl -s $C "$MCP/api/v1/oauth/authorize?$Q&resource=$OTHER" | head -1
curl -s $C $MCP/api/v1/oauth/token -d "grant_type=authorization_code&client_id=$CID&code=x&redirect_uri=http%3A%2F%2F127.0.0.1%2Fcb&code_verifier=dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk&resource=$OTHER" | jq -r .error
```

Expect 401 with the `resource_metadata` challenge for the JWT, `error:invalid_target` from
authorize and `invalid_target` from the token endpoint.

**4. Step-up is enforced by the server.** The same request the consent screen sends, with
the session JWT and no step-up grant, then with a forged one:

```sh
curl -s $C "$MCP/api/v1/oauth/authorize?$Q&resource=$MINE" -H "Authorization: Bearer $JWT" | jq '{clientId, scopes, redirectUri}'
curl -s $C "$MCP/api/v1/oauth/authorize?$Q&resource=$MINE" \
  -H "Authorization: Bearer $JWT" -H 'X-Cluckwork-Step-Up: forged' | jq -c '{status, title, redirectUri}'
```

Expect the consent payload (`clientId`, `scopes`) with `redirectUri: null`, so no code;
then `{"status":403,"title":"Identity.StepUpRequired","redirectUri":null}`.

**5. Owner reads, paging and filters.**

```sh
node client.ts call $T cluckwork_list_flocks '{"limit":2}' | jq -c '{n:(.items|length), complete, nextOffset}'
node client.ts call $T cluckwork_list_flocks '{"limit":2,"offset":2}' | jq -c '[.items[].name], .nextOffset'
node client.ts call $T cluckwork_list_flocks '{"nameContains":"House A","status":"all"}' | jq -c '[.items[].name]'
node client.ts call $T cluckwork_read_stock | jq -c '[.items[]|{grade, available}]'
read LOT LOTENTRY < <(node client.ts call $T cluckwork_list_egg_lots '{"limit":1}' | jq -r '.items[0]|"\(.lotId) \(.dailyEntryId)"')
node client.ts call $T cluckwork_list_egg_lot_movements "{\"lotId\":\"$LOT\"}" | jq -c '[.items[].movementType]'
node client.ts call $T cluckwork_list_daily_entries '{"flock":"Sim House A","limit":2}' | jq -c '[.items[]|{flockName, date}], .nextOffset'
```

Expect `{"n":2,"complete":false,"nextOffset":2}`, then two other flocks and `4`;
`["Sim House A"]`; one row per grade; `["Production"]`; two Sim House A entries, newest
first, and `2`.

**6. Farm-entered text stays out (#792), with a positive control.** Seed a marked discard
on `$LOT` and a marked void on another Submitted entry through REST, confirm REST keeps the
marker, then fetch exactly those rows through MCP:

```sh
MARK=MCPCANARY-$RANDOM; H=(-H "Authorization: Bearer $JWT" -H 'content-type: application/json')
curl -s $APP/api/v1/stock/lots/$LOT/movements "${H[@]}" -H "Idempotency-Key: $(uuidgen)" \
  -d "{\"movementType\":\"Discard\",\"quantityDelta\":-1,\"reason\":\"$MARK\"}" | jq -r .reason
read ENTRY DAY FLOCK < <(node client.ts call $T cluckwork_list_daily_entries '{"limit":100}' | jq -r --arg skip $LOTENTRY \
  '[.items[]|select(.status=="Submitted" and .dailyEntryId!=$skip)][0]|"\(.dailyEntryId) \(.date) \(.flockId)"')
V=$(curl -s $APP/api/v1/daily-entries/$ENTRY -H "Authorization: Bearer $JWT" | jq .version)
curl -s $APP/api/v1/daily-entries/$ENTRY/void "${H[@]}" -H "Idempotency-Key: $(uuidgen)" -d "{\"version\":$V,\"reason\":\"$MARK\"}" | jq -r .status
curl -s $APP/api/v1/daily-entries/$ENTRY -H "Authorization: Bearer $JWT" | jq -r .voidReason
node client.ts call $T cluckwork_list_egg_lot_movements "{\"lotId\":\"$LOT\"}" > $S/mov.json
node client.ts call $T cluckwork_list_daily_entries "{\"flock\":\"$FLOCK\",\"from\":\"$DAY\",\"to\":\"$DAY\"}" > $S/entry.json
node client.ts call $T cluckwork_list_flocks '{"status":"all","limit":100}' > $S/flocks.json
jq -e '.items|any(.movementType=="Discard" and .quantityDelta==-1)' $S/mov.json
jq -e --arg e $ENTRY '.items|any(.dailyEntryId==$e and .status=="Voided")' $S/entry.json
grep -ciE "$MARK|reason|note|breed" $S/mov.json $S/entry.json $S/flocks.json
```

Expect the marker twice from REST and `Voided`; both `jq -e` lines print `true` (the marked
rows are in the replies); the final count is `0` for every file.

**7. Flock-restricted Worker.**

```sh
node client.ts call $W cluckwork_list_flocks '{"status":"all","limit":100}' | jq -c '[.items[].name]'
node client.ts call $W cluckwork_list_daily_entries '{"limit":100}' | jq -c '[.items[].flockName]|unique'
diff <(node client.ts call $W cluckwork_read_stock | jq -c '[.items[]|{grade, available}]') \
     <(curl -s $APP/api/v1/stock -H "Authorization: Bearer $WJWT" | jq -c '[.[]|{grade: .gradeName, available}]') && echo stock-parity
FOREIGN=$(jq -rn --slurpfile o <(node client.ts call $T cluckwork_list_egg_lots '{"limit":100}') \
  --slurpfile w <(node client.ts call $W cluckwork_list_egg_lots '{"limit":100}') \
  '($w[0].items|map(.lotId)) as $m | [$o[0].items[].lotId | select(. as $id | $m | index($id) | not)][0]')
node client.ts call $W cluckwork_list_egg_lot_movements "{\"lotId\":\"$FOREIGN\"}"
node client.ts call $W cluckwork_list_egg_lot_movements '{"lotId":"00000000-0000-0000-0000-000000000001"}'
node client.ts call $W cluckwork_list_daily_entries '{"flock":"Sim House B"}'
```

Expect `["Sim House A"]` twice (the first Worker's one assigned flock on the sim fixture);
`stock-parity`; for both lots `isError: true` and `… No egg lot <id> exists on this farm.`,
the same words for the foreign and the missing id; `No flock you can see is named or
numbered 'Sim House B'.`

**8. A write-only token cannot read.**

```sh
node client.ts tools $WO
node client.ts call $WO cluckwork_list_flocks
```

Expect no read tool in the list, then `ERROR code=-32600 MCP error -32600: Access forbidden: This tool requires authorization.`

**9. Both protocol versions.**

```sh
for v in 2026-07-28 2025-11-25; do dotnet run probe.cs -- $MCP/mcp $T $v cluckwork_read_stock '{"limit":1}'; done
```

Expect `negotiated=<v>` for each, the five tools, and one stock row. The TypeScript SDK
stops at 2025-11-25, so this is the only 2026-07-28 check.

**10. The farm switch.**

```sh
sw() { V=$(curl -s $APP/api/v1/account -H "Authorization: Bearer $JWT" | jq .version)
  curl -s -o /dev/null -w "switch $1: %{http_code}\n" -X PUT $APP/api/v1/account/connected-apps "${H[@]}" \
    -H "Idempotency-Key: $(uuidgen)" -d "{\"allow\":$1,\"version\":$V}"; }
sw false; rpc "$(cat $T)"; sw true; rpc "$(cat $T)" | head -1
```

Expect `switch false: 204`; 401 with `Bearer error="invalid_token", resource_metadata=…` and
title `Auth.ConnectedAppsOff`; `switch true: 204`; `HTTP/1.1 200 OK`.

**11. Disconnect from the Account page.**

```sh
node client.ts disconnect restricted-worker
rpc "$(cat $W)"
```

Expect `account page: "Cluckwork live verifier is disconnected. …"`, then 401 with the
challenge. `disconnect` removes the first app with that name, so give the Worker one
connection; with two, run it twice and check that each token dies in turn while the
Owner's keeps working.

**12. Optional third client and schema lint.**

```sh
npx -y @modelcontextprotocol/inspector --cli $MCP/mcp --transport http \
  --header "Authorization: Bearer $(cat $T)" --method tools/list --strict
```

Expect the five tools and `0 errors`.

## What each check proves

| Check | Step | Proves |
| --- | --- | --- |
| 401 challenge and both metadata documents under the issuer, whatever the Host | 1 | a client finds the authorization server from the challenge alone, and discovery never leaks the request host |
| Registration, PKCE, `resource`, consent, token exchange | 2 | the whole connect flow works in a real browser with a real SDK |
| Consent text and role | 2 | the screen names the app, the scope and "acts as you" |
| Session JWT and `invalid_target` refusals | 3 | the server mints and accepts tokens for `/mcp` only |
| No code without a spent step-up grant | 4 | the password step-up is server-enforced, not only form validation |
| Paging, filters, a real lot's movements | 5 | the read tools work through a real client |
| Marked rows present, marker absent | 6 | farm-entered reasons and breed never reach a model |
| Worker sees one flock, stock matches REST, foreign id reads as missing | 7 | flock scope reaches tools and does not leak existence |
| Write-only token: empty list, forbidden call | 8 | scope gates both `tools/list` and `tools/call` |
| 2026-07-28 and 2025-11-25 | 9 | one endpoint serves both protocol versions |
| Switch off refuses, on restores | 10 | the switch refuses without revoking |
| Disconnect revokes | 11 | revocation reaches `/mcp` on the next request |
| A client ID metadata document connects end to end | not covered | needs a public https URL the server can fetch; locally only the refusals are visible (see Gotchas) |

## Gotchas

- **The SDK requests every scope the resource metadata lists** and ignores
  `clientMetadata.scope`. `client.ts` narrows the scope on the authorize URL; without that,
  every token holds both scopes and step 8 proves nothing.
- **The throwaway certificate is trusted only for the local proxy.** `client.ts` ignores
  certificate errors only when `MCP_BASE` is localhost or 127.0.0.1, and then aborts every
  browser request to another origin. `probe.cs` trusts exactly the certificate in
  `MCP_TLS_CERT`. Node trusts it through `NODE_EXTRA_CA_CERTS`.
- **Playwright must stay at the version `tools/simulation/ui` pins** (1.62.1), so a
  downloaded browser matches.
- **Login's password field is not an exact label match**; use the harness's non-exact
  `getByLabel`, as `fixtures.ts` does.
- **Each connected app on the Account page is a collapsed `<details>`**; open the row before
  its Disconnect button exists.
- **`probe.cs` inside the repo** must opt out of central package versions and lock files
  (its header does). A stray `packages.lock.json` beside it fails the restore with NU1005;
  delete it.
- **The fixture has no reason text.** Step 6 seeds it; without the two `jq -e` lines the
  redaction check can pass on rows that never carried a reason.
- **Voiding the entry behind `$LOT`** would also touch the lot; step 6 voids a different
  Submitted entry.
- **Expected console noise on `/connect`:** one `401` (the session probe) and
  `An SSL certificate error occurred when fetching the script` (the service worker refuses
  the throwaway certificate).
- **Client ID metadata documents** need a public https URL. Locally, a loopback client id is
  refused before any fetch (`must be a plain https URL with a host name`), and a public URL
  that answers 404 gives `did not answer 200 OK` and logs
  `Client metadata document from {Host} refused`.
- A token is a Data Protection blob, not a JWT, so its audience is proven by behavior
  (steps 3 and 8), not by decoding it.
