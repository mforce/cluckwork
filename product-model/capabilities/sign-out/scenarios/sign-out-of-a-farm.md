---
kind: primary
routes:
  web: Web
steps:
- text: The Worker chooses Sign out
  kind: actor
  actor: worker
  entities: []
  contexts:
    web:
      place: farm-web::farm
- text: The Product ends the session and forgets which farm this tab was on
  kind: product
  actor: worker
  entities:
  - entity: farm
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm
---

# Sign out

## Trigger

Someone finishes working.

## Outcome

The tab returns to the sign-in page; other farms' sessions in the browser are untouched.
