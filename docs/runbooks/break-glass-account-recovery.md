# Runbook: break-glass account recovery (`recover-admin`)

**Issue:** #265 · **Applies to:** any Cluckwork deployment · **Privilege required:** shell access to the running deployment (or its database)

## When to use this

A user — most critically a **sole Owner** — has lost their password and cannot get
back in. Cluckwork has **no email/SMTP**, so there is no self-service
password-reset-by-email. The normal recovery paths do not cover a locked-out
sole Owner:

- Self-service change (`Account → change password`) requires the **current** password.
- An Owner can reset **another** user, but is forbidden from resetting **their own** account via the Users screen.
- Re-seeding never touches an **existing** admin's password.

Without this command the only recourse would be direct SQL surgery on the
`AspNetUsers` table. `recover-admin` is that break-glass path, done safely.

## What it does (atomically, in one transaction)

1. Finds the user by email (across accounts; disambiguate with `--account` if ever ambiguous).
2. Sets a **freshly generated** strong temporary password (never one you pass on the command line).
3. **Rotates the security stamp** (invalidates Identity-derived state).
4. **Revokes every refresh token** for that user — all existing sessions/devices are signed out.
5. Writes a conspicuous **`User.BreakGlassReset` audit row** carrying your `--reason`.

> An access token already issued stays valid until it expires (~15 min) — there
> is no server-side denylist. Refresh is dead immediately, so the session cannot
> be extended.

Unlike `seed --profile demo|simulation`, this command is **NOT** environment-gated:
it is designed to run against a real **Production** database. Its safety comes
from requiring shell access to invoke it, plus the audit trail it leaves.

## Procedure

Run the API binary with the `recover-admin` verb on (or with network access to)
the deployment's database. The command **migrates the schema, performs the reset,
prints the temporary password once, then exits** — Kestrel never starts.

```bash
# Minimum: the target's email. --reason is strongly recommended (it lands in the audit row).
dotnet Cluckwork.Api.dll recover-admin \
  --email owner@thefarm.example \
  --reason "sole-owner lockout, ticket OPS-1234"

# If an email ever resolves to more than one account (dormant multi-tenant future):
dotnet Cluckwork.Api.dll recover-admin --email owner@thefarm.example --account <account-guid> --reason "..."
```

The database connection comes from the same configuration the serving process
uses (`ConnectionStrings__Default` / `Database__Provider`, plus the `Jwt__*`
values the host reads at startup). In a container deployment, exec into the app
container so that environment is already present, e.g.:

```bash
docker compose -f deploy/docker-compose.yml exec app \
  dotnet Cluckwork.Api.dll recover-admin --email owner@thefarm.example --reason "OPS-1234"
```

### Expected output (exit code `0`)

```
Break-glass reset complete for owner@thefarm.example (account 0000000a-...). All existing sessions were revoked.
Temporary password: <20-char one-time password>
Log in with this now and change it immediately (Account → change password).
```

The temporary password is written to **stdout only** (never the logger/OTLP), so
it does not persist in structured logs. Capture it from the terminal, hand it to
the user over a trusted channel, and have them rotate it on first login.

### Failure (exit code `1`)

A clear message is written to **stderr**, e.g.:

- `Recovery failed: Recovery.NotFound — Recovery '<email>' was not found.` — check the email.
- `Recovery failed: Recovery.Ambiguous — N users share the email ... pass --account <id>` — add `--account`.
- `Invalid --account '<x>' — must be a GUID.`

Nothing is changed on a failure.

## Post-recovery verification (drill this on staging before you need it)

1. The user logs in with the temporary password → **succeeds**.
2. The **old** password → **rejected**.
3. Any previously open session (a still-loaded browser tab) can no longer refresh → signed out.
4. `SELECT action, actor_email, reason, occurred_at_utc FROM audit_events WHERE action = 'User.BreakGlassReset' ORDER BY occurred_at_utc DESC LIMIT 1;` shows the row with your reason.
5. The user changes the temporary password via `Account → change password`.

## Preventing the lockout in the first place

The most robust mitigation is organizational: **provision at least two Owner
accounts before go-live** so admins can reset each other through the normal Users
screen, keeping `recover-admin` as the true last resort.
