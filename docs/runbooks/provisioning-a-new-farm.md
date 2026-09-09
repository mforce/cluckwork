# Runbook: provisioning a new farm (`provision-account`)

**Issue:** #533 · **When to use this:** add a new farm to an already-migrated
Cluckwork database and create that farm's first Owner.

**Not this runbook:** the default farm exists but has no Owner; use
[first admin provisioning](first-admin-provisioning.md). A farm already exists
but its Owner lost the one-time password; use
[break-glass account recovery](break-glass-account-recovery.md).

**Blast radius:** creates one account, ten egg grades, six packed-unit
conversions, one Owner, and their audit rows. They commit together or not at all.

**Prerequisites:** the schema is already current, the published image is the
same release as the serving API, and the command receives the ordinary DML-only
runtime database credential. It does not require the migrator role and should
not be given migrator credentials.

**Last drilled:** not recorded.

---

## Procedure

### 1. Choose and verify the farm code

The code is lowercase letters, digits, and hyphens; 3–32 characters; and cannot
start or end with a hyphen. It is immutable during this phase — no verb,
endpoint or Settings field changes it — so have a second person verify it
before continuing. The one operator-level exception is
[renaming the default farm's code](#renaming-the-default-farms-code) below.

```bash
docker run --rm --env-file <runtime-credential.env> \
  ghcr.io/mforce/cluckwork@sha256:<digest> \
  list-accounts
```

Expected: the chosen code is absent. Host-specific image references, network
arguments, and env-file paths belong in the deployment repo.

### 2. Provision the farm

```bash
docker run --rm --env-file <runtime-credential.env> \
  ghcr.io/mforce/cluckwork@sha256:<digest> \
  provision-account \
  --name "Example Farm" \
  --slug example-farm \
  --owner-email owner@example.com \
  --locale en-US \
  --currency USD \
  --timezone Asia/Manila
```

Pass the verb only: the image entrypoint already supplies
`dotnet Cluckwork.Api.dll`. Locale, currency and timezone default to `en-US`,
`USD` and `UTC`, but production provisioning should state them explicitly.

State `--timezone` when you know the farm's zone: every date the farm sees
depends on it, so a farm left in `UTC` records its first day of data against
the wrong zone until its Owner reaches **Settings**. The value must be an IANA
id such as `Asia/Manila` — an abbreviation like `PST` is rejected — and it is
checked before anything is written, so a bad zone fails the command instead of
committing a farm whose dates will not render. Omit the flag and the farm
starts in `UTC` exactly as before; the Owner can change it in **Settings**
either way.

The command echoes the normalized farm code before any write; runtime warnings
may appear before that line. On success it exits `0`, prints the new account id
and Owner email, then prints one temporary password. The password goes to
stdout, which a host log collector may capture; handle it as a secret and
deliver it to the Owner out of band.

## Verify

1. Run `list-accounts` again and confirm the new code is `active`.
2. Sign in using the new farm code, Owner email, and printed password.
3. Confirm the SPA shows **Set your password** and blocks every other screen.
4. Set a permanent password, confirm the farm's timezone in **Settings** (set
   it there if `--timezone` was omitted), and confirm the farm opens normally.

## If it fails

| Symptom | Action |
|---|---|
| `Account.SlugInvalid` | Correct the code; uppercase is rejected rather than folded. Nothing was written. |
| `Provision.TimeZoneInvalid` | Not a known IANA zone. Use a full id such as `Asia/Manila`, not an abbreviation like `PST`. Nothing was written. |
| `Provision.SlugTaken` | Choose a different code. Do not assume the similarly named farm is this attempted provision. |
| `Provision.SlugTakenRecoverable` | The account and matching Owner already committed, but the printed password may have been lost. Run the exact `recover-admin --email <email> --account <guid> --reason <reason>` command in the error. Do not rerun provisioning with another code. |
| `Provision.SlugTakenSuspended` | Run `reactivate-account --slug <farm-code>` first, then recover the Owner if needed. |
| `Provision.SlugTakenOwnerDisabled` | Have another active Owner re-enable that user. If none exists, escalate; `recover-admin` refuses disabled users. |
| `permission denied` or a missing table | The runtime grants or migration state are wrong. Do not switch casually to a schema-owner credential; verify the migration job and runtime-role grants. |

## Crash after commit

If the terminal disconnects after the farm commits but before the password is
captured, rerun the identical command. It exits `1` with
`Provision.SlugTakenRecoverable` and prints the account-specific recovery
command. Run that `recover-admin` command to mint a new one-time password; it
revokes the lost credential and records the reason.

