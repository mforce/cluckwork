---
appliesTo:
- type: entity
  id: daily-entry
  effect: changes
  to: Locked
permits:
- unattended: true
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Infrastructure/Jobs/DailyEntryLockSweep.cs
---

# Only the Product's schedule locks a daily entry

No person locks an entry; the background sweep does.
