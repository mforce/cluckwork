# Persist the Data Protection key ring in Postgres, encrypted in Production (#794)

> **Rule** — the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md);
> this file is the relocated rationale (what shipped, why the short version was
> insufficient, what not to break). The operator procedures are in the
> [runbook](../runbooks/data-protection-key-ring.md).

**Status:** accepted
**Date:** 2026-10-06, amended 2026-10-07 (plaintext-key refusal, certificate expiry, revocation)
and 2026-11-12 (previous certificates, #1338; re-wrapping stored keys, #1420)

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
database. One-shot verbs use an in-memory ring and never load the stored one.
`rewrap-key-ring` is the one verb that reads and rewrites the table's rows (#1420). Dropping
the persistence brings back the restart and replica failure. Dropping either Production
guard lets anyone with a database backup forge every token the ring protects. A serving
process may also list previous certificates, which only decrypt (#1338).

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
  the original encrypted (`Unable to retrieve the decryption key.`). A certificate change
  that does not list the old certificate makes every stored key unreadable. Outstanding
  tokens then fail closed and the framework creates a new key.

## Previous certificates (#1338)

`DataProtection:PreviousCertificates` lists the certificates the ring used before the
current one, each as a `CertificatePem` and `PrivateKeyPem` pair. A serving process passes
them to `UnprotectKeysWithAnyCertificate`. The framework (ASP.NET Core 10 source) adds
them to its decryption options only. It matches a stored key to a certificate by
thumbprint and needs that certificate's private key. It checks no dates. Only
`ProtectKeysWithCertificate` sets the encryptor, so a listed certificate never encrypts.
The maintainer chose this on 2026-11-12 ("Ok start 1338 with your recommendations"), so
a planned certificate change costs users nothing.

- **A list, not one pair.** A second change made before the first certificate is retired
  still needs it.
- **Boot refusals.** A serving start refuses an entry that does not parse, has no
  matching private key, is not RSA, or has the current certificate's thumbprint. The
  framework silently skips a certificate without its key, so that entry would boot and
  then fail every read. A renewal on the same key pair has a different thumbprint and
  boots. An expired entry boots, because the framework does not check dates.
- **Outside Production** a list with no current certificate is accepted. It only reads,
  and new keys there are stored in plaintext anyway.
- **Not for a leaked key.** A leak still revokes the whole ring, and the leaked
  certificate is never listed.
- **Retiring a previous certificate.** Two-factor authenticator keys are encrypted once,
  at enrolment, and users who enrol up to about 90 days after the change still get a key
  the old certificate encrypted. Removing an entry before the keys are re-encrypted costs
  what an unplanned change costs. Re-wrapping (below) is the way to retire it.

## Re-wrapping stored keys (#1420)

`rewrap-key-ring` re-encrypts every stored key under the current certificate, so a
previous certificate can leave the list with no user action. The maintainer settled its
shape on 2026-11-12, and two design reviews tightened it before it was built.

- **What it rewrites.** Only the `EncryptedData` under each `encryptedSecret`, through the
  framework's public `EncryptedXmlDecryptor` and `CertificateXmlEncryptor`. Key ids, dates,
  descriptors, `decryptorType` and the key material stay as stored, so every payload the
  ring protects stays readable. Current, expired and revoked keys are all rewritten.
  Revocation records hold no key and stay byte-identical.
- **Why a verb reads the table.** The framework has no re-wrap. Its key manager caches keys
  by id and its repository only inserts rows, so re-encrypting means rewriting the rows.
  The verb never loads the stored ring: it decrypts each element itself, with only the
  configured certificates.
- **Every row is decrypted on every run.** The embedded certificate decides only whether to
  write. A run without a certificate listed therefore proves that no row still needs it,
  which a fingerprint listing cannot: a row can name one certificate and be encrypted to
  another.
- **Configured recipients only.** `EncryptedXml` falls back to the operating system's
  certificate store. Before it decrypts, the verb requires the shape the encryptor writes
  (one `EncryptedKey` for one `X509Certificate`) and a recipient that is the current or a
  listed certificate.
- **The decryptor type** is compared by type name, not assembly version, as the framework
  resolves it: a ring an older framework wrote names another version. Any other type
  refuses, because the encryptor would then wrap a blob the stored type cannot read.
- **The server's configuration and refusals.** `EnsureKeyEncryptionCertificate` and
  `EnsurePreviousCertificates` run as at a serving start. A missing current certificate
  refuses in every environment.
- **All or nothing.** Every refusal comes before one `SaveChanges`, which is one
  transaction. There is no explicit transaction: `EnableRetryOnFailure` refuses one opened
  outside an execution strategy.
- **Online, after the serving instances run with the new certificate.** A running instance
  keeps the keys it has read, unchanged in key material, and inserts new keys under the
  current certificate. The check run reads only the rows that exist when it runs, so it
  cannot prove the restart happened; the runbook makes that a prerequisite.
- **No audit row.** `AuditWriter` needs a resolved farm, and the ring belongs to none. The
  verb prints counts and fingerprints and logs one line with the same. It never prints XML
  or an exception message from the decrypt path.
- **Old backups.** Re-wrapping changes no key material and cannot reach a backup. An old
  private key stays as protected as the current one until the last backup taken before the
  re-wrap is destroyed. Restoring such a backup needs the old certificate listed again.
  A leak still revokes the whole ring (procedure C).

## What counts as plaintext

A stored record is plaintext when it shows any `masterKey` element, anywhere. A
certificate-encrypted record carries only ciphertext, never a visible `masterKey`, so
nothing legitimate is caught. The framework's descriptor deserializer reads `masterKey`
whatever its `requiresEncryption` marker says, so the marker cannot decide. The first
version trusted it, and a key with the marker removed or altered passed (review round 2).
No element name proves encryption either. The framework's `NullXmlEncryptor` writes an
`encryptedSecret` wrapper around a clear master key. The framework also hands a key's
descriptor child to the deserializer without checking its name, so a plaintext descriptor
renamed `EncryptedData` still exposed a usable key. The second version exempted that
name and passed it (review round 3). Revocation records hold no master key and pass.

## Fixing a refused start

The boot message names how many keys are plaintext and their key ids, never key material.
Stop every serving instance, delete those rows, and start again. Tokens they protected then
fail closed. The [runbook](../runbooks/data-protection-key-ring.md#b-production-refuses-to-start-plaintext-keys)
has the SQL and the checks.

## Revoking the ring when the private key leaks

This applies to the private key of any certificate that encrypted a key still in the table
or in a backup: the current certificate, a listed previous one, or one removed from the
list. Removing an entry does not revoke its certificate. Anyone holding a leaked key and any
copy of the database can decrypt every stored key it encrypted. Revoke the whole ring. Never
keep reading with the leaked certificate.

1. Issue a new certificate on a new key pair.
2. Stop every serving instance. A running instance holds the ring in memory: it would keep
   protecting new payloads with keys that step 3 deletes, and if the current certificate
   leaked, it could write a new key encrypted with it.
3. Delete every row in `DataProtectionKeys`.
4. Supply the new certificate and key as secrets through configuration, remove the leaked
   ones, delete every previous-certificate entry (a blanked entry stops the boot), and
   start. The startup log line names the new certificate's SHA-256 fingerprint.
5. Treat backups taken before step 3 as compromised. Restoring one brings the revoked keys
   back, so repeat step 3 after any such restore.

Every outstanding protected token stops validating. This procedure has not been drilled
yet; the runbook records when it is.

## What this does NOT cover

- **Backups taken before a re-wrap.** They still hold keys encrypted with the old
  certificates. Keep each old private key protected until the last such backup is
  destroyed. Restoring one needs the old certificate listed again and the runbook's D.4
  repeated.
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
  leaves `migrate` running. `ServingGuardCoverageTests` maps `EnsureKeyEncryptionCertificate`,
  `EnsureUsableCertificate` and `EnsureNoPlaintextDataProtectionKeysAsync` to those rows.
- `DataProtectionKeyRingTests` protects a payload on one host and unprotects it on a second
  host in the same process, with its own service provider and a different default
  discriminator. It asserts the single stored key is encrypted and that the plaintext check
  does not flag it. It also asserts that a one-shot ring round-trips with no database and
  that a second one-shot ring cannot read it.
- `PlaintextDataProtectionKeyClassifierTests` runs the classifier over XML the framework
  wrote: a plaintext key, the same key with its marker removed or altered five ways, its
  descriptor renamed `EncryptedData` in three namespaces, a certificate-encrypted key, an
  encrypted key with an unmarked plaintext sibling, a key behind `NullXmlEncryptor`, and a
  revocation record.
- `MigrateOnStartupDisabledTests` boots Production against an unmigrated database, which
  covers the plaintext check's missing-table skip.
- `DataProtectionCertificateExpiryTests` pins the two certificate findings above.
- `DataProtectionPreviousCertificateTests` stores a key under an expired certificate A,
  then starts a host with certificate B current and A listed. That host unprotects A's
  payload and encrypts a new key with B and not A. A host with C current, A at index 0
  and B at index 2 reads payloads under both. A renewal on A's key pair boots with A
  listed and reads A's payload. A host without A cannot read the payload.
- `ProcessRoleGuardTests` has five more serving-only rows, one for each refusal of a
  previous entry: an unparseable certificate, a blank key, a mismatched key, a non-RSA key,
  and the current certificate. Every other row runs with a valid expired previous
  certificate listed, the plaintext-key row included. `ServingGuardCoverageTests` maps them
  to `EnsureUsableCertificate` and `EnsurePreviousCertificates`.
- `DataProtectionKeyRewrapTests` runs `rewrap-key-ring` as an operator does, against
  Postgres. Five keys under an expired certificate A (current, expired, revoked, one naming
  `Version=8.0.0.0` types, one with two encrypted elements) and a revocation record are
  re-wrapped to B. A host with B alone reads every key and an A-era payload, everything
  outside the ciphertext is unchanged, and no output names `masterKey`. A rerun writes
  nothing, and a run with no previous certificate passes. A forced failure on the second
  update leaves every row unchanged. A row under an unlisted certificate, a row that names
  B but is encrypted to A, a recipient only the certificate store holds, every refused row
  shape, a missing current certificate and a refused previous entry each exit 1 with every
  row unchanged. The plaintext check on the rewritten XML has no test that reaches it.
- `TableOwnerRealModelTests` requires the Platform row in `TableOwnerOverrides`, and
  `TenantBypassDiscoveryTests` pins `DataProtectionKey` in the filter-free set. The
  plaintext check's two queries carry `BypassAllowList` and `FilterFreeSetSites` rows, and
  the verb's query a `FilterFreeSetSites` row.
- `tools/simulation/verify-harness.sh` rejects a missing or placeholder certificate before the
  sim stack boots.
