---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses deactivate on a category
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
- text: The Product deactivates the category
  kind: product
  actor: manager
  entities:
  - entity: expense-category
    effect: changes
    from: Active
    to: Inactive
    facts: []
  contexts:
    web:
      place: farm-web::farm::expenses
---

# Deactivate a category

## Trigger

A category is no longer used.

## Outcome

It shows as deactivated and is not offered for new expenses.
