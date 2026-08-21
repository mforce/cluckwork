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
start or end with a hyphen. It is immutable during this phase, so have a second
person verify it before continuing.

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
  --timezone America/Los_Angeles \
  --locale en-US \
  --currency USD
```

Pass the verb only: the image entrypoint already supplies
`dotnet Cluckwork.Api.dll`. The timezone, locale, and currency flags default to
`UTC`, `en-US`, and `USD`, but production provisioning should state them
explicitly.

The command echoes the normalized farm code before any write; runtime warnings
may appear before that line. On success it exits `0`, prints the new account id
and Owner email, then prints one temporary password. The password goes to
stdout, which a host log collector may capture; handle it as a secret and
deliver it to the Owner out of band.

## Verify

1. Run `list-accounts` again and confirm the new code is `active`.
2. Sign in using the new farm code, Owner email, and printed password.
3. Confirm the SPA shows **Set your password** and blocks every other screen.
4. Set a permanent password and confirm the farm opens normally.

## If it fails

| Symptom | Action |
|---|---|
| `Account.SlugInvalid` | Correct the code; uppercase is rejected rather than folded. Nothing was written. |
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

## Drill

Safe on a scratch database only.

1. Migrate a scratch database and run the command with a DML-only role.
2. Verify all four postconditions above.
3. Simulate lost output by rerunning the identical command; expect
   `Provision.SlugTakenRecoverable` and an account-specific recovery command.
4. Run that recovery command, verify the replacement password signs in, and
   confirm the original password no longer does.
5. Update **Last drilled** above.
