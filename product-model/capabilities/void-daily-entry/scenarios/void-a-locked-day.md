---
kind: alternative
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
    from: Locked
    to: Voided
    facts:
    - Void reason
  contexts:
    web:
      place: farm-web::farm::history
---

# Void a locked day

## Trigger

A Manager voids a day that has already locked.

## Outcome

The locked day is voided like any official day.
