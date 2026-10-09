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
   shows, `{ clientId, clientName, redirectHost, scopes, alreadyAllowed }`, or, when it
   issues a code or an error, `{ redirectUri }` for the SPA to navigate to. An inline
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
- **Skip when nothing is new.** Approval creates a *permanent* authorization holding the
  requested scopes. A later request whose scopes one valid permanent authorization of
  this user for this app already holds is approved without the screen or the password,
  and reuses that authorization. A request for more shows the screen again, with
  `alreadyAllowed` naming what an earlier approval covered.
- **Disconnect.** Revoking the authorization revokes every token it issued (#796), and a
  revoked authorization never skips consent. Two approvals racing can create two
  authorizations, so #799's Disconnect must revoke every valid authorization of the user
  for that app, not one.

## Accepted risk: a stolen session bearer and an approved app

No incident. The skip has a cost. Someone holding only a stolen session access token (the
#308 threat) can ask the authorize endpoint, as the SPA does, for an app the user already
approved, with their own PKCE challenge. Nothing new is asked, so no password is needed,
and the JSON response hands them the code. They redeem it for an access token that lives
until revoked, which outlives the 15-minute session token. #308 exists to stop exactly
this kind of escalation.

What bounds it: the token carries only scopes the user approved for that app, never more
than the user's role, and the credential epoch revokes it on a password change, a role
change, a disable or a suspension (#364, #796). Closing it means asking for the password
on every connection, which #788 rejected because it trains people to click through.
**The maintainer has not yet confirmed this trade.** Requiring step-up on every
connection is a one-branch change in `OAuthEndpoints.Authorize`.

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
Development, where `appsettings.Development.json` already sets an issuer.

## The return path after sign-in

Sign-in returns to the route the user came from, carried in router state, never in a
query parameter. `returnPath` still resolves it against the page's origin and goes home
for anything that lands elsewhere: an absolute URL, `//host` and `/\host`. `/connect`
keeps its query, which is the app's request; every other route returns without one, as
before.

## What this does NOT cover

- **The consent and login screens.** The SPA route, the login notice, the shared step-up
  password component, translations, Help and the glossary follow in this PR once the
  maintainer picks a direction.
- **`prompt` and `max_age`.** Ignored, so `prompt=consent` cannot force the screen.
- **Audit events for connect and cancel.** Not decided (#788's mockup question 4).

## How it is enforced

- `OAuthConsentTests` drives each rule: the consent payload and no row before approval,
  a refused and a replayed grant, the JSON redirect with `state` and `iss`, the skip,
  a wider request, another user's approval, a revoked approval, cancel, the expired
  bearer, the read-only default, an unknown scope, and discovery from another host.
- `OAuthServerTests.AuthorizationRequest_WithoutASignedInUser_GoesToTheConsentRoute`
  pins the navigation hand-off.
- `OAuthServerProductionTests` runs the whole flow in a Production host and refuses
  three bad issuers.
- `ProcessRoleGuardTests` has a row per issuer violation; `ServingGuardCoverageTests`
  maps `EnsureOAuthIssuer` to both.
- `web/src/auth/returnPath.test.ts` and `Login.test.tsx` cover the return path.
- `tools/oauth/mutation-check.sh` applies one mutation per claim and requires the named
  test to fail.
