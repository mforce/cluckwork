---
entities:
- entity: egg-grade
  shows:
  - Name
  - Low-stock floor
- entity: egg-lot
  shows:
  - Production date
  - Flock
  - Produced
  - Available
  - Restricted until
- entity: egg-movement
  shows:
  - When
  - Type
  - Change
  - Reason
entryPoints:
- farm-web: /stock
references:
- kind: code
  role: implementation
  target: web/src/routes/StockPage.tsx
---

# Stock

Egg stock by grade, available and restricted, against each grade's low-stock floor; each grade's lots by production date; and each lot's movement ledger.
