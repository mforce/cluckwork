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
  target: src/Cluckwork.Application/Modules/Access/Users/ChangeUserRole/ChangeUserRoleHandler.cs
---

# Change a user's role

Promote or demote a person among Admin, Manager, Worker, Sales and Read-only. An actual change ends the person's sessions on their next request.
