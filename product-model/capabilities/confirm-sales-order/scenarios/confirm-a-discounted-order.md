---
kind: alternative
routes:
  web: Web
steps:
- text: The Sales person confirms an order with a line below its list price, choosing a discount reason and any note
  kind: actor
  actor: sales
  entities:
  - entity: sales-order
    effect: reads
    facts:
    - Lines
    - Discount
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Product allocates the eggs and confirms the order with the discount reason
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
    - Discount reason
    - Discount reason note
  contexts:
    web:
      place: farm-web::farm::sales-order
---

# Confirm an order sold below list price

## Trigger

The farm sells some eggs below the catalogue price.

## Outcome

The order records why it was sold below list.

## Edge cases

- Other needs a note; the other reasons take one optionally.
