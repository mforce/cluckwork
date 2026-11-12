# Runbook: the Data Protection key ring (#794)

**When to use this:** installing the key-encryption certificate on a new deployment;
changing the certificate as planned (procedure D); a Production serving process refuses
to start with `Production found N Data Protection key(s) stored without encryption`; or
the private key of the current certificate, or of any listed previous certificate, has
leaked (procedure C).

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
Procedure D loses nothing while the old certificate stays listed. Signed-in sessions are
not affected; they use JWTs.

A certificate change outside procedure D, removing a previous certificate, or a ring
deletion also makes every two-factor authenticator stored under the lost keys unreadable
(#1274). Recovery codes do not depend on the ring, but each one works once. To recover alone,
a signed-out user needs two unused recovery codes: one to sign in, and one for the step-up
that replacing the authenticator needs. A signed-in user needs one. A user with fewer needs an
Owner to reset their two-factor authentication, and an Owner with fewer needs another Owner or
`recover-admin`
([`break-glass-account-recovery.md`](break-glass-account-recovery.md)).

**Prerequisites:** the ability to change the deployment's configuration secrets, to stop
and start every serving instance, and SQL access to the Production database.

**Last drilled:** 2026-10-07 by the #794 implementer, against a scratch PostgreSQL
container with the API running in Production: procedure A (certificate issued with the
command below, fingerprint matched) and procedure B, steps 1 to 4, against a plaintext
row with single-quoted attributes, spaces around `=` and no `requiresEncryption` marker,
stored beside a real encrypted key and a revocation record. Procedures C and D: not
recorded.

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
colons.

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

Use this when the private key of the current certificate, or of any certificate listed in
`DataProtection__PreviousCertificates__*`, has leaked. Anyone holding a leaked private key
and a copy of the database (or any backup of it) can decrypt every stored key it
encrypted and forge payloads that validate. Revoke all of them. Do not keep the leaked
certificate configured for reading, and never list it as a previous certificate:
procedure D is for a planned change only.

### 1. Issue a new certificate on a new key pair

Follow A.1. Never reissue on the leaked key pair.

### 2. Stop every serving instance

An instance still running with the leaked certificate could write a new key encrypted
with it.

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
listed for decryption only. New keys are encrypted with the new certificate alone. The
framework creates a new key about every 90 days; after that, the keys under the old
certificate protect no new payloads but still read the ones they protected.

### 1. Issue the new certificate

Follow A.1. A new key pair is preferred. A renewal on the same key pair also works,
because the boot compares certificates by thumbprint, and a reissued certificate has a
new one.

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

### 4. Keep each previous certificate listed

Keep an entry for as long as any key it encrypted protects data still in use. Two-factor
authenticator keys are encrypted once, at enrolment, and are read at every sign-in. An
entry is therefore needed for as long as any user keeps an authenticator key protected by
a key that certificate encrypted. That includes users who enrolled up to about 90 days
after the change, because the ring keeps using its current key until the next one takes
over. It can be indefinitely. Removing an entry costs what a change without procedure D
costs: see the blast radius above. No step yet re-encrypts stored keys under the new
certificate, so there is no safe point at which to retire an old certificate while those
users remain enrolled.

## Verify

After B, C or D: the instances start (the guard found no plaintext key), the startup log
names the expected fingerprints, and `/health/ready` returns 200. After D, a user enrolled
before the change signs in with an authenticator code.

## If it fails

- **The boot names plaintext keys again after B.** Either step 1 missed an id from the
  message, or something is still writing into the Production database without the
  certificate. Check the ids first, then find that host before repeating B.
- **The boot fails with a database connection error.** The guard reads the key table at
  startup, so a Production serving process needs its database reachable to start.
- **Tokens issued before the change no longer validate.** That is the expected cost of
  B and C, not a fault. After D it means the old certificate is not listed, or a different
  certificate is: compare the "previous certificate" fingerprints with the old certificate's.
