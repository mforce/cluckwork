# Consent with step-up, and the OAuth server in Production (#798)

> **Rule** — the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md);
> this file is the rationale. The design it implements is in
> [`docs/plans/788-mcp-oauth/`](../plans/788-mcp-oauth/02-design.md) and the
> "Consent screen: decided" comment on #788.

**Status:** accepted, amended 2026-10-18 by #1326
**Date:** 2026-10-09

## Amendment, 2026-10-18: partial consent (#1326)

This replaces **All or nothing** under "Consent" and the section "A reconnect skips the
permissions, never the password". The rest stands.

- **The person grants what they tick.** The token and the authorization it is bound to
  hold the granted set G, never more than the requested set R. G is also limited to U,
  the scopes the person's role can use. The server derives U in one place,
  `CluckworkMcp.UsableScopesAsync`: a scope is usable when a registered MCP tool requires
  it and the person meets that tool's role policies, read from the tool's own
  `[Authorize]` metadata. A scope no tool requires is usable by nobody. There is no
  role-to-scope table, on the server or in the SPA.
- **The derivation reads only the policy forms the tools use today.**
  `CluckworkMcp.UsableScopesAsync` in `src/Cluckwork.Api/Hosting/CluckworkMcp.cs` combines
  each tool's `[Authorize]` attributes, its role policies plus one scope policy, and
  evaluates the role part against the person. It does not handle the SDK's other forms:
  `AuthorizationPolicy` metadata on the tool, `IAuthorizationRequirementData`, or a
  requirement that depends on a resource. A tool that uses one of them must extend the
  derivation first; otherwise consent can offer or withhold its scope wrongly.
- **One parser, before the password is spent.** The SPA sends G as a JSON array in
  `X-Cluckwork-Consent-Scopes`. `ConsentSelection.Read` accepts it only when G ⊆ R ∩ U;
  anything else (a scope outside R or U, malformed JSON, a repeated header) is a 400
  `OAuth.InvalidConsentScopes` before the farm switch and before the step-up grant is
  spent. A request without the header, from an older SPA, gets G = R ∩ U.
- **Nothing ticked is Cancel.** `[]`, or an older SPA's approval whose R ∩ U is empty,
  takes Cancel's path: `access_denied` to the app, no grant spent, no authorization, no
  audit row, even when the farm switch is off. **Read farm data** may be unticked; the
  `farm:read` default for an empty `scope` can be unticked too.
- **Checkboxes on every authorization**, a reconnect of a fully approved app included
  (the maintainer, 2026-10-18). Every approval still spends a step-up grant. The payload
  adds `usable` (R ∩ U); a requested scope outside it shows **Not allowed for your
  role** with no checkbox and is never granted. What an earlier approval allowed
  (`alreadyAllowed`) starts ticked, the rest unticked and marked **New**; with no overlap
  every usable scope starts ticked. `alreadyApproved` (one approval holds all of R) now
  only picks the headline.
- **No stored declines.** An unticked scope is simply not granted, so it shows as
  **New** again next time. `alreadyAllowed` is a union across approvals, so the screen
  says "allowed before", never "your last choice".
- **Disconnect removes access.** A narrower approval revokes nothing: earlier
  authorizations and their tokens keep working until **Disconnect** in Account ›
  Connected apps. The screen and Help say so.
- **The token response** names G in `scope`, through OpenIddict's own sign-in handling.
  A call to a tool outside G stays a JSON-RPC `InvalidRequest` (#806), never a 403
  `insufficient_scope`, so clients do not start re-authorization.

Accepted cost: an older SPA's approval now drops role-blocked scopes it used to grant
inertly.

**Accepted risk: an API rollback under an open consent page** (the maintainer,
2026-10-18; No incident). A consent page loaded from a build with selection support and
submitted after the API is rolled back to a build without it sends a header the old API
ignores. That approval grants every requested scope, the unticked ones included, and the
app keeps them until the person chooses **Disconnect**. The SPA and the API ship in one
image, so this needs a rollback while the page is open between load and Allow.

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
   shows, `{ clientId, clientName, redirectHost, scopes, alreadyAllowed, usable, alreadyApproved, assignedFlocks }`,
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

- **All or nothing** (replaced 2026-10-18, see the amendment). The token gets every requested scope or none. An empty `scope`
  defaults to `farm:read`, the narrower of the two; the screen shows what it gets.
- **Step-up.** `IAccessModule.ConsumeStepUpGrantAsync` validates and spends a #308 grant,
  exactly as the user-administration handlers do. `IStepUpGrantService` stays internal to
  Access (#857).
- **Every code costs the password.** No path issues a code without a spent step-up
  grant, a reconnect included (why: "A reconnect skips the permissions, never the
  password" below). Updated 2026-10-18 by #1326: approval creates a *permanent*
  authorization holding the granted scopes G, or reuses a valid permanent authorization
  of this user for this app whose scopes equal G exactly. `alreadyApproved` says one
  such authorization already holds every requested scope; it only picks the screen's
  headline, and the permissions always show. `alreadyAllowed` names the requested scopes
  any earlier approval holds. Before #1326 the approval stored every requested scope and
  reused any authorization covering them, and the SPA asked an approved app for the
  password only.
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

## A reconnect skips the permissions, never the password (replaced 2026-10-18, see the amendment)

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

- `OAuthConsentTests.PartialConsent.cs` and `Mcp/McpPartialConsentTests` drive #1326:
  the token response, stored and audited scopes, refusals before the grant is spent,
  empty choice as Cancel, exact reuse, the usable list, and the tool list per choice.
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
