---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses New category and enters a name
  kind: actor
  actor: manager
  entities: []
  contexts:
    web:
      place: farm-web::farm::expenses
- text: The Product creates the category as Active
  kind: product
  actor: manager
  entities:
  - entity: expense-category
    effect: creates
    to: Active
    facts:
    - Name
  contexts:
    web:
      place: farm-web::farm::expenses
---

# Create a category

## Trigger

The farm wants to track a new kind of spending.

## Outcome

The category is offered for new expenses.

## Edge cases

- A name another category already has, in any case, is refused.
