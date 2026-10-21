---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses New grade and enters its name, type, sort order and saleability
  kind: actor
  actor: manager
  entities: []
  contexts:
    web:
      place: farm-web::setup::grades
- text: The Product creates the grade as Active
  kind: product
  actor: manager
  entities:
  - entity: egg-grade
    effect: creates
    to: Active
    facts:
    - Name
    - Grade type
    - Sort order
    - Saleable
  contexts:
    web:
      place: farm-web::setup::grades
---

# Create a grade

## Trigger

The farm grades eggs into a new bucket.

## Outcome

A saleable grade is offered in Daily entry grading and on products.
