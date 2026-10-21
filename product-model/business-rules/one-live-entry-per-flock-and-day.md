---
appliesTo:
- type: capability
  id: save-daily-entry-draft
- type: capability
  id: void-daily-entry
- type: entity
  id: daily-entry
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/EggOperations/Eggs/DailyEntry.cs#Create
- kind: doc
  role: intent
  target: specs/product/GLOSSARY.md
---

# A flock has at most one live daily entry per day

Saving the same flock and day again updates its entry; uniqueness counts only entries that are not voided, so voiding a day frees it to be recorded again.
