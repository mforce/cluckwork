---
entities:
- entity: farm
  collects:
  - Farm code
- entity: user
  collects:
  - Email
  - Password
entryPoints:
- farm-web: /login
references:
- kind: code
  role: implementation
  target: web/src/routes/Login.tsx
---

# Sign in

Where someone signs in with a farm code, email and password. A link can prefill the farm code.
