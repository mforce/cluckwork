---
availability:
- place: farm-web::farm
domain: stock
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/EggOperations/EggLots/RecordEggLotMovement/RecordEggLotMovementHandler.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/EggOperations/Eggs/EggLot.cs#AdjustAvailable
- kind: code
  role: implementation
  target: web/src/routes/WriteOffDialog.tsx
---

# Write off stock

Correct one egg lot's available eggs with a reason: remove eggs lost to breakage, spoilage or theft (Discard) or used on the farm (InternalUse), or apply a recount (Reconciliation). The daily entry's production figures are never restated.
