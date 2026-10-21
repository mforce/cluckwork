---
kind: alternative
routes:
  web: Web
steps:
- text: The Manager arrives from an app at the consent screen, which lists each permission the app asks for
  kind: actor
  actor: manager
  entities:
  - entity: ai-agent
    effect: reads
    facts: []
  - entity: app-connection
    effect: reads
    facts:
    - Read farm data
    - Record daily entries
    - Submit daily entries
    - Record farm operations
    - Record expenses
  contexts:
    web:
      place: farm-web::farm::connect-app
- text: The Manager unticks the permissions they do not want, re-enters their current password and chooses Allow
  kind: actor
  actor: manager
  entities: []
  contexts:
    web:
      place: farm-web::farm::connect-app
- text: The Product records an approval of only the ticked permissions and returns the browser to the app
  kind: product
  actor: manager
  entities:
  - entity: app-connection
    effect: creates
    facts:
    - Read farm data
    - Connected
  - entity: ai-agent
    effect: reads
    facts: []
  - entity: farm
    effect: reads
    facts:
    - Connected apps
  contexts:
    web:
      place: farm-web::farm::connect-app
---

# Approve only some of the permissions an app asks for

## Trigger

A person wants their AI assistant to do less than it asks.

## Outcome

The app gets only the ticked permissions; the approval is audited as App connected, naming what was allowed and what the app asked for.

## Edge cases

- Unticking everything leaves only Cancel, which needs no password and grants nothing.
- Approving fewer permissions than an earlier approval does not take the others away; only disconnecting the app does.
