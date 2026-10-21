---
kind: primary
routes:
  web: Web
steps:
- text: The Owner turns Allow connected apps off
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
- text: The Product saves the switch off
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

# Switch connected apps off

## Trigger

The Owner wants no app acting on the farm for now.

## Outcome

Every app is refused until the switch is turned back on; registering an app is unaffected.

## Edge cases

- The switch saves under the farm's settings version, so a concurrent Farm settings save gets a conflict instead of overwriting it.
