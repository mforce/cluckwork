---
kind: validation
routes:
  web: Web
steps:
- text: The Manager records a cull dated after today, or with zero or a negative number of birds
  kind: actor
  actor: manager
  entities: []
  contexts:
    web:
      place: farm-web::farm::flocks
- text: The Product refuses the movement
  kind: condition
  actor: manager
  entities:
  - entity: bird-movement
    effect: reads
    facts:
    - Date
    - Quantity
  contexts:
    web:
      place: farm-web::farm::flocks
---

# Refuse an invalid bird movement

## Trigger

A movement cannot be right as entered.

## Outcome

Nothing is added to the ledger.
