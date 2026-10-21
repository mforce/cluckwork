---
availability:
- place: farm-web::setup
domain: users
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/Users/UserEndpoints.cs
- kind: code
  role: implementation
  target: web/src/routes/UsersPage.tsx
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Access/Users/EnableUser/EnableUserHandler.cs
---

# Enable a user

Restore a disabled person's access. Every credential issued before the disable stays dead, so they sign in fresh.
