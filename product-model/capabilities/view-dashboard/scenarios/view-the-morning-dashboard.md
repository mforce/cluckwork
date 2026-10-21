---
kind: primary
routes:
  web: Web
steps:
- text: The Worker opens the Dashboard
  kind: actor
  actor: worker
  entities: []
  contexts:
    web:
      place: farm-web::farm::dashboard
- text: The Product shows each active flock's entry status for today with the ones without an entry first, today's eggs, stock by grade with any below its floor, recent orders and the lay rate
  kind: product
  actor: worker
  entities:
  - entity: daily-entry
    effect: reads
    facts:
    - Date
    - Total eggs
  - entity: flock
    effect: reads
    facts:
    - Name
  - entity: egg-grade
    effect: reads
    facts:
    - Name
    - Low-stock floor
  - entity: egg-lot
    effect: reads
    facts:
    - Available
  - entity: sales-order
    effect: reads
    facts:
    - Reference
    - Order date
    - Total
  - entity: customer
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::farm::dashboard
---

# View the morning dashboard

## Trigger

Someone starts the farm day.

## Outcome

They see which houses still need recording; a house with no entry links to Daily entry for that flock and day.

## Edge cases

- A Draft counts as an entry for today; a voided one does not.
- The lay rate counts submitted days only and ends yesterday or earlier.
- A day where some flocks recorded and others did not is drawn as partial and left out of the peak and average.
