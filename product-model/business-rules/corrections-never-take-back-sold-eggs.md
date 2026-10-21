---
appliesTo:
- type: capability
  id: adjust-daily-entry
- type: capability
  id: void-daily-entry
- type: entity
  id: egg-lot
  facts:
  - Produced
  - Available
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/EggOperations/Eggs/EggLot.cs#AdjustProduction
---

# A correction never takes back eggs a lot has sold, allocated or written off

Adjusting a day can grow, shrink, add or empty its lots but never below the eggs a lot has sold, allocated or written off; voiding a day is refused while any of its lots has sold, allocated or written off eggs.
