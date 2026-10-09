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

**It has its own endpoint, on the Connected apps page.** The Owner turns it on or off
at the top of **Setup › Connected apps**, the page that lists the apps it governs.
`PUT /api/v1/account/connected-apps` takes `{ allow, version }`. It is Owner-only
(#729) and needs an `Idempotency-Key` like every write. `Account.SetConnectedApps`
bumps `Version` only on a real change, as `Rename` does. The switch shares the
`Version` token with the Farm settings save, so a racing settings save or a second
Owner holding an older version gets 409, never a silent overwrite. Each change
writes `Account.UpdateSettings` with `AllowConnectedApps` before and after and the
Owner as actor. No new audit action was added; the maintainer accepted that. An
omitted `allow` is a 400, never a silent `false`. The page reads the switch and its
version from `GET /account` with its rows, not from the session's farm, which
another Owner may have changed.

The first version of this slice put the switch in the Farm settings block. The
maintainer moved it to the Connected apps page, where an Owner already looks at
connections. The Farm settings `PUT` no longer carries the field.

The control is MUI `Switch`, a sliding toggle. It costs 5.60 KiB of precache, and the
maintainer raised the ceiling to 1,960 KiB for it (`web/scripts/verify-sw.mjs`).

## Accepted costs

- A code issued just before the switch goes off still redeems into a token. That
  token is refused on its first request.
- `OAuthLastUsedStamp` runs during token validation, before the middleware refuses,
  so an app retrying while the switch is off still moves its **Last used**. Suspension
  and a disabled user behave the same way today.

## Proof

`OAuthFailClosedTests.ConnectedAppsSwitch.cs` holds one test per rule, and
`tools/oauth/mutation-check.sh` names the mutant each one kills.
