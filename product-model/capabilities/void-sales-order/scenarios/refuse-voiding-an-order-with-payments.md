---
kind: validation
routes:
  web: Web
steps:
- text: The Manager voids a confirmed order that still has money recorded against it
  kind: actor
  actor: manager
  entities: []
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Product refuses and asks for the payments to be voided first
  kind: condition
  actor: manager
  entities:
  - entity: payment
    effect: reads
    facts:
    - Amount
  - entity: sales-order
    effect: reads
    facts:
    - Outstanding
  contexts:
    web:
      place: farm-web::farm::sales-order
---

# Refuse voiding an order that has payments

## Trigger

Money has been recorded against the order.

## Outcome

The order stays Confirmed.
