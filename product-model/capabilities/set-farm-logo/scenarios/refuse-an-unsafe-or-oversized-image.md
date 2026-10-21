---
kind: validation
routes:
  web: Web
steps:
- text: The Owner uploads an SVG, an animated image, a file over the cap or one claiming more than 4096 pixels a side
  kind: actor
  actor: owner
  entities: []
  contexts:
    web:
      place: farm-web::setup::farm-settings
- text: The Product refuses the image
  kind: condition
  actor: owner
  entities:
  - entity: farm
    effect: reads
    facts:
    - Logo
  contexts:
    web:
      place: farm-web::setup::farm-settings
---

# Refuse an unsafe or oversized image

## Trigger

The image could carry script, hide data or lock up browsers.

## Outcome

The logo is unchanged.

## Edge cases

- The banner follows the same rules with its own larger cap.
