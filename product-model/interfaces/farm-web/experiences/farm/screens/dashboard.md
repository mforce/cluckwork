---
entities:
- entity: daily-entry
  shows:
  - Date
  - Total eggs
- entity: flock
  shows:
  - Name
- entity: egg-grade
  shows:
  - Name
  - Low-stock floor
- entity: egg-lot
  shows:
  - Available
- entity: sales-order
  shows:
  - Reference
  - Order date
  - Total
- entity: customer
  shows:
  - Name
entryPoints:
- farm-web: /
references:
- kind: code
  role: implementation
  target: web/src/routes/Dashboard.tsx
---

# Dashboard

The farm at a glance: the morning brief, today's collection status per house with the houses that have no entry first, stock by grade with low-stock warnings, recent orders and the lay rate trend.
