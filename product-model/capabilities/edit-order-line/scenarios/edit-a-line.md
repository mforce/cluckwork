---
kind: primary
routes:
  web: Web
steps:
- text: The Sales person edits a line's quantity or unit price and saves it
  kind: actor
  actor: sales
  entities:
  - entity: sales-order
    effect: reads
    facts:
    - Lines
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Product updates the line and recalculates the order total
  kind: product
  actor: sales
  entities:
  - entity: sales-order
    effect: changes
    from: Draft
    to: Draft
    facts:
    - Lines
    - Total
  contexts:
    web:
      place: farm-web::farm::sales-order
---

# Edit a line

## Trigger

The customer changes how much they want or the price is agreed differently.

## Outcome

The line and total reflect the change.

## Edge cases

- If the order's fetched quantity or price changed since editing began, saving is blocked until the line is reloaded.
