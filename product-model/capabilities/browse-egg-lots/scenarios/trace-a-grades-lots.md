---
kind: primary
routes:
  web: Web
steps:
- text: The Read-only person opens a grade's lots on Stock and narrows them by production date
  kind: actor
  actor: read-only
  entities:
  - entity: egg-grade
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::farm::stock
- text: The Product lists the grade's lots with production date, flock, produced, available and restricted-until
  kind: product
  actor: read-only
  entities:
  - entity: egg-lot
    effect: reads
    facts:
    - Production date
    - Flock
    - Produced
    - Available
    - Restricted until
  - entity: flock
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::stock
- text: The Read-only person opens a lot's history
  kind: actor
  actor: read-only
  entities:
  - entity: egg-lot
    effect: reads
    facts:
    - Production date
  contexts:
    web:
      place: farm-web::farm::stock
- text: The Product shows every movement of the lot, whose running sum equals its available eggs
  kind: product
  actor: read-only
  entities:
  - entity: egg-movement
    effect: reads
    facts:
    - When
    - Type
    - Change
    - Reason
  contexts:
    web:
      place: farm-web::farm::stock
---

# Trace a grade's lots and a lot's movements

## Trigger

Someone needs to know where a batch of eggs came from and where it went.

## Outcome

They see the lot's production, each sale that drew from it, any void that returned eggs and every correction.
