# Runbook: extend the Owner two-factor grace period or switch the requirement off (`TwoFactor:OwnerGraceDays`, `TwoFactor:RequireForOwners`)

**When to use this:** Owners must not be locked out of the app for want of two-factor authentication. Use it when:

- a fault keeps Owners from turning on two-factor authentication, so Owners past their deadline see only the setup screen and cannot finish it;
- you rolled the API back to an image older than #1281 and are about to deploy a gated image again; or
- the deployment must stop requiring two-factor for Owners ([step 4](#4-switch-the-requirement-off)).

**Not this runbook:** an Owner who lost their authenticator and their recovery codes. That Owner needs another Owner's reset or [`recover-admin`](break-glass-account-recovery.md).

**Blast radius:** every Owner on the deployment. `OwnerGraceDays` moves every Owner's deadline at once, and `RequireForOwners` turns the requirement off for every Owner at once; neither can exempt one user. A restart ends no session.

**Prerequisites:** the right to change the API's environment and restart it.

**Last drilled:** not recorded.

---

## How the grace period works

While the requirement is on ([step 4](#4-switch-the-requirement-off)), each Owner's grace starts at their first signed-in request after #1281, or, for a user who was never an Owner, at their first request after a promotion to Owner. It is stored in `AspNetUsers`. A former Owner who is promoted again keeps the old start. It lasts `TwoFactor:OwnerGraceDays` days, 14 when the setting is unset. Until then the app refuses nothing. After it, an Owner without two-factor reaches only the password change, the two-factor setup and sign-out, and every other request, connected apps included, answers 403 `Auth.TwoFactorEnrolmentRequired`.

Each Owner has their own deadline. An Owner who is added, or a user promoted to Owner for the first time, gets a full grace from their first request, even when an Owner without two-factor added them.

The value is a whole number from 1 to 365. Any other value stops the API from starting. The API reads it at startup, so a change needs a restart.

The start never moves, so raising the setting cannot help an Owner whose start is more than 365 days old. A two-factor reset and [`recover-admin`](break-glass-account-recovery.md) keep the start too. While a fault blocks enrolment, another Owner who is not gated (enrolled, or still in their own grace) can change that Owner's role to Manager, which lifts the gate, and change it back once enrolment works. A farm whose Owners are all gated has no such Owner, the same as a farm with one gated Owner. It waits for the fault to be fixed, or you roll the API back to an image older than #1281, which runs no gate; read [step 2](#2-after-a-rollback-raise-it-before-you-redeploy) before you deploy the gated image again.

## Procedure

### 1. Raise the setting

Set the environment variable `TwoFactor__OwnerGraceDays` on the API service to a number of days that covers the outage, for example `30`. Restart the API.

### 2. After a rollback, raise it before you redeploy

An image older than #1281 runs no gate, but every Owner's grace keeps counting from the start stored in the database. Owners whose deadline passes while the old image runs are refused at their next request once the gated image is back. Raise `TwoFactor__OwnerGraceDays` (step 1) before you deploy the gated image, so they get time to enrol.

### 3. Lower it again

When the fault is fixed and the Owners have enrolled, remove the variable or set it back to `14`, and restart.

### 4. Switch the requirement off

Set `TwoFactor__RequireForOwners=false` on the API service and restart. Until you remove it:

- the app refuses no Owner for want of two-factor, and records no grace start;
- an Owner may turn their own two-factor off, as a Manager may;
- `GET /api/v1/auth/two-factor` answers no `requiredBy`, so the app shows no deadline once its banner ships;
- every startup logs one warning: `Owner two-factor requirement off: TwoFactor:RequireForOwners is false, so no Owner must use two-factor`.

The value is `true` or `false`, in any case, and surrounding spaces are ignored, so ` false ` turns the requirement off too. Any other value, `0`, `1` and `yes` included, stops the API from starting.

### 5. Switch the requirement back on

Remove the variable, or set it to `true`, and restart. What each Owner without two-factor sees depends on their grace start:

- An Owner with no start, such as one who first signed in while the requirement was off, gets a fresh grace of `OwnerGraceDays` days from their next request.
- An Owner whose start was set before the requirement went off keeps that start, and so does one who turned their two-factor off meanwhile. Switching the requirement off does not stop the days counting, so an Owner whose deadline passed meanwhile is refused at their next request.

To give those Owners time to enrol, raise `TwoFactor__OwnerGraceDays` ([step 1](#1-raise-the-setting)) in the same restart, and lower it again ([step 3](#3-lower-it-again)) once they have enrolled. Raising it cannot help an Owner whose start is more than 365 days old; for that Owner, use the way out in [How the grace period works](#how-the-grace-period-works).

## Verify

1. The API log at startup has one warning: `Owner two-factor grace extended: TwoFactor:OwnerGraceDays is 30, above the default 14`. While the setting is above 14 the warning appears at every start, so a forgotten extension stays visible.
2. Signed in as an Owner, `GET /api/v1/auth/two-factor` answers `requiredBy` moved out by the extra days.
3. With the requirement off, the startup log has the `Owner two-factor requirement off` warning, and the same request answers no `requiredBy`.

## If it fails

- **The API does not start** and the log says `TwoFactor:OwnerGraceDays must be a whole number from 1 to 365, and '<value>' is not.` A number out of range, a fraction and a word all fail this way. Fix the value. A value of 0 or a negative value never means "no grace".
- **The API does not start** and the log says `TwoFactor:RequireForOwners must be true or false, and '<value>' is not.` Set it to `false`, or remove it.
- **`Auth.OwnerGraceStartFailed` warnings** in the log. Setting an Owner's grace start failed, so the request passed and the next one tries again. While it keeps failing, that Owner's grace does not end. A steady rate points at the database; see the [log redaction policy](../security/log-redaction-policy.md) for the event.

## Drill

1. On a scratch stack, sign in as an Owner without two-factor, so the grace starts.
2. Move that Owner's start back 15 days in the scratch database, and check that `GET /api/v1/flocks` answers 403 `Auth.TwoFactorEnrolmentRequired`.
3. Set `TwoFactor__OwnerGraceDays=30`, restart, and check the startup warning and that the same request now answers 200.
4. Expected end state: the Owner is in grace again, and removing the variable and restarting refuses them again.
