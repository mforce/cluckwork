---
entities:
- entity: user
  shows:
  - Email
  - Display name
  - Role
  collects:
  - Email
  - Display name
  - Role
  - Password
- entity: flock-assignment
  shows:
  - Flock
  collects:
  - Flock
- entity: flock
  shows:
  - Name
entryPoints:
- farm-web: /users
references:
- kind: code
  role: implementation
  target: web/src/routes/UsersPage.tsx
---

# Users

The Owner's list of the farm's people with their role and status, and each Worker's flock assignments.
