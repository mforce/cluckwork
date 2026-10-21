---
kind: alternative
routes:
  web: Web
steps:
- text: The Worker returns to the consent screen from an app they already approved, which asks for nothing new
  kind: actor
  actor: worker
  entities:
  - entity: ai-agent
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::connect-app
- text: The Product shows the permissions again, with what an earlier approval allowed ticked and marked Already allowed
  kind: product
  actor: worker
  entities:
  - entity: app-connection
    effect: reads
    facts:
    - Read farm data
    - Record daily entries
  contexts:
    web:
      place: farm-web::farm::connect-app
- text: The Worker re-enters the password and chooses Allow
  kind: actor
  actor: worker
  entities: []
  contexts:
    web:
      place: farm-web::farm::connect-app
- text: The Product records the reapproval
  kind: product
  actor: worker
  entities:
  - entity: app-connection
    effect: changes
    facts:
    - Connected
  contexts:
    web:
      place: farm-web::farm::connect-app
---

# Reconnect an app that asks for nothing new

## Trigger

An approved app signs in again, for instance after the person changed their password.

## Outcome

The app works again; an approval of exactly the permissions of an earlier one is audited as App reconnected.

## Edge cases

- When the app asks for more, each new permission starts unticked and is marked New, and the earlier ones start ticked and are marked Already allowed; approving a set not approved before, wider or narrower, is audited as App connected.
- Permissions the person declined before are not remembered.
