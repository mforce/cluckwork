---
kind: validation
routes:
  web: Web
steps:
- text: The Sales person enters more than the order still owes
  kind: actor
  actor: sales
  entities: []
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Product refuses the payment, stating the outstanding amount
  kind: condition
  actor: sales
  entities:
  - entity: sales-order
    effect: reads
    facts:
    - Outstanding
  - entity: payment
    effect: reads
    facts:
    - Amount
  contexts:
    web:
      place: farm-web::farm::sales-order
---

# Refuse a payment above the outstanding amount

## Trigger

A payment would overpay the order.

## Outcome

No payment is recorded.

## Edge cases

- Payments can only be recorded against confirmed orders.
- A payment dated after the farm's today is refused.
