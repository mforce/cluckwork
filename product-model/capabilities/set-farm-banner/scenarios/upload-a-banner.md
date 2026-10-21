---
kind: primary
routes:
  web: Web
steps:
- text: The Owner uploads an image as the banner on Farm settings
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
- text: The Product checks and rebuilds the image and saves it as the banner
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

# Upload a banner

## Trigger

The Owner wants a welcome image after sign-in.

## Outcome

Everyone sees the banner once per sign-in.
