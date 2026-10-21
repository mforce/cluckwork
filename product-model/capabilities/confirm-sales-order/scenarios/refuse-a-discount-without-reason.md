---
kind: validation
routes:
  web: Web
steps:
- text: The Sales person confirms an order with a line below its list price without choosing a reason
  kind: actor
  actor: sales
  entities: []
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Product refuses to confirm and asks for a discount reason; the order stays a Draft
  kind: condition
  actor: sales
  entities:
  - entity: sales-order
    effect: reads
    facts:
    - Lines
    - Discount reason
  contexts:
    web:
      place: farm-web::farm::sales-order
---

# Refuse a below-list order without a reason

## Trigger

A discount is given without saying why.

## Outcome

No stock is touched.

## Edge cases

- A reason supplied for an order with no below-list line is refused too.
