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
  target: src/Cluckwork.Application/Modules/Access/Users/DisableUser/DisableUserHandler.cs
---

# Disable a user

Revoke a person's access without deleting them: they cannot sign in and every session stops on its next request. Their records and history are untouched.
