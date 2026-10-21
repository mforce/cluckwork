---
kind: primary
routes:
  web: Web
steps:
- text: The Worker opens an item and records a purchase with its date, quantity and cost
  kind: actor
  actor: worker
  entities:
  - entity: inventory-item
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::farm::inventory
- text: The Product creates a lot for the batch and the item's stock on hand rises
  kind: product
  actor: worker
  entities:
  - entity: inventory-lot
    effect: creates
    facts:
    - Received date
    - Quantity received
    - Remaining
    - Unit cost
    - Supplier lot number
    - Expiry
  - entity: inventory-item
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::inventory
---

# Receive a purchase

## Trigger

Stock arrives at the farm.

## Outcome

The item has a new lot and more on hand.

## Edge cases

- A received date after the farm's today is refused; an expiry may be in the future.
