---
kind: primary
routes:
  web: Web
steps:
- text: The Manager chooses write off on a lot, picks Discard or InternalUse, enters the eggs and a reason
  kind: actor
  actor: manager
  entities:
  - entity: egg-lot
    effect: reads
    facts:
    - Available
  contexts:
    web:
      place: farm-web::farm::stock
- text: The Product removes the eggs from the lot's available eggs and records the correction in its ledger
  kind: product
  actor: manager
  entities:
  - entity: egg-lot
    effect: changes
    facts:
    - Available
  contexts:
    web:
      place: farm-web::farm::stock
---

# Write off lost eggs

## Trigger

Eggs were broken, spoiled or used on the farm.

## Outcome

The lot's available eggs drop; production and hen-day figures are unchanged.

## Edge cases

- A restricted lot can still be written off; removing stock is the safe direction.
