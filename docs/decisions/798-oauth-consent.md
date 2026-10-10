# Consent with step-up, and the OAuth server in Production (#798)

> **Rule** — the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md);
> this file is the rationale. The design it implements is in
> [`docs/plans/788-mcp-oauth/`](../plans/788-mcp-oauth/02-design.md) and the
> "Consent screen: decided" comment on #788.

**Status:** accepted
**Date:** 2026-10-09

## What happened

No incident. #795 shipped a stub: a caller signed in with a session bearer approved its
own authorization request, and Production ran no authorization server. This slice
replaces the stub with consent and turns the server on in Production.

## The flow

The SPA keeps its access token in tab memory, so a browser navigation to the authorize
endpoint carries no bearer, and `fetch` cannot read a cross-origin redirect. The
endpoint therefore answers three kinds of caller:

1. **A navigation** (no bearer). OpenIddict has already validated the client, the
   redirect URI, PKCE and the scopes. The endpoint redirects to the SPA's `/connect`
   route with the same query. The path is relative, so it cannot leave this origin.
2. **The SPA asking** (session bearer). The endpoint returns what the consent screen
   shows, `{ clientId, clientName, redirectHost, scopes, alreadyAllowed, alreadyApproved, assignedFlocks }`,
   or, when it issues a code or an error, `{ redirectUri }` for the SPA to navigate to. An inline
   handler on `ApplyAuthorizationResponseContext` writes that JSON for any request that
   carries a bearer. Response modes other than `query` are removed, so the redirect is
   always one URL.
3. **An expired bearer.** 401, so the SPA refreshes and asks again. A redirect to
   `/connect` here would hand `fetch` the SPA's HTML.

The SPA approves by repeating its request with `X-Cluckwork-Step-Up` and cancels with
`X-Cluckwork-Consent: deny`, which sends `access_denied` to the client. Approval and
cancel stay on the GET authorize endpoint because #796 refuses any other method there.

## Consent

- **All or nothing.** The token gets every requested scope or none. An empty `scope`
  defaults to `farm:read`, the narrower of the two; the screen shows what it gets.
- **Step-up.** `IAccessModule.ConsumeStepUpGrantAsync` validates and spends a #308 grant,
  exactly as the user-administration handlers do. `IStepUpGrantService` stays internal to
  Access (#857).
- **Every code costs the password.** No path issues a code without a spent step-up
  grant, a reconnect included. Approval creates a *permanent* authorization holding the
  requested scopes. When one valid permanent authorization of this user for this app
  already holds every requested scope, the payload says `alreadyApproved`, the SPA asks
  for the password only, and the approval reuses that authorization. A request for more
  sets `alreadyApproved` to false and shows the permissions again, with `alreadyAllowed`
  naming what an earlier approval covered.
- **What the app would reach.** `assignedFlocks` is the flock scope this very request
  resolved (#388, #612): null when the user reaches every flock, which includes a Worker
  with no assignments, else the assigned flocks' names, read through `IAccessModule`.
  The screen states the requested scopes within that scope and the role, never what the
  role name alone suggests.
- **The redirect host** comes from the redirect URI OpenIddict validated, not the
  request's `redirect_uri`. A client with one registered URI may omit the parameter, and
  OpenIddict then keeps the registered one on its validation context.
- **Disconnect.** Revoking the authorization revokes every token it issued (#796), and a
  revoked authorization never skips consent. Two approvals racing can create two
  authorizations, so #799's Disconnect must revoke every valid authorization of the user
  for that app, not one.

## A reconnect skips the permissions, never the password

#788 decided to skip re-approval when an app asks for nothing new. The first version of
this slice skipped the password too, and review found what that cost. Someone holding
only a stolen session access token (the #308 threat) could ask the authorize endpoint, as
the SPA does, for an app the user had approved, with their own PKCE challenge. The JSON
response handed them a code, which redeemed for an access token that lives until revoked,
far beyond the 15-minute session token. #308 exists to stop that kind of escalation.

The maintainer decided on 2026-10-09: a reconnect skips the permission explanation but
still asks for the password. App tokens never expire, so a person reconnects an app
rarely, and the password costs little each time. What #788 wanted to avoid was people
clicking through the same permission list; the list is still skipped.

Rejected: validating the refresh cookie as an independent browser proof. It keeps a
reconnect silent, but needs a new proof endpoint under the cookie's path and its own
replay and substitution tests, and it still falls to full same-origin script execution.

## Production

`OAuthIssuer()` no longer exempts Production. A Production serving process requires
`OAuth:Issuer` to be an absolute https URL with no query or fragment, and refuses to
start otherwise. The guard is serving-only (#347), so one-shot verbs run without it.
Outside Production the issuer stays optional, as #795 chose. OpenIddict's HTTPS
requirement stays on outside Development, so a Production deployment must present
https to the API, directly or through a trusted proxy (#260).

**Discovery names endpoints under the issuer**, not under the request's Host. #795's
discovery followed the Host the client called; behind a proxy that can be an internal
name. The issuer is the one URL the deployment declares as public, so the
authorization, token and registration endpoints are rebased onto it.

The sim harness supplies `https://cluckwork-sim.local/`. That stack is plain http, so
OpenIddict refuses its protocol requests; only the boot needs the value. The AppHost runs
Development and passes the API's own endpoint as `OAuth__Issuer`, so discovery follows
`LocalPorts:Api`; a Development run outside the AppHost uses
`appsettings.Development.json`'s `http://localhost:8080/`.

## The return path after sign-in

Sign-in returns to the route the user came from, carried in router state, never in a
query parameter. `returnPath` still resolves it against the page's origin and goes home
for anything that lands elsewhere: an absolute URL, `//host` and `/\host`. `/connect`
keeps its query, which is the app's request; every other route returns without one, as
before.

## What this does NOT cover

- **`prompt` and `max_age`.** Ignored, so `prompt=consent` cannot force the screen.
- **An audit event for cancel.** Cancel writes no audit row. Whether it should is still
  open (#788's mockup question 4).

Delivered since this record was written: the consent screen at `/connect`, the login
notice, the shared step-up password component, their translations, Help and glossary
entries (#798); the Connected apps panel and page, Disconnect and its
`User.AppDisconnected` audit (#799); connected-app attribution on audit rows (#800); the
farm switch (#1146). Approving an app spends the step-up grant
first, then commits the approval and its `User.AppConnected` or `User.AppReconnected`
row in one transaction ([`799-connected-apps.md`](799-connected-apps.md)).

## How it is enforced

- `OAuthConsentTests` drives each rule: the consent payload and no row before approval,
  a refused and a replayed grant, the JSON redirect with `state` and `iss`, a reconnect
  that gets no code from the bearer alone and spends its grant, a wider request, another
  user's approval, a revoked approval, cancel, a signed session expired past the clock
  skew, a malformed bearer, an omitted `redirect_uri`, the read-only default, an unknown
  scope, and discovery from another host.
- `OAuthServerTests.AuthorizationRequest_WithoutASignedInUser_GoesToTheConsentRoute`
  pins the navigation hand-off.
- `OAuthServerProductionTests` runs the whole flow in a Production host and refuses
  three bad issuers.
- `ProcessRoleGuardTests` has a row per issuer violation; `ServingGuardCoverageTests`
  maps `EnsureOAuthIssuer` to both.
- `web/src/auth/returnPath.test.ts` and `Login.test.tsx` cover the return path.
- `tools/oauth/mutation-check.sh` applies one mutation per claim and requires the named
  test to fail.
