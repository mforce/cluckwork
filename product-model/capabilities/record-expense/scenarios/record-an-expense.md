---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses Add expense and enters its date, category, description and amount, with an optional note
  kind: actor
  actor: manager
  entities:
  - entity: expense-category
    effect: reads
    facts:
    - Name
  - entity: expense
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::expenses
- text: The Product records the expense in the farm's currency
  kind: product
  actor: manager
  entities:
  - entity: expense
    effect: creates
    facts:
    - Date
    - Description
    - Amount
    - Currency
    - Flock
    - Note
  - entity: expense-category
    effect: reads
    facts: []
  - entity: farm
    effect: reads
    facts:
    - Currency
  contexts:
    web:
      place: farm-web::farm::expenses
---

# Record an expense

## Trigger

The farm spends money.

## Outcome

The expense counts in the period total and in the money summary.

## Edge cases

- A deactivated category cannot take new expenses.
- A date after the farm's today is refused.
