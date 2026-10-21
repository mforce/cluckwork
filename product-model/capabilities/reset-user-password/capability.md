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
  target: src/Cluckwork.Application/Modules/Access/Users/SetUserPassword/SetUserPasswordHandler.cs
---

# Reset a user's password

Set a colleague's password without knowing the current one: the forgot-password path, since there is no emailed reset. It ends all of that person's sessions.
