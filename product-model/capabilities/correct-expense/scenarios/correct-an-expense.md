---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses correct on an expense and changes its details
  kind: actor
  actor: manager
  entities:
  - entity: expense
    effect: reads
    facts:
    - Description
  contexts:
    web:
      place: farm-web::farm::expenses
- text: The Product saves the correction; the currency stays as recorded
  kind: product
  actor: manager
  entities:
  - entity: expense
    effect: changes
    facts:
    - Date
    - Description
    - Amount
    - Flock
    - Note
  - entity: expense-category
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::expenses
---

# Correct an expense

## Trigger

An expense was entered wrongly.

## Outcome

The expense shows its corrected values.
