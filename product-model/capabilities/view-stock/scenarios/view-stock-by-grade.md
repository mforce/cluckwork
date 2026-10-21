---
kind: primary
routes:
  web: Web
steps:
- text: The Read-only person opens Stock
  kind: actor
  actor: read-only
  entities: []
  contexts:
    web:
      place: farm-web::farm::stock
- text: The Product totals each grade's lots into available and restricted eggs as of the farm's today and compares available eggs with the grade's floor
  kind: product
  actor: read-only
  entities:
  - entity: egg-lot
    effect: reads
    facts:
    - Available
    - Restricted until
  - entity: egg-grade
    effect: reads
    facts:
    - Name
    - Low-stock floor
  - entity: farm
    effect: reads
    facts:
    - Timezone
  contexts:
    web:
      place: farm-web::farm::stock
- text: Each grade below its floor is flagged with its shortfall
  kind: condition
  entities:
  - entity: egg-grade
    effect: reads
    facts:
    - Low-stock floor
  contexts:
    web:
      place: farm-web::farm::stock
---

# View stock by grade

## Trigger

Someone checks what can be sold.

## Outcome

They see stock per grade, with restricted eggs shown but not counted as available.

## Edge cases

- A grade with a floor appears even with no lots, at a zero balance.
- A deactivated grade keeps its stock row but its floor is out of service.
- A floor of zero never warns; a grade with no floor never warns.
