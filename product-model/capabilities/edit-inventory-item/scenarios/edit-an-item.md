---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses edit on an item and changes its details
  kind: actor
  actor: manager
  entities:
  - entity: inventory-item
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::farm::inventory
- text: The Product saves the item
  kind: product
  actor: manager
  entities:
  - entity: inventory-item
    effect: changes
    facts:
    - Name
    - Unit of measure
    - Default unit cost
  contexts:
    web:
      place: farm-web::farm::inventory
---

# Edit an item

## Trigger

An item's details need correcting.

## Outcome

The item shows its new details.
