---
kind: validation
routes:
  web: Web
steps:
- text: The Sales person confirms an order with a line discounted further below list than allowed
  kind: actor
  actor: sales
  entities: []
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Product refuses to confirm, naming the line's grade and its discount against the farm's maximum; the order stays a Draft
  kind: condition
  actor: sales
  entities:
  - entity: sales-order
    effect: reads
    facts:
    - Lines
    - Discount
  - entity: farm
    effect: reads
    facts:
    - Discount ceiling
  contexts:
    web:
      place: farm-web::farm::sales-order
---

# Refuse a line beyond the farm's discount ceiling

## Trigger

A Sales user or Worker discounts beyond what the farm allows.

## Outcome

An Owner or Manager can confirm the same order untouched; there is no approval queue.

## Edge cases

- Exactly at the ceiling is allowed; the ceiling is measured per line, never against the order total.
- A line whose discount cannot be measured, such as one predating recorded list prices, is refused for a ceiling-bound seller.
