"""Authenticator codes for the cast's twoFactorOwner (#1281), as the server's
TotpMatcher computes them: RFC 6238, SHA-1, 30-second steps, 6 digits.

reset.sh imports code() to confirm that Owner's enrolment. From a shell, print
the code for the current step. The key goes in on stdin, so no process list
shows it:

    jq -r .twoFactorOwner.totpKey tools/simulation/.sim-cast.json | python3 tools/simulation/totp.py

The server takes each step once. After a harness signed this Owner in, wait up
to two minutes before you use a code for them, because a harness may have used
steps up to a minute ahead.
"""
import base64
import hashlib
import hmac
import struct
import sys
import time

STEP_SECONDS = 30


def code(key, step=None):
    if step is None:
        step = int(time.time()) // STEP_SECONDS
    digest = hmac.new(base64.b32decode(key.upper()), struct.pack(">Q", step), hashlib.sha1).digest()
    offset = digest[-1] & 0x0F
    return f"{(struct.unpack('>I', digest[offset:offset + 4])[0] & 0x7FFFFFFF) % 1_000_000:06d}"


if __name__ == "__main__":
    print(code(sys.stdin.read().strip()))
