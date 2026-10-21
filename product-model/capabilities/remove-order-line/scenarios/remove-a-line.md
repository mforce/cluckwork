---
kind: primary
routes:
  web: Web
steps:
- text: The Sales person removes a line from the Draft order
  kind: actor
  actor: sales
  entities:
  - entity: sales-order
    effect: reads
    facts:
    - Lines
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Product removes the line and recalculates the order total
  kind: product
  actor: sales
  entities:
  - entity: sales-order
    effect: changes
    from: Draft
    to: Draft
    facts:
    - Lines
    - Total
  contexts:
    web:
      place: farm-web::farm::sales-order
---

# Remove a line

## Trigger

A product is no longer wanted.

## Outcome

The order no longer carries the line.
