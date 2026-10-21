---
kind: validation
routes:
  web: Web
steps:
- text: The Visitor signs in with a farm code no farm on the server has
  kind: actor
  actor: visitor
  entities:
  - entity: farm
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::public::sign-in
- text: The Product refuses with its own message about the farm code
  kind: condition
  actor: visitor
  entities:
  - entity: farm
    effect: reads
    facts:
    - Farm code
  contexts:
    web:
      place: farm-web::public::sign-in
---

# Refuse an unknown farm code

## Trigger

The farm code is mistyped.

## Outcome

Nobody is signed in.
