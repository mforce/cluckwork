---
kind: validation
routes:
  web: Web
steps:
- text: The Owner picks another currency after the farm has recorded any amount in the current one
  kind: actor
  actor: owner
  entities:
  - entity: farm
    effect: reads
    facts:
    - Currency
  contexts:
    web:
      place: farm-web::setup::farm-settings
- text: The Product shows the currency locked with the reason and refuses the whole save
  kind: condition
  actor: owner
  entities:
  - entity: farm
    effect: reads
    facts:
    - Currency
  contexts:
    web:
      place: farm-web::setup::farm-settings
---

# Refuse a currency change once amounts exist

## Trigger

A currency change would leave the books in two denominations.

## Outcome

No setting is saved.

## Edge cases

- While nothing has been recorded, changing the currency re-derives its symbol and minor unit.
