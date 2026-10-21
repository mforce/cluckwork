---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses Correct stock on an item's lot and records an adjustment or discard with its quantity, date and reason
  kind: actor
  actor: manager
  entities:
  - entity: inventory-lot
    effect: reads
    facts:
    - Remaining
  contexts:
    web:
      place: farm-web::farm::inventory
- text: The Product changes the lot's remaining quantity and records the correction in the item's ledger
  kind: product
  actor: manager
  entities:
  - entity: inventory-lot
    effect: changes
    facts:
    - Remaining
  contexts:
    web:
      place: farm-web::farm::inventory
---

# Correct an inventory lot

## Trigger

Stock on hand disagrees with what was recorded.

## Outcome

The lot's remaining quantity is corrected; the earlier rows are untouched.
