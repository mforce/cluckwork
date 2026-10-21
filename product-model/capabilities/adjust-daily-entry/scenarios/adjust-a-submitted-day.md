---
kind: primary
routes:
  web: Web
steps:
- text: The Manager opens adjust on an official day in History, corrects counts and grading so they reconcile exactly, and gives a reason
  kind: actor
  actor: manager
  entities:
  - entity: daily-entry
    effect: reads
    facts:
    - Total eggs
    - Grading
  contexts:
    web:
      place: farm-web::farm::history
- text: The Product keeps the replaced values, applies the correction and marks the day Adjusted
  kind: product
  actor: manager
  entities:
  - entity: daily-entry
    effect: changes
    from: Submitted
    to: Adjusted
    facts:
    - Total eggs
    - Cracked
    - Dirty
    - Discarded
    - Mortality
    - Grading
    - Adjust reason
    - Values before adjustment
  contexts:
    web:
      place: farm-web::farm::history
- text: The Product grows, shrinks or empties the day's egg lots and adds a lot for a newly graded grade, and appends a compensating bird movement for any change in mortality
  kind: product
  actor: manager
  entities:
  - entity: egg-lot
    as: existing-lot
    effect: changes
    facts:
    - Produced
    - Available
  - entity: egg-lot
    as: added-lot
    effect: creates
    facts:
    - Production date
    - Flock
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
---

# Adjust a submitted day

## Trigger

A Manager finds a mistake in a day that is already official.

## Outcome

The day reads Adjusted with the corrected numbers and the reason; its stock and bird count follow the correction.

## Edge cases

- Unlike a stock write-off, which moves only available eggs, an adjustment restates the day's production.
- Someone saving against a stale copy of the day is refused and reloads.
