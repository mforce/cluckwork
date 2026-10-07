# Persist the Data Protection key ring in Postgres, encrypted in Production (#794)

> **Rule** — the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md);
> this file is the relocated rationale (what shipped, why the short version was
> insufficient, what not to break). The operator procedures are in the
> [runbook](../runbooks/data-protection-key-ring.md).

**Status:** accepted
**Date:** 2026-10-06, amended 2026-10-07 (plaintext-key refusal, certificate expiry, revocation)

## What happened

No incident. This is a forward-looking choice. `AddDefaultTokenProviders()` registers
Identity's `DataProtectorTokenProvider`, which protects password-reset tokens with ASP.NET
Core Data Protection. Nothing configured Data Protection, so the framework kept its key ring
in a directory under the container user's home. A replaced container lost that ring, and a
second replica could not read a token the first had protected. Nothing failed because
`IdentityProvider` mints and redeems its reset token in the same call. A real forgot-password
flow, TOTP (#320) and possibly OpenIddict (#795) would each break the moment a token has to
cross a restart or a replica.

Review of the first version found that the certificate only encrypts keys as they are
written. The framework still reads a key stored in plaintext and will use it, so a
Production database that held one key written by a host without the certificate was
forgeable from a backup despite the guard. The owner chose to refuse such a start.

## The rule

A serving process stores the key ring in the `DataProtectionKeys` table through
`PersistKeysToDbContext<AppDbContext>()`, under the application name `Cluckwork`. In
Production it refuses to start unless `DataProtection:CertificatePem` and
`DataProtection:PrivateKeyPem` hold an RSA certificate and its private key, and it refuses
to start while the table holds any key stored without encryption. Production uses its own
database. One-shot verbs use an in-memory ring and never read or write the table. Dropping
the persistence brings back the restart and replica failure. Dropping either Production
guard lets anyone with a database backup forge every token the ring protects.

## Why not the obvious alternative

- **Redis**, which #543 already provisions. It would make Redis auth-critical, and #543's
  in-process fallback would silently split the ring across replicas. A split ring is worse
  than a missing one because it fails only on some requests.
- **Plaintext keys in the table.** The table would become a token-forging credential in
  every backup and every read-only database account.
- **Trusting the certificate alone.** `ProtectKeysWithCertificate` installs a write-side
  encryptor; on read, the framework returns an unencrypted key element unchanged. Only a
  check of the stored rows makes "every key is encrypted" true.
- **Checking in `ServingBootGuards`.** That runs before `ValidateOnStart`, so a database read
  there would fire ahead of the upload-cap guards and hide them behind a connection error.
  `PlaintextDataProtectionKeyGuard` runs in `IHostedLifecycleService.StartingAsync`, after
  options validation and before any hosted service starts, so before Kestrel binds and
  before the framework loads the ring.
- **An opt-out flag**, like `Database:AllowInsecureConnection`. No current deployment shape
  needs plaintext keys, and `deploy/.env.example` gives the one `openssl req` command that
  makes a self-signed certificate. Add a flag when a real topology needs one.
- **Persisting in one-shot verbs too.** `recover-admin` must work with nothing but a
  connection string (#265, #331). Without the certificate it could not decrypt the stored
  keys, so the framework would create a new key and write it to the shared table in
  plaintext. Its token never leaves the process, so an in-memory ring is correct.
- **Configuring the ring inside `AddCluckworkIdentity`.** The ring serves every consumer of
  Data Protection, not only Access, so it registers in the Platform composition.

## Certificate lifetime and replacement

Measured by `DataProtectionCertificateExpiryTests`, against the framework directly:

- **An expired certificate still works.** A certificate past its NotAfter date encrypts new
  keys and decrypts stored ones. Nothing checks its chain or dates. Its lifetime is not a
  deadline.
- **Reissuing on the same key pair is a rotation.** The reissued certificate cannot read keys
  the original encrypted (`Unable to retrieve the decryption key.`). Any certificate change
  makes every stored key unreadable. Outstanding tokens then fail closed and the framework
  creates a new key.

There is no setting for a previous certificate yet, so a planned replacement costs the
same outstanding tokens as an emergency one.

## Fixing a refused start

The boot message names how many keys are plaintext and their key ids, never key material.
Stop every serving instance, delete those rows, and start again. Tokens they protected then
fail closed. The [runbook](../runbooks/data-protection-key-ring.md#b-production-refuses-to-start-plaintext-keys)
has the SQL and the checks.

## Revoking the ring when the private key leaks

Anyone holding the private key and any copy of the database can decrypt every stored key.
Revoke the whole ring. Never keep reading with the old certificate.

1. Issue a new certificate on a new key pair.
2. Stop every serving instance, so none writes a key with the leaked certificate.
3. Delete every row in `DataProtectionKeys`.
4. Supply the new certificate and key as secrets through configuration, remove the leaked
   ones, and start. The startup log line names the new certificate's SHA-256 fingerprint.
5. Treat backups taken before step 3 as compromised. Restoring one brings the revoked keys
   back, so repeat step 3 after any such restore.

Every outstanding protected token stops validating. This procedure has not been drilled
yet; the runbook records when it is.

## What this does NOT cover

- **Planned rotation with an overlap window.** Supporting the previous certificate for
  decryption (`UnprotectKeysWithAnyCertificate`) is a follow-up.
- **A key written into the Production database after startup.** The plaintext check runs at
  host start only. A non-Production host writing into the Production database is caught at
  the next start, which is why Production must use its own database.
- **Non-Production serving hosts without a certificate** (Development, Testing, the AppHost)
  store plaintext keys. The framework logs a warning.
- **A Production start with the database unreachable.** The plaintext check reads the table,
  so that start fails instead of booting and reporting unready. A start with migrations
  pending still boots. The check skips a missing table and `/health/ready` reports it (#263).

## How it is enforced

- `ProcessRoleGuardTests` has four serving-only rows: certificate missing, unparseable, a
  parseable non-RSA key, and a stored plaintext key. Each fails a Production serving boot and
  leaves `migrate` running. `ServingGuardCoverageTests` maps `EnsureKeyEncryptionCertificate`
  and `EnsureNoPlaintextDataProtectionKeysAsync` to those rows.
- `DataProtectionKeyRingTests` protects a payload on one host and unprotects it on a second
  host in the same process, with its own service provider and a different default
  discriminator. It asserts the single stored key is encrypted and that the plaintext check
  does not flag it. It also asserts that a one-shot ring round-trips with no database and
  that a second one-shot ring cannot read it.
- `MigrateOnStartupDisabledTests` boots Production against an unmigrated database, which
  covers the plaintext check's missing-table skip.
- `DataProtectionCertificateExpiryTests` pins the two certificate findings above.
- `TableOwnerRealModelTests` requires the Platform row in `TableOwnerOverrides`, and
  `TenantBypassDiscoveryTests` pins `DataProtectionKey` in the filter-free set. The
  plaintext check's two queries carry `BypassAllowList` and `FilterFreeSetSites` rows.
- `tools/simulation/verify-harness.sh` rejects a missing or placeholder certificate before the
  sim stack boots.
