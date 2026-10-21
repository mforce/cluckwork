---
kind: alternative
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
    from: Adjusted
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

# Adjust a day again

## Trigger

A correction itself needs correcting.

## Outcome

The day stays Adjusted, and the values the second adjustment replaced are the ones kept.
