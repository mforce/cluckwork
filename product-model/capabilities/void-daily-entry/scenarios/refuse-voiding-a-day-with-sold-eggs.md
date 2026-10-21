---
kind: validation
routes:
  web: Web
steps:
- text: The Manager voids a day one of whose lots has already sold, allocated or written off eggs
  kind: actor
  actor: manager
  entities: []
  contexts:
    web:
      place: farm-web::farm::history
- text: The Product refuses the void and names the grade; nothing changes
  kind: condition
  actor: manager
  entities:
  - entity: egg-lot
    effect: reads
    facts:
    - Produced
    - Available
  - entity: daily-entry
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::history
---

# Refuse voiding a day whose eggs have left its lots

## Trigger

Some of the day's eggs have already been sold, allocated to an order or written off.

## Outcome

The day stays official until every egg is back in its lots, for example by voiding the sale first.

## Edge cases

- A day of an archived flock cannot be voided until the flock is reactivated.
