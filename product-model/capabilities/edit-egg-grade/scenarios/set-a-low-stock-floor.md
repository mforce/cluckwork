---
kind: alternative
routes:
  web: Web
steps:
- text: The Owner edits a grade and sets or clears its low-stock floor in eggs
  kind: actor
  actor: owner
  entities:
  - entity: egg-grade
    effect: reads
    facts:
    - Name
    - Low-stock floor
  contexts:
    web:
      place: farm-web::setup::grades
- text: The Product saves the floor on the grade
  kind: product
  actor: owner
  entities:
  - entity: egg-grade
    effect: changes
    facts:
    - Low-stock floor
  contexts:
    web:
      place: farm-web::setup::grades
---

# Set a grade's low-stock floor

## Trigger

The farm wants a warning before a grade runs out.

## Outcome

Stock and the Dashboard flag the grade when its available eggs fall below the floor.
