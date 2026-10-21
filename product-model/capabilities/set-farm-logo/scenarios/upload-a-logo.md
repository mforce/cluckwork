---
kind: primary
routes:
  web: Web
steps:
- text: The Owner uploads an image as the logo on Farm settings
  kind: actor
  actor: owner
  entities:
  - entity: farm
    effect: reads
    facts:
    - Logo
  contexts:
    web:
      place: farm-web::setup::farm-settings
- text: The Product checks the image by its content, rebuilds it without metadata and saves it as the logo
  kind: product
  actor: owner
  entities:
  - entity: farm
    effect: changes
    facts:
    - Logo
  contexts:
    web:
      place: farm-web::setup::farm-settings
---

# Upload a logo

## Trigger

The Owner brands the app for the farm.

## Outcome

Everyone on the farm sees the logo in the app's chrome.
