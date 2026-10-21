---
kind: primary
routes:
  web: Web
steps:
- text: The Owner changes settings on Farm settings and saves
  kind: actor
  actor: owner
  entities:
  - entity: farm
    effect: reads
    facts:
    - Name
    - Timezone
    - Locale
    - Currency
    - Unit system
    - First day of week
    - Date and time formats
    - Palette
    - Default counting unit
    - Worker sale allocation
    - Discount ceiling
  contexts:
    web:
      place: farm-web::setup::farm-settings
- text: The Product saves every setting together
  kind: product
  actor: owner
  entities:
  - entity: farm
    effect: changes
    facts:
    - Name
    - Timezone
    - Locale
    - Currency
    - Unit system
    - First day of week
    - Date and time formats
    - Palette
    - Default counting unit
    - Worker sale allocation
    - Discount ceiling
  contexts:
    web:
      place: farm-web::setup::farm-settings
---

# Save farm settings

## Trigger

The Owner sets up or changes how the farm works.

## Outcome

Everyone on the farm reads with the new settings; capture screens follow a new timezone on their next render.

## Edge cases

- Choosing a palette previews it in the Owner's app; leaving without saving restores the saved one.
- A timezone the server cannot resolve, or a locale without a region such as en rather than en-US, is refused.
- Someone saving against a stale copy of the settings is refused and reloads.
