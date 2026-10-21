---
kind: validation
routes:
  web: Web
steps:
- text: The Owner tries to give flock scope to a person whose role is not Worker
  kind: actor
  actor: owner
  entities:
  - entity: user
    effect: reads
    facts:
    - Role
  - entity: flock
    effect: reads
    facts: []
  - entity: worker
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::setup::users
- text: The Product refuses; only plain workers are ever scoped this way
  kind: condition
  actor: owner
  entities:
  - entity: user
    effect: reads
    facts:
    - Role
  - entity: worker
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::setup::users
---

# Refuse assigning a flock to someone who is not a Worker

## Trigger

Only Workers are ever scoped by flock.

## Outcome

No assignment is added.

## Edge cases

- Assigning the same flock twice is refused.
