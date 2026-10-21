---
kind: primary
routes:
  web: Web
steps:
- text: The Worker arrives from an app at the consent screen, signing in first if needed
  kind: actor
  actor: worker
  entities:
  - entity: ai-agent
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::connect-app
- text: The Product shows the app's name with its verified domain or an Unverified app marker, each permission it asks for with every one the Worker's role can use ticked, and where the browser returns
  kind: product
  actor: worker
  entities:
  - entity: ai-agent
    effect: reads
    facts:
    - App name
    - Verified domain
    - Return addresses
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
- text: The Worker keeps the permissions ticked, re-enters their current password and chooses Allow
  kind: actor
  actor: worker
  entities: []
  contexts:
    web:
      place: farm-web::farm::connect-app
- text: The Product records the approval of the ticked permissions and returns the browser to the app
  kind: product
  actor: worker
  entities:
  - entity: app-connection
    effect: creates
    facts:
    - Read farm data
    - Record daily entries
    - Submit daily entries
    - Record farm operations
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

# Approve an app

## Trigger

A person wants their AI assistant to work with the farm.

## Outcome

The app can act for the person within what they allowed; the approval is audited as App connected, naming what was allowed and what the app asked for.

## Edge cases

- Record expenses is marked Not allowed for your role for a Worker, has no checkbox and is never granted.
- Cancel returns nothing to the app.
- A wrong password gives the same denial whatever the reason.
