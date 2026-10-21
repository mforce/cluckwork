---
availability:
- place: farm-web::farm
domain: flocks
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/FlockManagement/Flocks/RecordBirdMovement/RecordBirdMovementHandler.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/FlockManagement/Flocks/RecordBirdMovement/RecordBirdMovementValidator.cs
---

# Record a bird movement

Record a cull or a recount adjustment in a flock's bird ledger. Mortality is never entered by hand; it comes from submitted daily entries.