## Renaming the default farm's code

**When to use this:** a database provisioned before multi-farm tenancy
(release `v0.0.4` or earlier) was upgraded, and the migration
`20260818235944_AddAccountSlug` stamped the pre-existing account with the
documented code `default-farm`. Nothing asks for a code at migration time, and
no verb changes one afterwards, so a farm that wants its own code gets it by a
direct database write.

**Not this runbook:** a farm created by `provision-account`. Its code was
chosen on purpose; treat it as immutable.

**Blast radius:** one row in `Accounts`. Nothing else stores the code:
refresh cookies and access tokens bind to the account **id**, so every signed-in
user stays signed in. The write bypasses the domain, so it bumps `Version` by
hand and leaves **no audit row** — record the change in the deployment repo's
change log instead.

**Prerequisites:** the migration job has run (`list-accounts` shows the
account), and you hold a credential with `UPDATE` on `Accounts`. The ordinary
DML-only runtime credential is enough; do not use the migrator role.

### 1. Choose and verify the new code

Same rules as step 1 of the procedure above: lowercase letters, digits and
hyphens; 3–32 characters; no leading or trailing hyphen. Uppercase is not
folded by the database write, so a mistyped code here is a code nobody can
sign in with. Nine names are reserved by the domain and refused by
`provision-account`; the database write checks nothing, so refuse them
yourself: `api`, `admin`, `www`, `health`, `app`, `static`, `assets`, `login`,
`auth`.

```bash
docker run --rm --env-file <runtime-credential.env> \
  ghcr.io/mforce/cluckwork@sha256:<digest> \
  list-accounts
```

Expected: exactly one account carries `default-farm`, and the chosen code is
absent. Have a second person verify the code; it appears in `?farm=<code>`
URLs and on printed material.

### 2. Rename

The `WHERE` names both the id and the current code, so the statement matches
nothing if the account was already renamed or if the id is not the default
account. Save it as `rename-farm.sql`:

```sql
UPDATE "Accounts"
SET "Slug" = 'example-farm',
    "Version" = "Version" + 1
WHERE "Id" = '0000000a-0000-0000-0000-000000000001'
  AND "Slug" = 'default-farm';
```

The Cluckwork image ships no `psql`, so run it through a Postgres client.
With the reference compose stack, the `db` service's own image has one, and
its `POSTGRES_USER` / `POSTGRES_DB` are already in that container's
environment:

```bash
docker compose -f deploy/docker-compose.yml exec -T db \
  sh -c 'psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"' \
  < rename-farm.sql
```

Against a managed Postgres, run a throwaway client container with the same
image the stack pins (`deploy/docker-compose.yml`, service `db`) and a
connection URL that carries the same TLS parameters the API uses:

```bash
docker run --rm -i \
  postgres:18.4-trixie@sha256:3a82e1f56c8f0f5616a11103ac3d47e632c3938698946a7ad26da0df1334744a \
  psql -v ON_ERROR_STOP=1 "$DATABASE_URL" < rename-farm.sql
```

Keep the URL in an env var or a file, never on the command line of a shared
host: `ps` shows it to every user. Host-specific network flags belong in the
deployment repo.

Expected: `UPDATE 1`. An `UPDATE 0` means the guard did not match; run
`list-accounts` again before changing anything. A unique-violation error on
`IX_Accounts_Slug` means the code is already taken; choose another.

### 3. Verify

1. Run `list-accounts` and confirm the account now shows the new code and is
   `active`.
2. Sign in with the new farm code. A browser that remembers only
   `default-farm` prefills it, so overtype it once; the new code is remembered
   from then on, and the old one can be removed from the login form's picker.
3. Confirm `default-farm` no longer signs in (`Auth.UnknownFarmCode`).

Existing sessions keep working without a new login. Two SPA caches still name
the old code until the user next signs in: the remembered farm code on the
login form, and the per-farm palette cache. Both are cosmetic and refresh on
that login; nothing needs clearing.

Tell every user the new code before the change lands. Any printed material or
bookmarked `?farm=default-farm` URL is stale from the moment the statement
commits.

## Drill

Safe on a scratch database only.

1. Migrate a scratch database and run the command with a DML-only role.
2. Verify all four postconditions above.
3. Simulate lost output by rerunning the identical command; expect
   `Provision.SlugTakenRecoverable` and an account-specific recovery command.
4. Run that recovery command, verify the replacement password signs in, and
   confirm the original password no longer does.
5. Update **Last drilled** above.
