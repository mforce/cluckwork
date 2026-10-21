---
kind: alternative
routes:
  web: Web
steps:
- text: The Manager records an adjustment with a negative number after a recount found more birds
  kind: actor
  actor: manager
  entities:
  - entity: flock
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::farm::flocks
- text: The Product appends the adjustment, adding the birds back
  kind: product
  actor: manager
  entities:
  - entity: bird-movement
    effect: creates
    facts:
    - Date
    - Type
    - Quantity
    - Note
  - entity: flock
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::flocks
---

# Record a recount that adds birds back

## Trigger

A recount disagrees with the ledger.

## Outcome

The flock's current birds rise; the earlier rows are untouched.
