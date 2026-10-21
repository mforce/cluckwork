---
kind: primary
routes:
  web: Web
steps:
- text: The Worker picks a flock and a day no later than today
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
- text: The Worker enters the egg counts, losses and mortality, grades some sellable eggs and chooses Save draft
  kind: actor
  actor: worker
  entities: []
  contexts:
    web:
      place: farm-web::farm::daily-entry
- text: The Product creates a Draft for that flock and day with the counts and the grading so far
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
- text: The day shows as Editing draft and nothing reaches stock yet
  kind: condition
  entities:
  - entity: daily-entry
    effect: reads
    facts:
    - Sellable
    - Grading
  contexts:
    web:
      place: farm-web::farm::daily-entry
---

# Save a new day as a draft

## Trigger

A Worker starts recording a flock's day before every egg is graded.

## Outcome

The day is saved as a Draft that anyone who records production can reopen and finish.

## Edge cases

- A Draft opened from History's edit link reopens on this screen with its saved numbers.
- Grading past the current sellable figure raises the total eggs to match instead of refusing.
