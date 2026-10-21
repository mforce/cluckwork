---
kind: validation
routes:
  web: Web
steps:
- text: The Worker chooses Submit day while the grading is short of, or over, the sellable count
  kind: actor
  actor: worker
  entities: []
  contexts:
    web:
      place: farm-web::farm::daily-entry
- text: The Product refuses to submit because the grading must equal total eggs minus cracked, dirty and discarded exactly
  kind: condition
  actor: worker
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

# Refuse a day whose grading does not add up

## Trigger

A day is submitted before its grading adds up.

## Outcome

The day stays a Draft at most; nothing reaches stock.

## Edge cases

- A Draft whose flock has since been archived, or whose date falls after the flock's depletion, can no longer be submitted.
