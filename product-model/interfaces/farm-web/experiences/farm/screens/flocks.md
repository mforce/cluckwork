---
entities:
- entity: flock
  shows:
  - Name
  - Breed
  - Placement date
  - Initial count
  - Current birds
  - Depleted on
  - Archived on
  collects:
  - Name
  - Breed
  - Placement date
  - Initial count
- entity: bird-movement
  shows:
  - Date
  - Type
  - Quantity
  - Note
  collects:
  - Date
  - Type
  - Quantity
  - Note
entryPoints:
- farm-web: /flocks
references:
- kind: code
  role: implementation
  target: web/src/routes/FlocksPage.tsx
---

# Flocks

The farm's flocks with their status and bird counts, and each flock's bird ledger. Archived flocks show behind a toggle.
