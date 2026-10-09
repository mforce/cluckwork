# The farm's connected-apps switch refuses; it never revokes (#1146)

> **Rule**: the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md);
> this file is the rationale.

**Status:** accepted
**Date:** 2026-10-09

## What happened

No incident. #788 declined a farm-level switch, partly to avoid a new required
config key. This version needs none, because the switch is farm data:
`Accounts.AllowConnectedApps`, a non-null boolean.

## The rule

**On by default.** The migration adds the column with a database default of `true`,
so every existing farm keeps its connections, and `Account` starts new farms on.
The EF model sets no default: a model default on a `bool` would make EF skip an
inserted `false` and let the database's `true` win.

**Off refuses, Disconnect revokes.** While the switch is off:

1. The consent endpoint answers 403 `Auth.ConnectedAppsOff` after the Cancel branch
   and before any step-up grant is spent or authorization created. Cancel still sends
   the user back to the app with `access_denied`.
2. `CredentialEpochVerifier` refuses every OAuth token of the farm on its next
   request, with 401 `Auth.ConnectedAppsOff`. The switch rides the same statement as
   `Account.IsActive`, so there is no extra round trip. A token is an OAuth token when
   it carries `client_id`, which a session JWT never does (#800); the middleware reads
   the claim itself, so its result does not depend on middleware order.
3. Registration is unaffected: an app registers before anyone signs in, so there is
   no farm to ask (#797).

Turning the switch back on restores every connection nobody disconnected. Nothing
argued for revoking on off. OpenIddict's purge prunes only dead tokens and
authorizations, so a long-off farm loses nothing. Revoking would turn a reversible
switch into a farm-wide Disconnect, which already exists per person.

**Precedence.** Unknown user, disabled user, suspended farm, then the switch, then
the epoch: signing in again cannot cure an app the switch refuses.

**It is a settings field.** The switch travels in the whole `PUT /account/settings`
block, so it inherits Owner-only (#729), the `Version` token and the
`Account.UpdateSettings` audit event, whose before and after snapshots carry it. A
racing settings save at the same `Version` gets 409, never a blend. The command's
field is nullable only so an omitted field is a 400, never a silent `false`. A
separate endpoint was considered and rejected: it needs its own command, audit
action and labels in three locales, and it would leave an open Farm settings form
holding a stale `Version`.

## Accepted costs

- A code issued just before the switch goes off still redeems into a token. That
  token is refused on its first request.
- `OAuthLastUsedStamp` runs during token validation, before the middleware refuses,
  so an app retrying while the switch is off still moves its **Last used**. Suspension
  and a disabled user behave the same way today.

## Proof

`OAuthFailClosedTests.ConnectedAppsSwitch.cs` holds one test per rule, and
`tools/oauth/mutation-check.sh` names the mutant each one kills.
