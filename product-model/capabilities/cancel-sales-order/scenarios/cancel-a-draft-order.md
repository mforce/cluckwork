---
kind: primary
routes:
  web: Web
steps:
- text: The Sales person chooses Cancel on a Draft order and confirms
  kind: actor
  actor: sales
  entities:
  - entity: sales-order
    effect: reads
    facts:
    - Reference
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Product cancels the order
  kind: product
  actor: sales
  entities:
  - entity: sales-order
    effect: changes
    from: Draft
    to: Cancelled
    facts: []
  contexts:
    web:
      place: farm-web::farm::sales-order
---

# Cancel a draft order

## Trigger

The customer no longer wants the order.

## Outcome

The order is Cancelled and drew no stock.
