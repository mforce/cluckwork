---
kind: alternative
routes:
  web: Web
steps:
- text: The Owner filters Connected apps by person and chooses Disconnect on one of their apps
  kind: actor
  actor: owner
  entities:
  - entity: app-connection
    effect: reads
    facts:
    - Connected
  - entity: user
    effect: reads
    facts:
    - Email
  - entity: ai-agent
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::setup::connected-apps
- text: The Product revokes that person's approvals of the app and its tokens; the person keeps their sign-in
  kind: product
  actor: owner
  entities:
  - entity: app-connection
    effect: removes
  - entity: user
    effect: reads
    facts: []
  - entity: ai-agent
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::setup::connected-apps
---

# Disconnect a colleague's app as the Owner

## Trigger

The Owner wants an app to stop acting for someone.

## Outcome

That app is refused for that person, which does less than disabling them.
