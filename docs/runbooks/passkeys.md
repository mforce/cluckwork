# Runbook: passkeys (`Passkeys:Origins`, `clear-passkeys`)

**When to use this:** you are about to turn passkeys on, turn them off, or move
the site to a new address; or a serving start logged `Passkeys.OffWithPasskeys`;
or a user lost every passkey and cannot sign in.

**Not this runbook:** an Owner who lost the password or the authenticator app,
with no passkey involved: see
[Break-glass account recovery](break-glass-account-recovery.md).

**Blast radius:** `clear-passkeys` removes every passkey in its scope and ends
every session and connected app of each user who had one. Those users sign in
again with their password.

**Prerequisites:** shell access to the deployment, and the database role the
one-shot verbs run as. That role needs `SELECT`, `INSERT`, `UPDATE` and `DELETE`
on `"AspNetUserPasskeys"` and `passkey_ceremonies`. A role created before the
`AddPasskeyStorage` migration does not get them automatically, unless its
default privileges cover new tables. Grant them before you run `recover-admin`
or `clear-passkeys`:

```sql
GRANT SELECT, INSERT, UPDATE, DELETE ON "AspNetUserPasskeys", passkey_ceremonies TO <runtime_role>;
```

**Last drilled:** not recorded.

---

## Release gate: keep passkeys off for now

Keep `Passkeys:Origins` unset on every deployment until #1284, #1285 and #1443
have all shipped (the maintainer's decision of 2026-11-18). Before that,
passkeys only half-work: a user can register a passkey that does nothing at
sign-in, or a passkey replaces the password at sign-in while step-up still
accepts the password alone. No code enforces this gate; this runbook is where it
is stated.

## How `Passkeys:Origins` works

`Passkeys:Origins` lists the origins passkeys work at, for example
`Passkeys__Origins__0=https://farm.example`. Unset or empty means passkeys are
off. A serving start refuses an entry that:

- is not an origin: a scheme, a host and an optional port, with no path, no
  trailing `/`, no query, fragment or user name, and no `*`;
- does not use `https` (`http://localhost` is allowed in Development only);
- names a host that `AllowedHosts` does not list.

It also refuses a single value where the list belongs, such as
`Passkeys__Origins=https://farm.example`: that binds no origin, so it would
leave passkeys silently off. Number the entries from 0.

A passkey works only at the host it was made on. A request whose host is not the
host of a configured origin gets 404 `Passkeys.Off` from every route that runs a
passkey ceremony. The passkey list, renaming and removing still work while
passkeys are off.

A passkey still counts as its user's factor while passkeys are off. So, while
passkeys are off and passkeys are stored, every serving start logs one warning,
`Passkeys.OffWithPasskeys`, with the count, naming `clear-passkeys`.

## Procedure

### 1. Before you turn passkeys off or move the site: clear them

Turning passkeys off, or moving the site to a new address, leaves every
registered passkey unusable. Clear them first, for one farm or for all:

```bash
dotnet Cluckwork.Api.dll clear-passkeys --farm <farm code> --reason "moving to farm.example"
dotnet Cluckwork.Api.dll clear-passkeys --all --reason "turning passkeys off"
```

Expected, one line per farm:

```
Farm '<farm code>': removed the passkeys of 3 user(s); their sessions and connected apps ended.
```

> **Destructive.** It deletes the passkeys and signs the affected users out
> everywhere. This cannot be undone: each user registers their passkeys again.

With neither `--all` nor `--farm`, or with both, the verb refuses and changes
nothing. An unknown farm code exits 1 and changes nothing. Each farm runs in its
own transaction, so a failure part way through `--all` leaves the farms before it
cleared.

### 2. Then change `Passkeys:Origins` and restart

Unset it to turn passkeys off, or set the new origin, then restart the serving
process. The start logs no `Passkeys.OffWithPasskeys` once step 1 cleared every
passkey.

### 3. One user lost every passkey

An Owner removes the user's passkeys with `DELETE /api/v1/users/{id}/passkeys`
and a step-up grant; the Users page offers it as **Remove passkeys** once the
screen ships. The user's
sessions and connected apps end, and their password works again. It works while
passkeys are off. An Owner cannot remove their own passkeys this way: another
Owner does it, or `recover-admin` does it for one Owner.

## Verify

- Every `User.PasskeysCleared` audit row names the actor `(clear-passkeys)`, the
  user's farm, the reason and the count.
- A restart logs no `Passkeys.OffWithPasskeys`.

## If it fails

- **`permission denied for table "AspNetUserPasskeys"`:** the role lacks the
  grants in the prerequisites. Grant them and run the verb again.
- **`No farm with code '…'`:** run `list-accounts` and use the code it prints.

## Drill

1. On a scratch database, register a passkey (or insert one with
   `PasskeyCount` set to match), with `Passkeys:Origins` unset.
2. Start the serving process and find one `Passkeys.OffWithPasskeys` warning.
3. Run `clear-passkeys --farm <code> --reason drill` and restart.
4. Expected end state: no passkey rows for that farm, one `User.PasskeysCleared`
   row per user who had one, and no warning at start.
