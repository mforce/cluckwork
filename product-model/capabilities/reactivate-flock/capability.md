---
availability:
- place: farm-web::farm
domain: flocks
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/FlockManagement/Flocks/ReactivateFlock/ReactivateFlockHandler.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/FlockManagement/Flocks/Flock.cs#Reactivate
---

# Reactivate a flock

Return a depleted or archived flock to Active, clearing its lifecycle dates so capture for any date is restored.
