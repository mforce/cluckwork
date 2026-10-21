---
kind: primary
routes:
  web: Web
steps:
- text: The Sales person chooses Confirm on the Draft order and confirms
  kind: actor
  actor: sales
  entities:
  - entity: sales-order
    effect: reads
    facts:
    - Lines
    - Total
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Product draws each line's eggs from its grade's oldest unrestricted lots produced by the farm's today, oldest first, and records which lots each line drew from
  kind: product
  actor: sales
  entities:
  - entity: egg-lot
    effect: changes
    facts:
    - Available
  - entity: sales-order
    effect: changes
    from: Draft
    to: Confirmed
    facts:
    - Allocations
  - entity: farm
    effect: reads
    facts:
    - Timezone
  contexts:
    web:
      place: farm-web::farm::sales-order
---

# Confirm an order

## Trigger

The farm commits to selling the order's eggs.

## Outcome

The order is Confirmed, its eggs have left available stock, and each lot's ledger shows the sale.

## Edge cases

- Two confirmations can never allocate the same eggs: lots are locked while they are drawn.
- An order with no lines cannot be confirmed.
