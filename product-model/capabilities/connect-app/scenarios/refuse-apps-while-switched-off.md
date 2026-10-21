---
kind: validation
routes:
  web: Web
steps:
- text: The Worker arrives at the consent screen while connected apps are turned off
  kind: actor
  actor: worker
  entities:
  - entity: ai-agent
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::connect-app
- text: The Product refuses the app without asking for a password
  kind: condition
  actor: worker
  entities:
  - entity: farm
    effect: reads
    facts:
    - Connected apps
  contexts:
    web:
      place: farm-web::farm::connect-app
---

# Refuse every app while connected apps are off

## Trigger

The farm does not allow connected apps.

## Outcome

No approval is made.
