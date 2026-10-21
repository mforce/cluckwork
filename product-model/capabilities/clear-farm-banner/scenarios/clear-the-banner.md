---
kind: primary
routes:
  web: Web
steps:
- text: The Owner clears the banner on Farm settings
  kind: actor
  actor: owner
  entities:
  - entity: farm
    effect: reads
    facts:
    - Banner
  contexts:
    web:
      place: farm-web::setup::farm-settings
- text: The Product removes the banner and leaves the logo untouched
  kind: product
  actor: owner
  entities:
  - entity: farm
    effect: changes
    facts:
    - Banner
  contexts:
    web:
      place: farm-web::setup::farm-settings
---

# Clear the banner

## Trigger

The Owner no longer wants a splash.

## Outcome

Nobody sees a splash after signing in.
