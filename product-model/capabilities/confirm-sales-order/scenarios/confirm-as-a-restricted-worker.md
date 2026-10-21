---
kind: alternative
routes:
  web: Web
steps:
- text: The Worker, restricted by assignment, confirms a Draft order
  kind: actor
  actor: worker
  entities:
  - entity: sales-order
    effect: reads
    facts:
    - Lines
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Product draws the order from lots according to the farm's worker sale allocation and confirms it
  kind: product
  actor: worker
  entities:
  - entity: egg-lot
    effect: changes
    facts:
    - Available
  - entity: sales-order
    effect: changes
    from: Draft
    to: Confirmed
    facts:
    - Allocations
  - entity: farm
    effect: reads
    facts:
    - Worker sale allocation
  contexts:
    web:
      place: farm-web::farm::sales-order
---

# Confirm as a Worker with assigned flocks

## Trigger

A Worker restricted to some flocks sells the day's eggs.

## Outcome

The order is confirmed from the lots the farm's policy lets that Worker draw.

## Decision points

### Worker sale allocation

Which flocks may the restricted Worker's sale draw from?

- Assigned flocks only → only the lots of the Worker's own assigned flocks are drawn
- All farm flocks → lots of every flock on the farm are drawn, oldest first
