---
kind: alternative
routes:
  web: Web
steps:
- text: The Worker reopens the flock's day that already has a Draft and changes its numbers
  kind: actor
  actor: worker
  entities:
  - entity: flock
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::farm::daily-entry
- text: The Product replaces the draft's counts and grading in place
  kind: product
  actor: worker
  entities:
  - entity: daily-entry
    effect: changes
    from: Draft
    to: Draft
    facts:
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
---

# Save over an existing draft

## Trigger

A Worker returns to a saved draft for the same flock and day.

## Outcome

The one Draft for that flock and day carries the new numbers; no second entry is made.
