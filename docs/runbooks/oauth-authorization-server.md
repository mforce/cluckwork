# Runbook: the OAuth authorization server for connected apps (#788)

**When to use this:** setting up a deployment so third-party apps can connect; an app
reports that discovery names the wrong address, or that every protocol request is
refused; an AI app cannot reach the MCP endpoint at `/mcp`; the Data Protection key ring
was replaced or lost and apps stopped working; or someone needs an app's access removed.

**Not this runbook:** a boot that fails on the Data Protection certificate, or a key-ring
replacement itself, is
[`data-protection-key-ring.md`](data-protection-key-ring.md). A boot that fails on
`RateLimiting:TrustedProxies` is configuration (#260); set the value as described below.

**Blast radius:** changing `OAuth:Issuer` changes every URL discovery advertises. Apps
that cached the old discovery document or issuer keep using the old address until they
fetch it again. Replacing or losing the key ring disconnects every app on every farm
(section D).

**Prerequisites:** the ability to change the deployment's configuration and restart every
serving instance, and `curl` on a machine that reaches the public URL.

**Last drilled:** not recorded.

Background: [`795-openiddict-server.md`](../decisions/795-openiddict-server.md),
[`798-oauth-consent.md`](../decisions/798-oauth-consent.md),
[`799-connected-apps.md`](../decisions/799-connected-apps.md),
[`1146-connected-apps-switch.md`](../decisions/1146-connected-apps-switch.md).

---

## A. Configure the public address

### 1. Set `OAuth:Issuer`

Set `OAuth__Issuer` to the public https URL people use to reach Cluckwork, with a
trailing slash, for example `https://farm.example/`. A Production serving process
refuses to start when it is missing, is not an absolute `https` URL, or carries a query
or fragment. One-shot verbs never need it.

Discovery builds every endpoint URL under this value, never under the `Host` the request
arrived with, so a proxy's internal hostname never leaks to apps.

### 2. Make `AllowedHosts` include the issuer's host

`AllowedHosts` must name the issuer's host (for `https://farm.example/`, `farm.example`).
Production already refuses `*` or a blank value (#319). Nothing checks that the two agree:
when they differ, the server boots and host filtering answers 400 to every request apps
send to the advertised URLs. Loopback is always allowed for health probes.

### 3. Trust the proxy that terminates TLS

OpenIddict refuses protocol requests that did not arrive over https. Behind a proxy that
terminates TLS, the API sees https only through `X-Forwarded-Proto`, which it trusts only
from networks in `RateLimiting:TrustedProxies` (#260). The setting is a list, so set it
with one indexed variable per network: `RateLimiting__TrustedProxies__0=<proxy CIDR>`,
then `RateLimiting__TrustedProxies__1` for a second one. An unindexed
`RateLimiting__TrustedProxies` binds nothing, and Production then refuses to start with
an empty list. If the list does not cover the proxy, discovery, authorize and token
requests answer 400 `invalid_request` with `This server only accepts HTTPS requests.`
A deployment that terminates TLS in the API itself sets
`RateLimiting__AllowNoTrustedProxies=true` instead.

### 4. Nothing else to provision

There are no OAuth signing or encryption certificates. Authorization codes and access
tokens are protected by the Data Protection key ring that #794 already requires
(`DataProtection__CertificatePem`, `DataProtection__PrivateKeyPem`). OpenIddict's own
signing and encryption keys are ephemeral, because they would sign only identity tokens,
which this server never issues.

Optional: `OAuth__ClientMetadata__Enabled=false` turns off client ID metadata documents
(#1148) when the API has no outbound https. Apps then register themselves.

## B. Verify discovery

```bash
curl -sS -i https://farm.example/.well-known/oauth-authorization-server
```

Expected: `200`, and JSON whose `issuer` is the configured `OAuth:Issuer`, and whose endpoints all
sit under it:

```
"issuer": "https://farm.example/",
"authorization_endpoint": "https://farm.example/api/v1/oauth/authorize",
"token_endpoint": "https://farm.example/api/v1/oauth/token",
"registration_endpoint": "https://farm.example/api/v1/oauth/register",
"token_endpoint_auth_methods_supported": ["none"],
```

Every app registers as a public client, so `none` is the only method listed (#1294).
Claude's hosted apps use a client ID metadata document only when `none` is listed.

`client_id_metadata_document_supported: true` appears unless step A.4's switch turned it
off.

A 400 has two causes. Read the body to tell them apart:

- An HTML page saying `Invalid Hostname`: `AllowedHosts` does not name the host (A.2).
- JSON with `This server only accepts HTTPS requests.`: the API does not trust the proxy
  that terminated TLS (A.3).

## C. The farm switch and disconnecting apps

- **Farm switch (#1146).** An Owner turns connected apps on or off at the top of
  **Setup › Connected apps** (`PUT /api/v1/account/connected-apps`). It is on by default.
  Off refuses: consent answers 403 `Auth.ConnectedAppsOff`, and every OAuth token of the
  farm gets 401 on its next request. Off revokes nothing, so turning it back on restores
  every connection nobody disconnected.
- **A person disconnects their own app** in **Account › Connected apps**
  (`DELETE /api/v1/me/connected-apps`). Any role can.
- **An Owner disconnects anyone's app** on **Setup › Connected apps**
  (`DELETE /api/v1/users/{id}/connected-apps`).

Disconnect revokes the person's approvals for that app and every token they issued, in
one transaction, and writes `User.AppDisconnected`. The app is refused on its next
request. It can connect again only through the consent screen.

## D. After the key ring is replaced or lost

Replacing the Data Protection certificate, or deleting every stored key (procedure C of
[`data-protection-key-ring.md`](data-protection-key-ring.md)), makes every outstanding
authorization code and access token unreadable. Every connected app on every farm gets
401 on its next API request. An app that was between authorization and redemption gets
400 `invalid_grant` from `POST /api/v1/oauth/token` instead. There are no refresh tokens,
so each app has to send its user through authorization again.

Procedure B deletes only the plaintext keys it names. Only codes and tokens protected by
those keys fail. An app holding such an access token gets 401; redeeming such a code
gets 400 `invalid_grant`. Either way the app must reauthorize. Tokens protected by
the encrypted keys B keeps still work.

The approvals and the app registrations live in the database, not in the ring, so they
survive. A person whose approval still covers what the app asks for sees the consent
screen ask only for their password. Tell the farms before a planned replacement.

## E. The MCP endpoint

AI apps connect to `<OAuth:Issuer>mcp`, for example `https://farm.example/mcp` (#806). It
exists only where the authorization server does: a process with no `OAuth:Issuer` maps
neither. It accepts only access tokens this server issued for that address; a sign-in
session token is refused.

An app finds everything else from the endpoint. Its first request has no token, gets 401,
and the challenge names the protected-resource metadata (RFC 9728). That document names the
authorization server, and the app continues with discovery as in B. Both documents build
their URLs from `OAuth:Issuer`, never from the request's `Host`. Section A covers `/mcp` too.
`AllowedHosts` must name the issuer's host, or every request gets the `Invalid Hostname`
400, and the TLS proxy must be trusted, or the authorization flow the app needs answers
`This server only accepts HTTPS requests.`

```bash
curl -sS -i -X POST https://farm.example/mcp -H 'content-type: application/json' -d '{}'
curl -sS https://farm.example/.well-known/oauth-protected-resource/mcp
```

Expected: the first answers 401 with

```
WWW-Authenticate: Bearer resource_metadata="https://farm.example/.well-known/oauth-protected-resource/mcp"
```

The second answers 200 with JSON whose `resource` is `https://farm.example/mcp`,
whose `authorization_servers` is `["https://farm.example/"]`, and whose
`scopes_supported` lists the five scopes: `farm:read`, `daily-entries:write`,
`daily-entries:submit`, `operations:write` and `expenses:write` (#1311, #1313). The metadata path is the
well-known prefix followed by the resource's own path, `/mcp`. A `GET /mcp` answers 405
with `Allow: POST`, because the endpoint serves no event stream. A request body over 32 KB
is refused.

A token refused at `/mcp` answers 401 with the same challenge plus
`error="invalid_token"`. That tells the app to authorize again. The body's `title` names
the cause: `Auth.ConnectedAppsOff` while the farm's switch is off (C), or
`Auth.CredentialsSuperseded` after the person's password was reset. A disconnected app's
revoked token gets the 401 as well. A tool the person's role or the token's scopes do not
cover is missing from `tools/list`, and calling it returns a JSON-RPC error inside a 200,
not an HTTP error.

## If it fails

- **Discovery names the wrong host or port.** `OAuth:Issuer` is wrong. Fix it and restart
  every serving instance; apps must fetch discovery again.
- **Discovery, authorize or token requests are refused as not https.** The proxy is not
  in `RateLimiting:TrustedProxies` (A.3).
- **Apps get 401 `Auth.ConnectedAppsOff`.** The farm's switch is off (C).
- **An AI app cannot find the authorization server.** Run the two requests in E. A
  `resource_metadata` or `resource` naming the wrong host means `OAuth:Issuer` is wrong.
  A `POST /mcp` answered 405 with `Allow: GET, HEAD` means the process has no
  `OAuth:Issuer`, so `/mcp` is not mapped and the request fell through to the SPA.

## Drill

Against a scratch deployment running Production config behind a TLS proxy:

1. Set `OAuth__Issuer`, `AllowedHosts` and `RateLimiting__TrustedProxies__0` as in A.
2. Run B and E. Expected: the issuer and all three endpoints under the public URL, and
   the `/mcp` challenge and protected-resource metadata under it too.
3. Note the current `RateLimiting__TrustedProxies__*` values. Replace them with one
   network that does not contain the proxy, for example `192.0.2.0/24`, and restart. An
   empty list would fail the boot instead (#260). Run B again. Expected: 400 with
   `This server only accepts HTTPS requests.`
4. Restore the values from step 3 and restart.
5. Expected end state: discovery as in B, and an app completes the authorization flow.
