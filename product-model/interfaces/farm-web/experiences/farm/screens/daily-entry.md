---
entities:
- entity: daily-entry
  shows:
  - Sellable
  - Grading
  collects:
  - Date
  - Total eggs
  - Cracked
  - Dirty
  - Discarded
  - Mortality
  - Grading
- entity: flock
  shows:
  - Name
  - Current birds
  collects:
  - Name
  - Breed
  - Placement date
  - Initial count
- entity: egg-grade
  shows:
  - Name
entryPoints:
- farm-web: /daily-entry
references:
- kind: code
  role: implementation
  target: web/src/routes/DailyEntryPage.tsx
---

# Daily entry

Where one flock's day is counted and graded: the egg counts, the grading that counts down to the sellable figure, and the day's feed and water beside them.
