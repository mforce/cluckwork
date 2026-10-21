---
entities:
- entity: water-usage
  shows:
  - Date
  - Quantity
  - Unit
  - Source
  - Meter readings
  - Note
  collects:
  - Date
  - Quantity
  - Unit
  - Source
  - Meter readings
  - Note
- entity: flock
  shows:
  - Name
entryPoints:
- farm-web: /water
references:
- kind: code
  role: implementation
  target: web/src/routes/WaterPage.tsx
---

# Water

Where a flock's water for a day is logged and corrected, beside the water history.
