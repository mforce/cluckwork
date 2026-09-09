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
start or end with a hyphen. No endpoint and no Settings field changes it, so a
farm cannot rename itself — but an operator can, on any farm, with the
`rename-account` verb (#732); see
[renaming a farm's code](#renaming-a-farms-code) below. Renaming is a
deliberate, announced change rather than a cheap undo, so still have a second
person verify the code before continuing.

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

## Renaming a farm's code

**When to use this:** a farm's code has to change. The commonest case is repair:
a database provisioned before multi-farm tenancy (release `v0.0.4` or earlier)
was upgraded, and the migration `20260818235944_AddAccountSlug` stamped the
pre-existing account with the documented code `default-farm`. Nothing asks for a
code at migration time, so a farm that wants its own code gets it from the
`rename-account` verb.

**Any farm may be renamed**, not only that upgraded one — the verb does not
distinguish them, and neither does the domain. What it is either way is an
operator-controlled rebrand: use it to repair a farm still carrying
`default-farm`, or to carry out a deliberate, announced rebrand of a farm
`provision-account` created. There is no self-service path and no undo.

**Blast radius:** one row in `Accounts`. Nothing else stores the code. Refresh
cookies and access tokens bind to the account **id**, so every signed-in user
stays signed in; the verb says so on success. Two SPA caches are cosmetic and do
not clear themselves. An explicit sign-in with the new code prepends it to that
device's remembered farm-code list and refreshes the per-farm palette cache under
the new key; the OLD remembered code stays in the list until the user picks
Forget, and offering it returns `Auth.UnknownFarmCode` unless another farm has
since reused it. Anything outside the app that names the code is stale the moment
the change commits: printed material, and every bookmarked `?farm=<old>` URL.

**A code a farm has moved off is immediately reusable.** There is no
retired-code list, so `--slug` names whoever holds that code *now*, which may
not be the farm you meant last week. Run `list-accounts` immediately before you
rename, and read its output rather than your notes.

**Tell every user the new code before it lands.** The sign-in form still offers
the old code and every bookmarked `?farm=<old>` link stops working at commit
time, so an unannounced rename looks to a user like the farm disappeared.

**Prerequisites:** the migration job has run and `list-accounts` shows the farm.
The ordinary DML-only runtime credential is enough; this needs no migrator role.

### Procedure

1. List the farms and copy the code exactly as it is stored:

   ```bash
   docker run --rm --env-file <runtime-credential.env> \
     ghcr.io/mforce/cluckwork@sha256:<digest> \
     list-accounts
   ```

2. Rename it. The `--reason` text is stored on the audit row, so give it the
   change reference somebody will search for later:

   ```bash
   docker run --rm --env-file <runtime-credential.env> \
     ghcr.io/mforce/cluckwork@sha256:<digest> \
     rename-account \
     --slug <current> \
     --new-slug <new> \
     --reason "<change ref>"
   ```

   The current code is matched case-insensitively. The new code is **not**
   folded: it must already be lowercase letters, digits and hyphens, 3–32
   characters, no leading or trailing hyphen, and not one of the nine reserved
   names. The verb refuses anything else before it writes.

3. List again and confirm the new code is the one you meant.

Unlike the procedure this section replaced, the rename goes through the domain:
it validates the code, bumps `Version` itself, and writes an `Account.Rename`
audit row carrying the old and new codes, the machine, the operating-system
user and your `--reason`. Nothing needs recording by hand.

### Verify

1. Sign in with the new code.
2. Check what the old code does now, and read `list-accounts` before you judge it.
   If no farm has reused it, signing in with it returns `Auth.UnknownFarmCode`.
   If another farm has taken it — there is no retired-code list — `list-accounts`
   names that holder, and a successful sign-in with that code
   authenticates that holder. That is the reuse working, not the rename
   failing.
3. Confirm an already-signed-in session still works without a new login.

### If it fails

The verb exits `1` and prints one line naming the error code:

- `Account.SlugInvalid` — the new code breaks the pattern or is reserved.
  Nothing was written. Choose another and re-run.
- `Account.SlugTaken` — another farm already holds that code. Codes are unique
  across every farm on the deployment. Nothing was written.
- `Account.SlugStale` — the farm's code changed between this command reading it
  and locking the row, so somebody else renamed it first. Nothing was written:
  re-run `list-accounts` and start again from the code it has now.
- `No farm with code '<code>'.` — no farm holds the code you passed to
  `--slug`. This one carries no error code: the verb resolves the code before
  it reaches the domain, and prints the same line `suspend-account` and
  `reactivate-account` print. Check `list-accounts`; remember a retired code
  may now belong to another farm.


## Provisioning drill

This drills `provision-account`, not `rename-account`: it rehearses creating a
farm and recovering a lost one-time password, and the rename procedure above has
no drill of its own. Safe on a scratch database only.

1. Migrate a scratch database and run the command with a DML-only role.
2. Verify all four postconditions above.
3. Simulate lost output by rerunning the identical command; expect
   `Provision.SlugTakenRecoverable` and an account-specific recovery command.
4. Run that recovery command, verify the replacement password signs in, and
   confirm the original password no longer does.
5. Update **Last drilled** above.
