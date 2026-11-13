# Runbook: the Data Protection key ring (#794)

**When to use this:** installing the key-encryption certificate on a new deployment;
changing the certificate as planned (procedure D); retiring a previous certificate after
such a change (D.4); a Production serving process refuses
to start with `Production found N Data Protection key(s) stored without encryption`; or
the private key of any certificate that encrypted a key still in the table or in a backup
has leaked, whether it is current, listed as previous, or no longer listed (procedure C).

**Not this runbook:** a boot that fails with `DataProtection:CertificatePem and
DataProtection:PrivateKeyPem are not configured` or `are not a usable certificate` is
a configuration error. Fix the two values (procedure A). A boot that names
`DataProtection:PreviousCertificates:<n>` is the same kind of error in a previous
certificate (procedure D). Nothing in the database needs changing.

**Blast radius:** every token a deleted or unreadable key protected stops validating:
password-reset tokens, and the authorization codes and access tokens of connected apps
(#795). Procedure B deletes only the plaintext keys it names, so only tokens protected by
those keys fail. Procedure C, and a certificate change that does not keep the old
certificate listed as in procedure D, loses every key: every connected app on every farm
is refused on its next request and has to send its user through authorization again; see
[`oauth-authorization-server.md`](oauth-authorization-server.md#d-after-the-key-ring-is-replaced-or-lost).
Procedure D loses nothing: the old certificate stays listed until D.4 has re-encrypted
every stored key under the new one. Signed-in sessions are not affected; they use JWTs.

A certificate change outside procedure D, removing a previous certificate before D.4 has
re-encrypted the keys it encrypted, or a ring deletion also makes every two-factor
authenticator stored under the lost keys unreadable (#1274). Recovery codes do not depend on the ring, but each one works once. To recover alone,
a signed-out user needs two unused recovery codes: one to sign in, and one for the step-up
that replacing the authenticator needs. A signed-in user needs one. A user with fewer needs an
Owner to reset their two-factor authentication, and an Owner with fewer needs another Owner or
`recover-admin`
([`break-glass-account-recovery.md`](break-glass-account-recovery.md)).

**Prerequisites:** the ability to change the deployment's configuration secrets, to stop
and start every serving instance, to run a one-shot verb in the serving image, and SQL
access to the Production database.

**Last drilled:** 2026-10-07 by the #794 implementer, against a scratch PostgreSQL
container with the API running in Production: procedure A (certificate issued with the
command below, fingerprint matched) and procedure B, steps 1 to 4, against a plaintext
row with single-quoted attributes, spaces around `=` and no `requiresEncryption` marker,
stored beside a real encrypted key and a revocation record. Procedure D: 2026-11-12 by
the #1338 implementer at commit `c401267e`, the same way. Certificate A was issued with
the A.1 command and an Owner enrolled in two-factor. Certificate B was then issued and A
moved to `DataProtection__PreviousCertificates__0__*` as D.2 says. After the restart, both
fingerprint lines matched the `openssl` output and the Owner signed in with an
authenticator code. With A removed from the list, the same sign-in was refused with
`Auth.SecondFactorKeyUnavailable`. Procedure D.4: 2026-11-13 by the #1420 implementer at
commit `74947ddc`, against a scratch PostgreSQL container with the API serving in Production
and the verb run from the image built at that commit. Certificate A was issued with the A.1
command and an Owner enrolled in two-factor, then D.1 to D.3 moved to certificate B with A
listed. Before any re-wrap, a host with B alone refused that Owner's code with
`Auth.SecondFactorKeyUnavailable`. D.4 steps 1 to 5 then ran as written: the key-id list,
`rewrap-key-ring` through `docker run --env-file`, the run with no previous entries, the
restart with A removed, and the Owner's sign-in with a code, which succeeded. The verb also
re-encrypted the ring under a role with only schema usage, table DML and sequence access,
and no DDL. Procedure C: not recorded.

Background and the reasons behind each rule:
[`794-data-protection-key-ring.md`](../decisions/794-data-protection-key-ring.md).

---

## A. Install or replace the certificate

The key ring is encrypted with an RSA certificate supplied as secrets through
configuration. Production must use its own database; a key written by a Development
or Testing host into it is stored in plaintext and makes Production refuse to start.

### 1. Issue an RSA certificate and private key

A self-signed certificate is enough. Nothing checks its chain or its dates.

```bash
openssl req -x509 -newkey rsa:2048 -nodes -days 3650 \
  -subj "/CN=cluckwork-data-protection" -keyout dp-key.pem -out dp-cert.pem
openssl x509 -in dp-cert.pem -noout -fingerprint -sha256
```

**The certificate keeps working after its NotAfter date.** The framework encrypts and
decrypts with an expired certificate (`DataProtectionCertificateExpiryTests`). The
lifetime is not a deadline.

**Renewing is a rotation.** A certificate reissued on the same key pair is a different
certificate, and it cannot read keys the old one encrypted (`Unable to retrieve the
decryption key.`). Replace the certificate for any reason other than a leak by
procedure D, which keeps the old one listed so stored keys stay readable. A renewal on
the same key pair is listed the same way; the boot refuses only the current certificate
itself, compared by thumbprint.

### 2. Supply both values to every serving instance

Set `DataProtection__CertificatePem` and `DataProtection__PrivateKeyPem` as secrets.
Either real line breaks or the escaped `\n` form used by `deploy/.env.example` works.

### 3. Verify

Each serving instance logs the fingerprint at startup:

```
Data Protection key ring encrypted with the certificate whose SHA-256 fingerprint is <FINGERPRINT>
```

It must equal the `openssl x509 -fingerprint -sha256` output from step 1, without the
colons. Production logs compact JSON, so the line shows as this message template, with the
fingerprint in the `CertificateSha256` property: compare that value.

## B. Production refuses to start: plaintext keys

### 1. Confirm the situation

The boot fails with:

```
Production found 1 Data Protection key(s) stored without encryption (key id: <id>). ...
```

Find the rows for the key ids the message names. The query parses each record's XML
rather than matching its text, so quoting and spacing do not matter. Put every id from
the message in the list:

```sql
SELECT "Id", "FriendlyName", (xpath('/*/@id', "Xml"::xml))[1]::text AS key_id
FROM "DataProtectionKeys"
WHERE (xpath('/*/@id', "Xml"::xml))[1]::text IN ('<id from the message>');
```

Note the `"Id"` of each row returned; step 3 deletes exactly those rows. The guard
decides by structure. Any visible `masterKey` element counts as plaintext, whatever its
`requiresEncryption` marker or its parent's name says. Do not search the XML for either.

A plaintext key usually means a non-Production host wrote into this database, or the
database ran a serving process before the certificate was configured. Find which before
continuing, or the key comes back.

### 2. Stop every serving instance

The guard runs only at startup. An instance already running may still hold the
plaintext key in memory.

### 3. Delete those rows

```sql
DELETE FROM "DataProtectionKeys" WHERE "Id" IN (<the "Id" values from step 1>);
```

> **Destructive.** Tokens protected with these keys stop validating. **This cannot be
> undone** except by restoring the rows from a backup, which brings the plaintext keys
> back.

### 4. Start again and verify

The instances start. If the table is now empty, the first one writes a new encrypted key.
If the boot refuses again, it names the remaining key ids; repeat from step 1 with them.

## C. The private key leaked: revoke the whole ring

Use this when the private key of any certificate that encrypted a key still in the table or
in a backup has leaked: the current certificate, one listed in
`DataProtection__PreviousCertificates__*`, or one removed from that list. Anyone holding a
leaked private key and a copy of the database (or any backup of it) can decrypt every
stored key it encrypted and forge payloads that validate. Revoke all of them. Do not keep
the leaked certificate configured for reading, and never list it as a previous
certificate: procedure D is for a planned change only.

### 1. Issue a new certificate on a new key pair

Follow A.1. Never reissue on the leaked key pair.

### 2. Stop every serving instance

A running instance holds the ring in memory. It would keep protecting new payloads with
keys that step 3 deletes, and if the current certificate leaked, it could write a new key
encrypted with it.

### 3. Delete every stored key

```sql
DELETE FROM "DataProtectionKeys";
```

> **Destructive.** Every protected token stops validating. **This cannot be undone**, and
> must not be: the deleted keys are the compromised material.

### 4. Replace the secrets and start

Supply the new certificate and key (A.2), remove the leaked ones from every place they
were stored, and start the instances. Delete every `DataProtection__PreviousCertificates__*`
variable too, because step 3 deleted every key they could read. Delete the variables
rather than blanking them: a blank entry stops the boot. Verify the fingerprint line (A.3)
names the new certificate and that no "previous certificate" line follows it.

### 5. Treat old backups as compromised

Backups taken before step 3 still hold keys encrypted with the leaked certificate.
Restoring one brings them back. After any restore of such a backup, repeat step 3
before starting a serving instance.

## D. Change the certificate as planned

Use this for a renewal, a new key size, or any replacement that is not a leak. Keys
already stored stay encrypted with the old certificate, so the old certificate stays
listed for decryption only until D.4 re-encrypts them under the new one. New keys are
encrypted with the new certificate alone. The framework creates a new key about every 90
days; after that, the keys under the old certificate protect no new payloads but still read
the ones they protected.

### 1. Issue the new certificate

Follow A.1, but write the new pair under new file names, so the current pair's files are
still there for step 2:

```bash
openssl req -x509 -newkey rsa:2048 -nodes -days 3650 \
  -subj "/CN=cluckwork-data-protection" -keyout dp-key-new.pem -out dp-cert-new.pem
openssl x509 -in dp-cert-new.pem -noout -fingerprint -sha256
```

A new key pair is preferred. A renewal on the same key pair also works, because the boot
compares certificates by thumbprint, and a reissued certificate has a new one.

### 2. Move the current certificate to the previous list

Set the current certificate's two values as the next free entry of the previous list,
and keep every entry already there. For the first change, that entry is `0`:

```
DataProtection__PreviousCertificates__0__CertificatePem=<the current certificate>
DataProtection__PreviousCertificates__0__PrivateKeyPem=<its private key>
```

Then put the new certificate and key in `DataProtection__CertificatePem` and
`DataProtection__PrivateKeyPem` (A.2). An expired previous certificate is accepted.

### 3. Stop every serving instance, then start them again

The boot refuses a previous entry that does not parse, has no matching private key, is not
RSA, or is the current certificate; the message names the entry. Each start logs the
current fingerprint, then one line per previous certificate:

```
Data Protection key ring also reads keys encrypted with the previous certificate whose SHA-256 fingerprint is <FINGERPRINT>
```

Production logs compact JSON, so the lines show as these message templates, with the
fingerprints in the `CertificateSha256` and `PreviousCertificateSha256` properties.
`CertificateSha256` must equal the new certificate's `openssl x509 -fingerprint -sha256`
output from step 1, and each `PreviousCertificateSha256` the same output for a listed old
certificate, all without the colons.

### 4. Retire previous certificates

`rewrap-key-ring` re-encrypts every stored key under the current certificate: current,
expired and revoked keys alike. Key ids and key material do not change, so every token
and authenticator key they protect stays readable, and every previous entry can then
leave the configuration. Revocation records hold no key and stay as they are.

Before step 2, check all three:

- D.3 is done: every serving instance runs with the new certificate current.
- The environment the verb gets names the serving database: the same
  `ConnectionStrings__Default` the serving instances use.
- The certificate in the environment the verb gets is the serving one. Its `openssl x509
  -fingerprint -sha256` output, without the colons, equals `CertificateSha256` in the
  serving instances' startup log. A wrong certificate here re-encrypts the ring to a key
  the server does not hold.

Run the verb in the serving image, as the serving user, with the ordinary DML-only runtime
database credential. It updates rows and runs no DDL. It runs online: a serving instance
keeps the keys it has already read, and their key material is unchanged.

#### 1. Back up

Take a database backup, and export the key table on its own. Keep the ids of the stored keys:
the framework names each key row `key-<key id>`, and the query prints the ids.

```bash
pg_dump --data-only --table='"DataProtectionKeys"' "<connection URI>" > data-protection-keys.sql
psql "<connection URI>" -Atc 'SELECT substr("FriendlyName", 5) FROM "DataProtectionKeys" WHERE "FriendlyName" LIKE $$key-%$$ ORDER BY "Id"' > stored-keys.txt
```

`pg_dump` and `psql` take a libpq connection URI, such as
`postgresql://<user>@<host>:5432/<database>?sslmode=require`, not the `Host=…;Port=…` form
of `ConnectionStrings__Default`. Set `sslmode` to what the database uses: `require` when it
has TLS.

#### 2. Re-encrypt

Run the verb with the serving configuration: the database, the current certificate and every
previous entry. `docker run --env-file` keeps quotes as part of a value, so write that file
with one `NAME=value` per line, no quotes, and the PEM values in the escaped `\n` form.

```bash
docker run --rm --env-file <runtime-credential.env> \
  ghcr.io/mforce/cluckwork@sha256:<digest> \
  rewrap-key-ring
```

The container needs a network that reaches the database. Host-specific image references,
network arguments, and env-file paths belong in the deployment repo.

Expected output, and exit code 0:

```
current certificate SHA-256 <FINGERPRINT>
<key id> <every fingerprint the key was encrypted with> <FINGERPRINT>
<key id> revocation
rewrapped <N>, already current <M>, revocations <R>
```

The fields of each row line are separated by tabs. The `current certificate SHA-256` line
must equal `CertificateSha256` from the startup log; in Production, log lines in compact
JSON can come before it. Each key row then names its fingerprints before and after,
comma-separated, never key material. The row lines and the counts print only after the
write succeeds. Every key id in `stored-keys.txt` must have a line. The run is online, so a
serving instance may have created a key since step 1: such a key adds a line that names only
the current fingerprint, and one to `already current`, and that is expected. Any other
difference, a missing key id or an extra one under another fingerprint, means stop and find
out why before going on. These checks do not prove which database the verb read; that rests
on the prerequisite that the verb gets the serving `ConnectionStrings__Default`. The verb's
own log, in the same output, gets one line with the same counts and fingerprints.

The verb writes every row in one transaction, after every check, so a refused run writes
nothing. It refuses, with exit code 1 and one line per refusal, when:

- no current certificate is configured, in any environment;
- a previous entry is one a serving start refuses (D.3); the message names the entry;
- a row is encrypted with a certificate the configuration does not list, or does not
  decrypt with the one it names; the message gives the row, the key id and that
  fingerprint;
- a row holds a key stored without encryption: follow procedure B, never re-encrypt it;
- a row has a shape the framework does not write: a decryptor other than
  `EncryptedXmlDecryptor` in its own assembly, a recipient anywhere but the direct
  `KeyInfo` path, an `EncryptedData` nested in another, ciphertext by reference, a key with
  no `encryptedSecret`, an unknown root element, XML that is not well formed, or no XML.

#### 3. Check without the previous certificates

Run the verb again with no `DataProtection__PreviousCertificates__*` variables at all. It
must exit 0 and print `rewrapped 0`, and its `current certificate SHA-256` line must again
equal `CertificateSha256`. Every key id step 2 printed must have a line again, and
`already current` must be at least step 2's `rewrapped` plus `already current`. A key a
serving instance created since step 2 adds a line that names only the current fingerprint,
and one to `already current`, and that is expected; any other difference means stop and find
out why. The verb decrypts every key row on every run, so this proves the current
certificate alone reads every stored key. It checks only the rows it read: a key a serving
instance creates later is already under the current certificate, because D.3 is done.

#### 4. Remove the previous entries

Delete every `DataProtection__PreviousCertificates__*` variable from the serving
configuration (a blank entry stops the boot), and restart every serving instance. The
startup log names the current fingerprint and no "previous certificate" line follows it.

#### 5. Verify

A user who enrolled an authenticator before the certificate change signs in with a code.

#### Old backups

Backups taken before step 2 still hold keys encrypted with the old certificates. Keep each
old private key as protected as the current one until the last such backup is destroyed,
then destroy it. To restore such a backup, list the old certificate again (D.2 form),
restart, and repeat D.4.

Re-encrypting does not change key material. A leaked old private key, together with any
backup taken before step 2, still yields every master key, so a leak still means procedure
C, never a re-encrypt.

## Verify

After B, C or D: the instances start (the guard found no plaintext key), the startup log
names the expected fingerprints, and `/health/ready` returns 200. After D, and again after
D.4, a user enrolled before the change signs in with an authenticator code.

## If it fails

- **The boot names plaintext keys again after B.** Either step 1 missed an id from the
  message, or something is still writing into the Production database without the
  certificate. Check the ids first, then find that host before repeating B.
- **The boot fails with a database connection error.** The guard reads the key table at
  startup, so a Production serving process needs its database reachable to start.
- **Tokens issued before the change no longer validate.** That is the expected cost of
  B and C, not a fault. After D it means the old certificate is not listed, or a different
  certificate is: compare the "previous certificate" fingerprints with the old certificate's.
- **`rewrap-key-ring` refuses or fails (D.4 step 2 or 3).** Keep the configuration as it is
  and do not go on to step 4. A refused run wrote nothing. If the run lost its database
  connection while writing, run it again: the write is one transaction, and a second run
  re-encrypts whatever is still under an old certificate.
- **A key appears that step 1 did not list.** A serving instance that cannot read the
  default key creates a new one under the current certificate. Its line names only the
  current fingerprint, so D.4's rule for an extra key covers it.
- **Authenticator codes fail after D.4 step 4.** Put the removed entries back (D.2 form)
  and restart. Then compare the verb's `current certificate SHA-256` line with
  `CertificateSha256`. If the rows went under a wrong certificate, run the verb again with
  the serving certificate current and the wrong one listed. Never restore the whole
  database for this: a restore discards every write since step 1.
- **Rolling back to the old certificate as current after D.4.** It reads nothing unless the
  new certificate is listed as a previous one.
