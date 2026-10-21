---
kind: primary
routes:
  web: Web
steps:
- text: The Sales person records money received on a confirmed order with its date, amount and method
  kind: actor
  actor: sales
  entities:
  - entity: sales-order
    effect: reads
    facts:
    - Outstanding
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Product records the payment in the order's currency and the outstanding amount falls
  kind: product
  actor: sales
  entities:
  - entity: payment
    effect: creates
    to: Recorded
    facts:
    - Date
    - Amount
    - Method
    - Reference
    - Note
  - entity: sales-order
    effect: reads
    facts:
    - Outstanding
  contexts:
    web:
      place: farm-web::farm::sales-order
---

# Record a payment

## Trigger

A customer pays for an order.

## Outcome

The payment shows on the order and its outstanding amount drops.
