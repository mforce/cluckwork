---
entities:
- entity: feed-usage
  shows:
  - Date
  - Quantity
  - Estimated cost
  - Note
  collects:
  - Date
  - Quantity
  - Note
- entity: flock
  shows:
  - Name
- entity: inventory-item
  shows:
  - Name
  - Unit of measure
entryPoints:
- farm-web: /feed
references:
- kind: code
  role: implementation
  target: web/src/routes/FeedPage.tsx
---

# Feed

Where feed given to a flock is logged, beside the filterable feed history with each record's estimated cost.
