# Persist the Data Protection key ring in Postgres, encrypted in Production (#794)

> **Rule** — the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md);
> this file is the relocated rationale (what shipped, why the short version was
> insufficient, what not to break).

**Status:** accepted
**Date:** 2026-10-06

## What happened

No incident. This is a forward-looking choice. `AddDefaultTokenProviders()` registers
Identity's `DataProtectorTokenProvider`, which protects password-reset tokens with ASP.NET
Core Data Protection. Nothing configured Data Protection, so the framework kept its key ring
in a directory under the container user's home. A replaced container lost that ring, and a
second replica could not read a token the first had protected. Nothing failed because
`IdentityProvider` mints and redeems its reset token in the same call. A real forgot-password
flow, TOTP (#320) and possibly OpenIddict (#795) would each break the moment a token has to
cross a restart or a replica.

## The rule

A serving process stores the key ring in the `DataProtectionKeys` table through
`PersistKeysToDbContext<AppDbContext>()`, under the application name `Cluckwork`. In
Production it refuses to start unless `DataProtection:CertificatePem` and
`DataProtection:PrivateKeyPem` hold an RSA certificate and its private key. The framework
encrypts every key it writes with that certificate. One-shot verbs use an in-memory ring and
never read or write the table. Dropping the persistence brings back the restart and replica
failure. Dropping the Production guard lets anyone with a database backup forge every token
the ring protects.

## Why not the obvious alternative

- **Redis**, which #543 already provisions. It would make Redis auth-critical, and #543's
  in-process fallback would silently split the ring across replicas. A split ring is worse
  than a missing one because it fails only on some requests.
- **Plaintext keys in the table.** The table would become a token-forging credential in
  every backup and every read-only database account.
- **An opt-out flag**, like `Database:AllowInsecureConnection`. No current deployment shape
  needs plaintext keys, and `deploy/.env.example` gives the one `openssl req` command that
  makes a self-signed certificate. Add a flag when a real topology needs one.
- **Persisting in one-shot verbs too.** `recover-admin` must work with nothing but a
  connection string (#265, #331). Without the certificate it could not decrypt the stored
  keys, so the framework would create a new key and write it to the shared table in
  plaintext. Its token never leaves the process, so an in-memory ring is correct.
- **Configuring the ring inside `AddCluckworkIdentity`.** The ring serves every consumer of
  Data Protection, not only Access, so it registers in the Platform composition.

## What this does NOT cover

- **Certificate rotation.** There is no setting for a previous certificate. Replacing the
  certificate makes the stored keys unreadable. Outstanding tokens then fail validation, which
  fails closed, and the framework creates a new key. Add `UnprotectKeysWithAnyCertificate`
  configuration when rotation is needed.
- **Non-Production serving hosts without a certificate** (Development, Testing, the AppHost)
  store plaintext keys. The framework logs a warning.
- **Keys written before a certificate was configured** stay in the table in plaintext and stay
  readable.

## How it is enforced

- `ProcessRoleGuardTests` has three serving-only rows: certificate missing, unparseable, and a
  parseable non-RSA key. Each fails a Production serving boot and leaves `migrate` running.
  `ServingGuardCoverageTests` maps `EnsureKeyEncryptionCertificate` to those rows.
- `DataProtectionKeyRingTests` protects a payload on one host and unprotects it on a second
  host with its own service provider and a different default discriminator. It also asserts
  the single stored key is encrypted, and that a one-shot ring works with no database
  registered.
- `TableOwnerRealModelTests` requires the Platform row in `TableOwnerOverrides`, and
  `TenantBypassDiscoveryTests` pins `DataProtectionKey` in the filter-free set.
- `tools/simulation/verify-harness.sh` rejects a missing or placeholder certificate before the
  sim stack boots.
