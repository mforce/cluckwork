---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses Void on a confirmed order, gives a reason and confirms
  kind: actor
  actor: manager
  entities:
  - entity: sales-order
    effect: reads
    facts:
    - Reference
    - Total
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Product returns each allocated egg to the lot it was drawn from and voids the order with the reason
  kind: product
  actor: manager
  entities:
  - entity: egg-lot
    effect: changes
    facts:
    - Available
  - entity: sales-order
    effect: changes
    from: Confirmed
    to: Voided
    facts:
    - Void reason
  contexts:
    web:
      place: farm-web::farm::sales-order
---

# Void a confirmed order

## Trigger

An order was confirmed by mistake.

## Outcome

The eggs are back in their lots and the order reads Voided with its reason.

## Edge cases

- An order confirmed before lot-level allocations were recorded cannot be voided; its lots must be corrected by hand.
- This is not returns processing for delivered goods.
