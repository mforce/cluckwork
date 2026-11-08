# Runbook: extend the Owner two-factor grace period (`TwoFactor:OwnerGraceDays`)

**When to use this:** Owners must not be locked out of the app for want of two-factor authentication. Use it when:

- a fault keeps Owners from turning on two-factor authentication, so Owners past their deadline see only the setup screen and cannot finish it; or
- you rolled the API back to an image older than #1281 and are about to deploy a gated image again.

**Not this runbook:** an Owner who lost their authenticator and their recovery codes. That Owner needs another Owner's reset or [`recover-admin`](break-glass-account-recovery.md).

**Blast radius:** every Owner on the deployment. The setting moves every Owner's deadline at once; it cannot exempt one user. A restart ends no session.

**Prerequisites:** the right to change the API's environment and restart it.

**Last drilled:** not recorded.

---

## How the grace period works

Each Owner's grace starts at their first signed-in request after #1281, or after a promotion to Owner, and is stored in `AspNetUsers`. It lasts `TwoFactor:OwnerGraceDays` days, 14 when the setting is unset. Until then the app refuses nothing. After it, an Owner without two-factor reaches only the password change, the two-factor setup and sign-out, and every other request, connected apps included, answers 403 `Auth.TwoFactorEnrolmentRequired`.

The value is a whole number from 1 to 365. Any other value stops the API from starting. The API reads it at startup, so a change needs a restart.

## Procedure

### 1. Raise the setting

Set the environment variable `TwoFactor__OwnerGraceDays` on the API service to a number of days that covers the outage, for example `30`. Restart the API.

### 2. After a rollback, raise it before you redeploy

An image older than #1281 runs no gate, but every Owner's grace keeps counting from the start stored in the database. Owners whose deadline passes while the old image runs are refused at their next request once the gated image is back. Raise `TwoFactor__OwnerGraceDays` (step 1) before you deploy the gated image, so they get time to enrol.

### 3. Lower it again

When the fault is fixed and the Owners have enrolled, remove the variable or set it back to `14`, and restart.

## Verify

1. The API log at startup has one warning: `Owner two-factor grace extended: TwoFactor:OwnerGraceDays is 30, above the default 14`. While the setting is above 14 the warning appears at every start, so a forgotten extension stays visible.
2. Signed in as an Owner, `GET /api/v1/auth/two-factor` answers `requiredBy` moved out by the extra days.

## If it fails

- **The API does not start** and the log says `TwoFactor:OwnerGraceDays must be a whole number from 1 to 365, and '<value>' is not.` A number out of range, a fraction and a word all fail this way. Fix the value. A value of 0 or a negative value never means "no grace".
- **`Auth.OwnerGraceStartFailed` warnings** in the log. Setting an Owner's grace start failed, so the request passed and the next one tries again. While it keeps failing, that Owner's grace does not end. A steady rate points at the database; see the [log redaction policy](../security/log-redaction-policy.md) for the event.

## Drill

1. On a scratch stack, sign in as an Owner without two-factor, so the grace starts.
2. Move that Owner's start back 15 days in the scratch database, and check that `GET /api/v1/flocks` answers 403 `Auth.TwoFactorEnrolmentRequired`.
3. Set `TwoFactor__OwnerGraceDays=30`, restart, and check the startup warning and that the same request now answers 200.
4. Expected end state: the Owner is in grace again, and removing the variable and restarting refuses them again.
