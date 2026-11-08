"""Authenticator codes for the simulation Owner (#1281), as the server's
TotpMatcher computes them: RFC 6238, SHA-1, 30-second steps, 6 digits.

reset.sh imports code() to confirm the Owner's enrolment. From a shell, print
the code for the current step:

    python3 tools/simulation/totp.py "$(jq -r .owner.totpKey tools/simulation/.sim-cast.json)"

The server takes each step once. Wait for the next 30-second step before you
use a code for the same Owner again.
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
    print(code(sys.argv[1]))
