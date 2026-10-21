---
kind: primary
routes:
  web: Web
steps:
- text: The Manager opens a flock's bird ledger and records a cull with its date, number of birds and an optional note
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
- text: The Product appends the cull to the flock's bird ledger
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

# Record a cull

## Trigger

Birds were removed alive from a flock.

## Outcome

The flock's current birds drop from the day after the cull.
