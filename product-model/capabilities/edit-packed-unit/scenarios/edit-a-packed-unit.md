---
kind: primary
routes:
  web: Web
steps:
- text: The Manager edits a packed unit's eggs per unit or its active checkbox
  kind: actor
  actor: manager
  entities:
  - entity: packed-unit
    effect: reads
    facts:
    - Unit
    - Eggs per unit
  contexts:
    web:
      place: farm-web::setup::products
- text: The Product saves the unit's definition
  kind: product
  actor: manager
  entities:
  - entity: packed-unit
    effect: changes
    facts:
    - Eggs per unit
    - Active
  contexts:
    web:
      place: farm-web::setup::products
---

# Edit a packed unit

## Trigger

The farm's market packs cartons differently.

## Outcome

New lines in that unit use the new count; recorded lines keep theirs.

## Edge cases

- Individual cannot be changed.
- A counting unit that is deactivated falls back to one egg per stepper tap.
