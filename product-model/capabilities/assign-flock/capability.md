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
  target: src/Cluckwork.Application/Modules/Access/Users/AssignFlock/AssignFlockHandler.cs
---

# Assign a flock to a worker

Give a plain Worker scope over one flock. The first flock assignment restricts the Worker to their assigned flocks; no flock is preselected.
