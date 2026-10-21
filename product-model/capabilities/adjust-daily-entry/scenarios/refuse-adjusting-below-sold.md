---
kind: validation
routes:
  web: Web
steps:
- text: The Manager lowers a grade below the eggs its lot has already sold, allocated or written off
  kind: actor
  actor: manager
  entities: []
  contexts:
    web:
      place: farm-web::farm::history
- text: The Product refuses the adjustment and names the grade; nothing changes
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
    facts:
    - Grading
  contexts:
    web:
      place: farm-web::farm::history
---

# Refuse an adjustment below what a lot has sold, allocated or written off

## Trigger

A correction would take back eggs that already left the lot.

## Outcome

The day keeps its official numbers.

## Edge cases

- A missing reason, or grading that does not reconcile exactly, is refused the same way.
- A day submitted before lots were linked to their entry cannot be adjusted.
