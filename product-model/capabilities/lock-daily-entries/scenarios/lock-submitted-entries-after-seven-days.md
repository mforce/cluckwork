---
kind: unattended
routes:
  web: Web
steps:
- text: The Product's background sweep finds submitted daily entries dated more than seven days before the farm's today
  kind: condition
  unattended: true
  entities:
  - entity: daily-entry
    effect: reads
    facts:
    - Date
  - entity: farm
    effect: reads
    facts:
    - Timezone
  contexts:
    web:
      place: farm-web::farm::history
- text: The Product locks each one and stamps when
  kind: product
  entities:
  - entity: daily-entry
    effect: changes
    from: Submitted
    to: Locked
    facts:
    - Locked at
  contexts:
    web:
      place: farm-web::farm::history
---

# Lock submitted entries after seven farm days

## Trigger

The sweep runs on its schedule.

## Outcome

Those entries show as Locked in History; Owners and Managers can still adjust or void them.

## Edge cases

- An entry exactly seven days old stays Submitted until the next day.
