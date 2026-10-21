---
availability:
- place: farm-web::farm
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/FlockManagement/Flocks/CreateFlock/CreateFlockHandler.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/FlockManagement/Flocks/CreateFlock/CreateFlockValidator.cs
- kind: code
  role: implementation
  target: web/src/routes/dailyEntry/useNewFlock.tsx
---

# Create a flock

Add a flock with its name, breed, placement date and initial bird count. Offered on Flocks and from Daily entry's new flock control.
