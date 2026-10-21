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
  target: src/Cluckwork.Application/Modules/Access/Contracts/UnassignFlockCommand.cs
---

# Remove a worker's flock assignment

Take a flock assignment away. Removing a Worker's last assignment restores farm-wide scope.
