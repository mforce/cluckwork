# Runbook: the Data Protection key ring (#794)

**When to use this:** installing the key-encryption certificate on a new deployment;
a Production serving process refuses to start with `Production found N Data Protection
key(s) stored without encryption`; or the certificate's private key has leaked.

**Not this runbook:** a boot that fails with `DataProtection:CertificatePem and
DataProtection:PrivateKeyPem are not configured` or `are not a usable certificate` is
a configuration error. Fix the two values (procedure A). Nothing in the database needs
changing.

**Blast radius:** procedures B and C delete stored keys. Every token those keys
protected stops validating (password-reset tokens today, and anything else built on
ASP.NET Core Data Protection later). Signed-in sessions are not affected; they use JWTs.

**Prerequisites:** the ability to change the deployment's configuration secrets, to stop
and start every serving instance, and SQL access to the Production database.

**Last drilled:** 2026-10-07 by the #794 implementer, against a scratch PostgreSQL
container with the API running in Production: procedure A (certificate issued with the
command below, fingerprint matched) and procedure B, steps 1 to 4, against a plaintext
row with single-quoted attributes, spaces around `=` and no `requiresEncryption` marker,
stored beside a real encrypted key and a revocation record. Procedure C: not recorded.

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
decryption key.`). Replacing the certificate for any reason makes every stored key
unreadable, so outstanding tokens fail and the framework creates a new key. Plan a
replacement like procedure C, without the urgency.

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
decides by structure: a master key outside an encrypted element counts as plaintext,
whatever its `requiresEncryption` marker says. Do not search the XML for the marker.

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

Anyone holding the leaked private key and a copy of the database (or any backup of it)
can decrypt every stored key and forge payloads that validate. Revoke all of them. Do
not keep the old certificate configured for reading.

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
were stored, and start the instances. Verify the fingerprint line (A.3) names the new
certificate.

### 5. Treat old backups as compromised

Backups taken before step 3 still hold keys encrypted with the leaked certificate.
Restoring one brings them back. After any restore of such a backup, repeat step 3
before starting a serving instance.

## Verify

After B or C: the instances start (the guard found no plaintext key), the startup log names
the expected fingerprint, and `/health/ready` returns 200.

## If it fails

- **The boot names plaintext keys again after B.** Either step 1 missed an id from the
  message, or something is still writing into the Production database without the
  certificate. Check the ids first, then find that host before repeating B.
- **The boot fails with a database connection error.** The guard reads the key table at
  startup, so a Production serving process needs its database reachable to start.
- **Tokens issued before the change no longer validate.** That is the expected cost of
  B and C, not a fault.
