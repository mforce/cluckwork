---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses correct on a water record and changes its quantity, unit, source, readings or note
  kind: actor
  actor: manager
  entities:
  - entity: water-usage
    effect: reads
    facts:
    - Quantity
  contexts:
    web:
      place: farm-web::farm::water
- text: The Product saves the corrected values
  kind: product
  actor: manager
  entities:
  - entity: water-usage
    effect: changes
    facts:
    - Quantity
    - Unit
    - Source
    - Meter readings
    - Note
  contexts:
    web:
      place: farm-web::farm::water
---

# Correct a water record

## Trigger

A water record was entered wrongly.

## Outcome

The record shows the corrected values; its daily entry link is unchanged.

## Edge cases

- Someone saving against a stale copy of the record is refused and reloads.
