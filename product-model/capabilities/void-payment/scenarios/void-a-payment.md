---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses void on a payment and gives a reason
  kind: actor
  actor: manager
  entities:
  - entity: payment
    effect: reads
    facts:
    - Amount
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Product voids the payment with the reason
  kind: product
  actor: manager
  entities:
  - entity: payment
    effect: changes
    from: Recorded
    to: Voided
    facts:
    - Void reason
  contexts:
    web:
      place: farm-web::farm::sales-order
---

# Void a payment

## Trigger

A payment was recorded wrongly.

## Outcome

The payment shows its void reason and no longer counts against the order.
