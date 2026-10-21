---
kind: alternative
routes:
  web: Web
steps:
- text: The Manager ticks Only actions through connected apps, or Show only for one app
  kind: actor
  actor: manager
  entities:
  - entity: ai-agent
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::setup::audit-log
- text: The Product lists the events a connected app performed for a person
  kind: product
  actor: manager
  entities:
  - entity: audit-event
    effect: reads
    facts:
    - Actor
    - When
    - Action
    - Connected app
  - entity: ai-agent
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::setup::audit-log
---

# Show only actions through connected apps

## Trigger

An Owner or Manager reviews what connected apps did.

## Outcome

Each event names the person and the app's name as copied at approval.
