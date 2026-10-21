---
entities:
- entity: daily-entry
  shows:
  - Date
  - Total eggs
  - Cracked
  - Dirty
  - Discarded
  - Mortality
  - Sellable
  - Grading
  - Adjust reason
  - Values before adjustment
  - Void reason
  collects:
  - Total eggs
  - Cracked
  - Dirty
  - Discarded
  - Mortality
  - Grading
  - Adjust reason
  - Void reason
- entity: flock
  shows:
  - Name
entryPoints:
- farm-web: /history
references:
- kind: code
  role: implementation
  target: web/src/routes/HistoryPage.tsx
---

# History

The farm's daily entries by flock and date with their status, where drafts are reopened and official days are adjusted or voided.
