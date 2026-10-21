---
kind: primary
routes:
  web: Web
steps:
- text: The Worker chooses Disconnect on an app under Connected apps on Account and confirms
  kind: actor
  actor: worker
  entities:
  - entity: app-connection
    effect: reads
    facts:
    - Connected
    - Last used
  - entity: ai-agent
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::account
- text: The Product revokes the person's approvals of that app and its tokens
  kind: product
  actor: worker
  entities:
  - entity: app-connection
    effect: removes
  - entity: ai-agent
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::account
---

# Disconnect one's own app

## Trigger

Someone no longer wants an app acting for them.

## Outcome

The app is refused from its next request and must go through the consent screen again; the disconnect is audited.

## Edge cases

- An app unused for 30 days is marked and the panel suggests disconnecting it.
