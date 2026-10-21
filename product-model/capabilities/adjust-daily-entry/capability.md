---
availability:
- place: farm-web::farm
domain: history
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/EggOperations/DailyEntries/AdjustDailyEntry/AdjustDailyEntryHandler.cs#AdjustDailyEntryHandler
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/EggOperations/Eggs/DailyEntry.cs#ManagerAdjust
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/EggOperations/Eggs/EggLot.cs#AdjustProduction
---

# Adjust a daily entry

Correct the totals or grading of a submitted, locked or already adjusted day, with a reason, in the same two-step form the day was recorded in. The correction reconciles the day's egg lots and its mortality in the same act.
