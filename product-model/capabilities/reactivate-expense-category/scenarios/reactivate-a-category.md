---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses reactivate on a deactivated category
  kind: actor
  actor: manager
  entities:
  - entity: expense-category
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::farm::expenses
- text: The Product reactivates the category
  kind: product
  actor: manager
  entities:
  - entity: expense-category
    effect: changes
    from: Inactive
    to: Active
    facts: []
  contexts:
    web:
      place: farm-web::farm::expenses
---

# Reactivate a category

## Trigger

A retired category is needed again.

## Outcome

It is offered for new expenses.
