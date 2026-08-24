# Runbook: break-glass account recovery (`recover-admin`)

**Issue:** #265 · **Applies to:** any Cluckwork deployment · **Privilege required:** shell access to the running deployment (or its database)

**Last drilled:** not recorded — see [Post-recovery verification](#post-recovery-verification-drill-this-on-staging-before-you-need-it) and date it when you run it.

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

1. Finds the user by email — **case-insensitive**, matching the login path (across accounts; disambiguate with `--account` if ever ambiguous).
2. Sets a **freshly generated** strong temporary password (never one you pass on the command line).
3. **Rotates the security stamp and bumps the credential epoch** (#364) — invalidates Identity-derived state, and since every authenticated request is checked against the epoch, makes any access token already issued fail on its very next use.
4. **Clears any active lockout** and failed-attempt count — so a user locked out by repeated failed logins (the common trigger) can actually use the new password immediately, not after the 15-minute lockout expires.
5. **Revokes every refresh token** for that user — all existing sessions/devices are signed out.
6. Writes a conspicuous **`User.BreakGlassReset` audit row** carrying your `--reason` **and the host/OS-user that ran the command**.

> **Who that row names (#500).** `actor_email` is the literal
> **`(break-glass)`** and `actor_user_id` is all-zeroes. That is deliberate: this
> command exists precisely because no human is signed in, so it declares the
> non-person it is rather than falling back to the old `(unresolved)` placeholder
> — an audit event with no resolved actor is now refused outright. The real
> accountability for a break-glass reset is the `--reason` you pass plus the
> host and OS user captured in the row's details, which is why step 6 records
> both and why the verification below checks them.

> Since #364, an access token already issued is rejected on its very next
> request — it does not linger for the rest of its ~15-min lifetime. Refresh is
> dead immediately too, so the session cannot be extended.

Unlike `seed --profile demo|simulation`, this command is **NOT** environment-gated:
it is designed to run against a real **Production** database. Its safety comes
from requiring shell access to invoke it, plus the audit trail it leaves.

## Procedure

Run the API binary with the `recover-admin` verb on (or with network access to)
the deployment's database. The command **performs the reset, prints the
temporary password once, then exits** — Kestrel never starts. Unlike `seed`/
`bootstrap-admin`, it does **not** migrate the schema (#450): it's designed to
run under the app's least-privilege DML-only runtime credential, the same one
the serving process uses, not the higher-privileged migrator credential — so
it needs the separate `migrate` job to have already run (the normal #263
deploy ordering already guarantees this by the time an operator has a
locked-out account to recover). If you ever need to run recovery against a
database with genuinely pending migrations, run `migrate` first.

```bash
# Minimum: the target's email. --reason is strongly recommended (it lands in the audit row).
dotnet Cluckwork.Api.dll recover-admin \
  --email owner@thefarm.example \
  --reason "sole-owner lockout, ticket OPS-1234"

# If an email ever resolves to more than one account (dormant multi-tenant future):
dotnet Cluckwork.Api.dll recover-admin --email owner@thefarm.example --account <account-guid> --reason "..."
```

> **Ambiguous emails are now expected, not hypothetical (#532).** Since login is
> farm-scoped, one address can legitimately exist in several farms, and
> `recover-admin` looks across accounts. An ambiguous email is **refused**
> (`Recovery.Ambiguous`) — it never picks — so pass `--account <id>` to
> disambiguate. Find ids with the `list-accounts` command. The code already
> behaves this way; this note is documentation only.

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
Break-glass reset complete for owner@thefarm.example on farm <slug> (account 0000000a-...). All existing sessions were revoked.
Temporary password: <20-char one-time password>
Log in with this now and change it immediately (Account → change password).
```

The **farm code** before the account GUID is the required #532 sign-in input and
is printed nowhere else, so copy it from stdout along with the password.

The temporary password is written to **stdout only** (never the logger/OTLP), so
it does not persist in structured logs. Capture it from the terminal, hand it to
the user over a trusted channel, and have them rotate it on first login.

### Failure (exit code `1`)

A clear message is written to **stderr**, e.g.:

- `Recovery failed: Recovery.NotFound — Recovery '<email>' was not found.` — check the email.
- `Recovery failed: Recovery.Ambiguous — N users share the email ... pass --account <id>` — add `--account`.
- `Recovery failed: Recovery.UserDisabled — '<email>' was disabled at <time>. …` — the user was **disabled**
  (#356), not merely locked out. A password reset would not restore their access: a disabled user is refused
  before the password is ever checked. Have another Owner re-enable them from **Users → Enable**, then re-run
  this command if the password is still unknown. This command refuses rather than resetting precisely so it
  cannot hand you a credential that silently does not work; clearing the disabled flag from the CLI is not yet
  supported (tracked with the `--user-id` lookup in #357).

  **If EVERY Owner on the account is disabled, there is no CLI route back.** The application cannot produce that
  state — the last *active* Owner cannot be disabled, and that guard runs inside the account lock on both the
  disable and the demote path — but a hand-edited or restored database can arrive in it, and that is exactly the
  population this runbook serves. `bootstrap-admin` does **not** rescue it: it counts Owner role rows without
  excluding `DisabledAt`, so a disabled Owner still reads as "already provisioned" and it exits `0` having done
  nothing. Until #357 ships, recovery from that state is direct DB surgery, run against the account's database
  with an interactive `psql` session (or the deploy's own DB-access path — never paste connection strings into
  this document):

  ```sql
  -- 1. Find the account and the disabled Owner(s) on it. You have an EMAIL, not
  --    an id — that's the whole reason you're here — so start from NormalizedEmail,
  --    case-insensitively, exactly like the app's own lookup.
  SELECT "Id", "Email", "AccountId", "DisabledAt", "DisabledBy"
  FROM "AspNetUsers"
  WHERE "NormalizedEmail" = UPPER('owner@example.com');

  -- 2. Re-enable that row AND revoke everything a normal disable would have.
  --    This is a manual stand-in for BOTH DisableUserAsync's revocation and
  --    EnableUserAsync's flag-clear (IdentityProvider.cs) — never just the
  --    latter. You cannot assume DisabledAt on this row came from the app's
  --    own disable path: a hand-edited or restored database may have set it
  --    directly, without ever bumping CredentialEpoch, rotating the stamps,
  --    or revoking refresh tokens. Clearing DisabledAt alone would then
  --    immediately reactivate any access token still matching the current
  --    epoch, any still-active refresh token, and any outstanding step-up
  --    grant (#308) — exactly the credentials a normal disable exists to
  --    kill. Fail closed: do all of it, atomically (codex review of #492,
  --    round 6). gen_random_uuid() is native since Postgres 13 — no
  --    extension needed on this deploy's Postgres 18.
  BEGIN;

  UPDATE "AspNetUsers"
  SET "DisabledAt" = NULL,
      "DisabledBy" = NULL,
      "CredentialEpoch" = "CredentialEpoch" + 1,
      "SecurityStamp" = gen_random_uuid()::text,
      "ConcurrencyStamp" = gen_random_uuid()::text
  WHERE "Id" = '<owner-id-from-step-1>';

  UPDATE refresh_tokens
  SET "RevokedAt" = now()
  WHERE "UserId" = '<owner-id-from-step-1>' AND "RevokedAt" IS NULL;

  COMMIT;
  ```

  This is DB surgery, not the app's own audited path — it writes **no** `User.Enabled` audit row, unlike a normal
  re-enable (the refresh-token revocation above has no audit row of its own either way — `RevokeAllActiveForUserAsync`
  is a bulk update, not an audited event, even on the app's own disable path). Record what you did (who, when, why,
  which row) somewhere durable outside the database, the same way you would for any other manual production change.
  Sign in once you're done — with a
  **fresh** login, since every prior access and refresh token for this user is now dead by construction — and
  re-run this command if the password is also unknown.
- `Invalid --account '<x>' — must be a GUID.`

Nothing is changed on a failure.

## Post-recovery verification (drill this on staging before you need it)

1. The user logs in with the temporary password **and the printed farm code** → **succeeds**.
2. The **old** password → **rejected**.
3. Any previously open session (a still-loaded browser tab) can no longer refresh → signed out.
4. `SELECT action, actor_email, reason, occurred_at_utc FROM audit_events WHERE action = 'User.BreakGlassReset' ORDER BY occurred_at_utc DESC LIMIT 1;` shows the row with your reason.
5. The user changes the temporary password via `Account → change password`.

## Preventing the lockout in the first place

The most robust mitigation is organizational: **provision at least two Owner
accounts before go-live** so admins can reset each other through the normal Users
screen, keeping `recover-admin` as the true last resort.
