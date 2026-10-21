---
kind: alternative
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
- text: The oldest lot of a line's grade is restricted through a date on or after the farm's today
  kind: condition
  entities:
  - entity: egg-lot
    effect: reads
    facts:
    - Restricted until
  - entity: farm
    effect: reads
    facts:
    - Timezone
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Product skips the restricted lot and draws the line from the next oldest unrestricted lots
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
  contexts:
    web:
      place: farm-web::farm::sales-order
---

# Confirm past lots under withdrawal restriction

## Trigger

Some of a grade's stock is under medication withdrawal.

## Outcome

The order is confirmed from unrestricted eggs; the restricted lot keeps all its eggs.

## Edge cases

- When only restricted eggs could cover a line, confirming is refused as insufficient stock.
