---
kind: primary
routes:
  web: Web
steps:
- text: The Owner turns Allow connected apps on
  kind: actor
  actor: owner
  entities:
  - entity: farm
    effect: reads
    facts:
    - Connected apps
  - entity: ai-agent
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::setup::connected-apps
- text: The Product saves the switch on
  kind: product
  actor: owner
  entities:
  - entity: farm
    effect: changes
    facts:
    - Connected apps
  contexts:
    web:
      place: farm-web::setup::connected-apps
---

# Switch connected apps on

## Trigger

The Owner allows apps again.

## Outcome

Apps approved earlier work again without reconnecting.
