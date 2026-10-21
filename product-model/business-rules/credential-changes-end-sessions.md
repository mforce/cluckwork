---
appliesTo:
- type: capability
  id: change-password
- type: capability
  id: reset-user-password
- type: capability
  id: change-user-role
- type: capability
  id: change-user-email
- type: capability
  id: disable-user
- type: capability
  id: recover-admin
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Middleware/CredentialEpochMiddleware.cs
- kind: doc
  role: context
  target: docs/decisions/364-credential-epoch-revocation.md
---

# A changed credential, role or status ends the person's other sessions on their next request

A password change or reset, a role or login email change, and disabling a person invalidate already-issued sessions immediately rather than when they expire; the person's connected apps must be approved again. Self-service password change keeps the device that made it signed in.
