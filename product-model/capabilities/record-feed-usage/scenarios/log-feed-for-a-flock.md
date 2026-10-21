---
kind: primary
routes:
  web: Web
steps:
- text: The Worker chooses Log feed, picks the flock, the item, the day and the quantity
  kind: actor
  actor: worker
  entities:
  - entity: flock
    effect: reads
    facts:
    - Name
  - entity: inventory-item
    effect: reads
    facts:
    - Name
    - Unit of measure
  contexts:
    web:
      place: farm-web::farm::feed
- text: The Product draws the quantity from the item's oldest lots received by that day and records the usage with its estimated cost and any same-day daily entry link
  kind: product
  actor: worker
  entities:
  - entity: inventory-lot
    effect: changes
    facts:
    - Remaining
  - entity: feed-usage
    effect: creates
    facts:
    - Date
    - Quantity
    - Estimated cost
    - Note
    - Daily entry link
  - entity: inventory-item
    effect: reads
    facts: []
  - entity: flock
    effect: reads
    facts: []
  - entity: daily-entry
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::feed
---

# Log feed for a flock

## Trigger

A flock is fed.

## Outcome

The feed history shows the usage and its cost, and the item's stock on hand falls.

## Edge cases

- The daily entry link is never filled in later and is not cleared if that entry is voided; flock and day remain how feed joins a day.
