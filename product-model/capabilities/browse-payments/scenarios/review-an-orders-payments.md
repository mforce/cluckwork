---
kind: primary
routes:
  web: Web
steps:
- text: The Sales person opens a confirmed order
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
- text: The Product shows each payment with its date, amount, method and reference, a voided one with its reason, and the order's outstanding amount
  kind: product
  actor: sales
  entities:
  - entity: payment
    effect: reads
    facts:
    - Date
    - Amount
    - Method
    - Reference
    - Note
    - Void reason
  - entity: sales-order
    effect: reads
    facts:
    - Outstanding
  contexts:
    web:
      place: farm-web::farm::sales-order
---

# Review an order's payments

## Trigger

Someone checks whether an order is settled.

## Outcome

They see the order's settlement.
