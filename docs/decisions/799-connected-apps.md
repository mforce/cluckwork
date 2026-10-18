# Connected apps: listing, Disconnect, last used and audit (#799)

> **Rule**: the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md);
> this file is the rationale.

**Status:** accepted, amended 2026-10-18 by #1326
**Date:** 2026-10-09

## Amendment, 2026-10-18: exact reuse (#1326)

With partial consent ([`798-oauth-consent.md`](798-oauth-consent.md)), an approval
reuses only a valid permanent authorization whose scopes **equal** the granted set G,
and writes `User.AppReconnected`. Otherwise it creates a new authorization holding G and
writes `User.AppConnected`, including when G is narrower than an earlier approval. Two
approvals that together cover a request are never treated as one. The audit details now
carry `requested`, the scopes the app asked for, beside `scopes`, the scopes granted, so
an unticked permission is explainable from the row. Earlier authorizations are never
changed: Connected apps lists the union of every valid approval, and Disconnect revokes
them all. Accepted cost: one authorization per distinct granted set, so the stored row
always equals the token's scopes.

## What happened

No incident. #798's consent screen tells a person they can disconnect an app in
**Account › Connected apps**. This slice builds that panel and the Owner's farm-wide
page, and audits connecting and disconnecting.

## The rule

**A farm is its users.** OpenIddict's tables carry no `AccountId`. The farm-wide list
selects authorizations whose subject is one of the farm's users (`AspNetUsers` by
`AccountId`, in the same statement). Disconnect first confirms, in its own transaction,
that the person is on the caller's farm. An Owner who names another farm's person
gets 404, the same answer as for a missing connection.

**Disconnect revokes everything the person gave the app.** One person can hold several
valid authorizations for one app: a wider request creates a second one, and two
approvals can race (#798). Disconnect does three things in one transaction, which is
the idempotency middleware's under HTTP:

1. It revokes every valid authorization of that person for that client id.
2. It revokes every valid or inactive token of that pair.
3. It writes `User.AppDisconnected` with the human as the actor (#500).

Validation checks the authorization on every request (#796), so the app is refused on
its next one. The `authorization-alone-refuses` mutant in
`tools/oauth/mutation-check.sh` proves the authorization revoke is enough by itself.
The token revoke makes sure no token stays valid after its authorization is revoked.

**Through the model, not OpenIddict's managers.** The managers exist only where the
OAuth server runs, but the screens exist everywhere. So `ConnectedApps` reads and
updates the entities through `AppDbContext`. OpenIddict's entity cache is scoped to
the request, so the next request sees a bulk status update.

**No step-up for Disconnect.** Disconnect only removes access. The user-administration
actions ask for a password because they give out access or take a person's access
away. Disconnect does neither.

**Connecting is audited where the password is spent.** The maintainer decided on
2026-10-09 that approving an app writes an audit event. The authorize endpoint writes
it after the step-up grant is spent, in the same transaction that creates the
authorization:

- `User.AppConnected` is written when the approval creates an authorization: one for a
  set of granted scopes no valid approval holds exactly, wider or narrower (#1326; before
  it, a first approval or one for wider scopes).
- `User.AppReconnected` is written when the approval reuses an authorization whose
  scopes equal the granted set (#1326; before it, one that covered every requested
  scope).

If the transaction fails, it writes neither the authorization nor the row. The details
carry the client id, the app's name at write time and the granted scopes. The row's
connected-app columns (#800) stay empty, because they mean "acted through this app",
and here the person acted on the consent screen themselves. So these rows are not
included under **Only actions through connected apps**. The code is issued after
the endpoint returns, so a failure during issuance leaves a committed approval with no
code. The client then has to ask again, and that request, with the same choice, is
recorded as a reconnect.

**Last used, at most once every 15 minutes per authorization.** `CredentialEpochMiddleware`
calls `ConnectedApps.RecordUseAsync` once the account and credential checks pass (#1229;
until then an OpenIddict validation handler did it during authentication). It runs one
conditional `UPDATE` per OAuth request, and it writes only when
`LastUsedAtUtc` is null or older than 15 minutes. The database's `now()` decides, so
replicas cannot disagree. A failure is logged and the request continues, because the
token was already valid. The screens show last use in days, so up to 15 minutes of
staleness does not show.

## What this does NOT cover

- **Last used is not proof that the app did anything.** Since #1229 a request refused
  for a disabled person, a suspended farm, the farm switch or a superseded credential
  does not count. A request that passes those checks and is then refused for a missing
  scope or role, or whose MCP tool fails, still counts.
- **One round trip per OAuth request.** The conditional update reads one row by key
  even when it writes nothing. An in-process gate could skip it. It is not built.
- **A forced audit-write failure.** No test makes the audit write fail to show that
  the approval rolls back. Atomicity rests on `IUnitOfWork.ExecuteInTransactionAsync`.

## How it is enforced

`OAuthFailClosedTests.ConnectedApps.cs` covers:

- user isolation;
- the Owner-only gate;
- farm isolation;
- revoking every authorization and its tokens;
- the next request failing;
- the disconnect audit row;
- the three connect cases;
- last-used throttling.

`tools/oauth/mutation-check.sh` has a mutant for each of them.
