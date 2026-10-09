# Connected apps: listing, Disconnect, last used and audit (#799)

> **Rule**: the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md);
> this file is the rationale.

**Status:** accepted
**Date:** 2026-10-09

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

- `User.AppConnected` is written when the approval creates an authorization. That is a
  first approval or one for wider scopes.
- `User.AppReconnected` is written when the approval reuses an authorization that
  already covers every requested scope.

If the transaction fails, it writes neither the authorization nor the row. The details
carry the client id, the app's name at write time and the granted scopes. The row's
connected-app columns (#800) stay empty, because they mean "acted through this app",
and here the person acted on the consent screen themselves. So these rows are not
included under **Only actions through connected apps**. The code is issued after
the endpoint returns, so a failure during issuance leaves a committed approval with no
code. The client then has to ask again, and that request is recorded as a reconnect.

**Last used, at most once every 15 minutes per authorization.** A validation handler
runs one conditional `UPDATE` per OAuth request, and it writes only when
`LastUsedAtUtc` is null or older than 15 minutes. The database's `now()` decides, so
replicas cannot disagree. A failure is logged and the request continues, because the
token was already valid. The screens show last use in days, so up to 15 minutes of
staleness does not show.

## What this does NOT cover

- **A request refused after validation still counts as a use.** The stamp runs during
  authentication, before `CredentialEpochMiddleware` refuses a disabled person.
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
