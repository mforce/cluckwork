---
appliesTo:
- type: capability
  id: write-off-stock
- type: entity
  id: egg-lot
  facts:
  - Available
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/EggOperations/EggLots/RecordEggLotMovement/RecordEggLotMovementHandler.cs
---

# A stock write-off keeps a lot between zero and what it can recount

Available eggs never go below zero, and a positive recount is capped at the lot's cumulative write-offs, never at raw production, so allocated eggs never read as headroom.
