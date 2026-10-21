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
  target: src/Cluckwork.Application/Modules/Access/Users/ChangeUserEmail/ChangeUserEmailHandler.cs
---

# Change a user's login email

Replace a person's login email immediately. The old address stops working, their sessions end, and no confirmation email is sent.
