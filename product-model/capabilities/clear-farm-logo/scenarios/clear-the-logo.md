---
kind: primary
routes:
  web: Web
steps:
- text: The Owner clears the logo on Farm settings
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
- text: The Product removes the logo and leaves the banner untouched
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

# Clear the logo

## Trigger

The Owner no longer wants a custom logo.

## Outcome

Everyone sees Cluckwork's branding.
