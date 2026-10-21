---
kind: validation
routes:
  web: Web
steps:
- text: The Manager changes the unit of an item that has received stock
  kind: actor
  actor: manager
  entities:
  - entity: inventory-item
    effect: reads
    facts:
    - Unit of measure
  contexts:
    web:
      place: farm-web::farm::inventory
- text: The Product refuses because recorded quantities must never be reinterpreted in another unit
  kind: condition
  actor: manager
  entities:
  - entity: inventory-item
    effect: reads
    facts:
    - Unit of measure
    - Stock on hand
  contexts:
    web:
      place: farm-web::farm::inventory
---

# Refuse changing the unit of a stocked item

## Trigger

A unit change would reinterpret recorded quantities.

## Outcome

The item keeps its unit.
