---
availability:
- place: farm-web::farm
domain: history
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/EggOperations/DailyEntries/VoidDailyEntry/VoidDailyEntryHandler.cs#VoidDailyEntryHandler
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/EggOperations/Eggs/DailyEntry.cs#Void
---

# Void a daily entry

Undo an official day with a reason: every lot it created is emptied, its mortality is reversed by a compensating movement, and the entry stays listed as Voided. Voiding frees the day to be recorded again.
