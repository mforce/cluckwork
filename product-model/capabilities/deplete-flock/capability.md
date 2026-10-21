---
availability:
- place: farm-web::farm
domain: flocks
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/FlockManagement/Flocks/DepleteFlock/DepleteFlockHandler.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/FlockManagement/Flocks/Flock.cs#Deplete
---

# Deplete a flock

Mark a flock's birds as gone. The depletion is stamped with the farm's today, and the flock keeps accepting backfill dated on or before it.
