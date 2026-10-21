---
appliesTo:
- type: capability
  id: submit-daily-entry
- type: capability
  id: adjust-daily-entry
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/EggOperations/Eggs/DailyEntry.cs#DailyEntry
- kind: doc
  role: intent
  target: specs/product/GLOSSARY.md
---

# Grading must equal the sellable count exactly to submit or adjust a day

The grade lines must sum to exactly total eggs minus cracked, dirty and discarded, short and over refused alike, at submit and when saving an adjustment. Zero sellable eggs reconcile to no grading.

## Rationale

An ungraded or partly graded official day would silently produce too little stock for real production.
