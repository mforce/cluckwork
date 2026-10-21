---
kind: validation
routes:
  web: Web
steps:
- text: The Worker logs more of an item than its lots held on that day
  kind: actor
  actor: worker
  entities:
  - entity: inventory-item
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::farm::feed
- text: The Product refuses the usage, stating how much was in stock that day
  kind: condition
  actor: worker
  entities:
  - entity: inventory-lot
    effect: reads
    facts:
    - Remaining
  - entity: inventory-item
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::feed
---

# Refuse feed beyond the stock on hand that day

## Trigger

The recorded quantity exceeds what was on hand.

## Outcome

Nothing is recorded.

## Edge cases

- An item outside Feed, Supplement and Additive cannot be fed to a flock.
- A day the flock does not accept, or a day after the farm's today, is refused.
