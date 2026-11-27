"""A passkey for the cast's twoFactorOwner (#1285), made as a browser would
answer navigator.credentials.create: a P-256 key from openssl, attestation
format none, hand-written CBOR, as the integration tests' SoftwareAuthenticator
does. Attestation none carries no signature, so only the public key goes out.

reset.sh calls attest() to register it. The private key goes into the cast
file, and the Playwright passkey spec loads it into Chrome's virtual
authenticator, which then signs the sign-in assertions.
"""
import base64
import hashlib
import json
import os
import subprocess


def _b64url(data):
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def _head(major, n):
    if n < 24:
        return bytes([major << 5 | n])
    if n < 256:
        return bytes([major << 5 | 24, n])
    return bytes([major << 5 | 25]) + n.to_bytes(2, "big")


def _cbor(value):
    if isinstance(value, int):
        return _head(0, value) if value >= 0 else _head(1, -1 - value)
    if isinstance(value, bytes):
        return _head(2, len(value)) + value
    if isinstance(value, str):
        return _head(3, len(value.encode())) + value.encode()
    # A dict, as a list of pairs so the key order is the caller's.
    return _head(5, len(value)) + b"".join(_cbor(k) + _cbor(v) for k, v in value)


def attest(options, origin):
    """The registration JSON for these creation options, and the cast entry."""
    # Through SEC1, so the PKCS#8 holds the public key too, which Chrome's
    # WebAuthn.addCredential requires; genpkey's PKCS#8 leaves it out.
    sec1 = subprocess.run(["openssl", "ecparam", "-name", "prime256v1", "-genkey", "-noout"],
                          check=True, capture_output=True).stdout
    pkcs8 = subprocess.run(["openssl", "pkcs8", "-topk8", "-nocrypt", "-outform", "DER"],
                           input=sec1, check=True, capture_output=True).stdout
    spki = subprocess.run(["openssl", "pkey", "-inform", "DER", "-pubout", "-outform", "DER"],
                          input=pkcs8, check=True, capture_output=True).stdout
    x, y = spki[-64:-32], spki[-32:]
    credential_id = os.urandom(16)
    rp_id = options["rp"]["id"]
    client_data = json.dumps({"type": "webauthn.create", "challenge": options["challenge"],
                              "origin": origin, "crossOrigin": False}).encode()
    # COSE_Key, EC2 P-256 ES256, in CTAP2 canonical order.
    cose = _cbor([(1, 2), (3, -7), (-1, 1), (-2, x), (-3, y)])
    # User present, user verified, attested data. Not backup eligible: Chrome's
    # virtual authenticator reports it so, and the flag may not change.
    flags = 0x01 | 0x04 | 0x40
    auth_data = (hashlib.sha256(rp_id.encode()).digest() + bytes([flags]) + bytes(4) + bytes(16)
                 + len(credential_id).to_bytes(2, "big") + credential_id + cose)
    attestation = _cbor([("fmt", "none"), ("attStmt", []), ("authData", auth_data)])
    credential = {
        "id": _b64url(credential_id), "rawId": _b64url(credential_id), "type": "public-key",
        "response": {"clientDataJSON": _b64url(client_data), "attestationObject": _b64url(attestation),
                     "transports": ["internal"]},
        "clientExtensionResults": {}, "authenticatorAttachment": "platform",
    }
    entry = {"credentialId": _b64url(credential_id), "rpId": rp_id, "userHandle": options["user"]["id"],
             "privateKey": base64.b64encode(pkcs8).decode()}
    return credential, entry
