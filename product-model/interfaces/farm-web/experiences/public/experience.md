---
actors:
- visitor
access: public
entryPoints:
- farm-web: /login
references:
- kind: code
  role: implementation
  target: web/src/routes/Login.tsx
---

# Before signing in

Someone who has not signed in chooses a farm and signs in to it.
