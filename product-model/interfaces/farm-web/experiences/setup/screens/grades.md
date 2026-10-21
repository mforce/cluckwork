---
entities:
- entity: egg-grade
  shows:
  - Name
  - Grade type
  - Sort order
  - Saleable
  - Condition counter
  - Low-stock floor
  collects:
  - Name
  - Grade type
  - Sort order
  - Saleable
  - Low-stock floor
entryPoints:
- farm-web: /grades
references:
- kind: code
  role: implementation
  target: web/src/routes/GradesPage.tsx
---

# Grades

The farm's grading buckets with their type, saleability, condition counter and low-stock floor.
