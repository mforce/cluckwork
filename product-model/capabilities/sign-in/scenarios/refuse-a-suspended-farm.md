---
kind: validation
routes:
  web: Web
steps:
- text: The Visitor signs in to a farm that has been suspended
  kind: actor
  actor: visitor
  entities:
  - entity: farm
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::public::sign-in
- text: The Product refuses because the farm is suspended
  kind: condition
  actor: visitor
  entities:
  - entity: farm
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::public::sign-in
---

# Refuse signing in to a suspended farm

## Trigger

The farm is offline.

## Outcome

Nobody is signed in; a disabled person is refused the same way.
