---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses void on an official day in History, gives a reason and confirms
  kind: actor
  actor: manager
  entities:
  - entity: daily-entry
    effect: reads
    facts:
    - Total eggs
  contexts:
    web:
      place: farm-web::farm::history
- text: The Product empties every egg lot the day created and appends a bird movement reversing its mortality
  kind: product
  actor: manager
  entities:
  - entity: egg-lot
    effect: changes
    facts:
    - Produced
    - Available
  - entity: bird-movement
    effect: creates
    facts:
    - Date
    - Type
    - Quantity
  contexts:
    web:
      place: farm-web::farm::history
- text: The Product marks the day Voided with the reason
  kind: product
  actor: manager
  entities:
  - entity: daily-entry
    effect: changes
    from: Submitted
    to: Voided
    facts:
    - Void reason
  contexts:
    web:
      place: farm-web::farm::history
---

# Void a submitted day

## Trigger

A day was recorded against the wrong flock or should never have been recorded.

## Outcome

The day reads Voided with its reason, no longer counts anywhere, and the same flock and day can be recorded afresh.

## Edge cases

- An Adjusted day is voided the same way.
- A Draft cannot be voided; it never created anything.
