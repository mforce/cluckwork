---
kind: primary
routes:
  web: Web
steps:
- text: The Worker picks a language or a counting unit under Preferences on Account
  kind: actor
  actor: worker
  entities:
  - entity: user
    effect: reads
    facts:
    - UI language
    - Counting unit
  contexts:
    web:
      place: farm-web::farm::account
- text: The Product saves the preference and the app follows it
  kind: product
  actor: worker
  entities:
  - entity: user
    effect: changes
    facts:
    - UI language
    - Counting unit
  contexts:
    web:
      place: farm-web::farm::account
---

# Change language and counting unit

## Trigger

Someone wants the app in their language or counting by the tray.

## Outcome

Screens render in the chosen language, falling back to English for untranslated text, and steppers count in the chosen unit.

## Edge cases

- Switching to a language this device has never loaded needs a connection; offline, the app stays in its current language and says so.
- Only units with an active eggs-per-unit definition can be chosen.
