---
availability:
- place: farm-web::farm
domain: flocks
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/FlockManagement/Flocks/ArchiveFlock/ArchiveFlockHandler.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/FlockManagement/Flocks/Flock.cs#Archive
---

# Archive a flock

Hide a flock as bookkeeping. Archiving is allowed straight from Active for a flock created by mistake; an archived flock accepts nothing new and its name keeps rendering in history.
