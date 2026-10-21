---
availability:
- place: farm-web::farm
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Infrastructure/Jobs/DailyEntryLockSweep.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/EggOperations/DailyEntries/LockDueDailyEntries/LockDueDailyEntriesHandler.cs
---

# Lock old daily entries

The Product locks every submitted daily entry once it is strictly older than seven farm-local days, counted in each farm's own timezone.
