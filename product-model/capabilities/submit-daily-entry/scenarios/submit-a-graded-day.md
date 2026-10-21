---
kind: primary
routes:
  web: Web
steps:
- text: The Worker enters the counts and grades every sellable egg so Left to grade reads zero
  kind: actor
  actor: worker
  entities: []
  contexts:
    web:
      place: farm-web::farm::daily-entry
- text: The Worker chooses Submit day and confirms
  kind: actor
  actor: worker
  entities: []
  contexts:
    web:
      place: farm-web::farm::daily-entry
- text: The Product saves the day's numbers as a Draft for that flock
  kind: product
  actor: worker
  entities:
  - entity: daily-entry
    effect: creates
    to: Draft
    facts:
    - Date
    - Total eggs
    - Cracked
    - Dirty
    - Discarded
    - Mortality
    - Grading
  - entity: flock
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::daily-entry
- text: The Product submits the day, creating an egg lot for each graded grade from that flock and day and a mortality bird movement for its deaths
  kind: product
  actor: worker
  entities:
  - entity: daily-entry
    effect: changes
    from: Draft
    to: Submitted
    facts:
    - Condition grades
  - entity: egg-lot
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
  - entity: flock
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::daily-entry
- text: The day is frozen for further capture and its eggs are in stock
  kind: condition
  entities:
  - entity: daily-entry
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::daily-entry
---

# Submit a fully graded day

## Trigger

A Worker finishes counting and grading a flock's day.

## Outcome

The day is Submitted; its egg lots carry a link back to it and the flock's bird count drops from the next day.

## Decision points

### Condition grades

Are the farm's Cracked and Dirty grades active and saleable when the day becomes official?

- Active and saleable → the Cracked or Dirty count becomes its own egg lot in that grade
- Switched off or not saleable → the count is recorded as a loss and makes no lot

## Edge cases

- Which grade each condition count resolved to is kept with the day and never re-resolved, so later grade changes never restate it.
- A day with no deaths creates no mortality movement.
