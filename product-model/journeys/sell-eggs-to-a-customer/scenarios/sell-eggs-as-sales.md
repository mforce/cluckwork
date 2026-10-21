---
kind: primary
result: achieved
routes:
  web: Web
steps:
- text: The Sales person chooses New order, picks the customer and the date
  kind: actor
  actor: sales
  capability: create-sales-order
  entities:
  - entity: customer
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::farm::sales
- text: The Product creates the Draft order and opens it
  kind: product
  actor: sales
  capability: create-sales-order
  entities:
  - entity: sales-order
    effect: creates
    to: Draft
    facts:
    - Reference
    - Order date
    - Currency
    - Total
  - entity: customer
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::sales
- text: The Sales person adds a line for a product
  kind: actor
  actor: sales
  capability: add-order-line
  entities:
  - entity: product
    effect: reads
    facts:
    - Name
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Product adds the line and recalculates the total
  kind: product
  actor: sales
  capability: add-order-line
  entities:
  - entity: sales-order
    effect: changes
    from: Draft
    to: Draft
    facts:
    - Lines
    - Total
  - entity: product
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Sales person confirms the order
  kind: actor
  actor: sales
  capability: confirm-sales-order
  entities:
  - entity: sales-order
    effect: reads
    facts:
    - Lines
    - Total
  contexts:
    web:
      place: farm-web::farm::sales-order
- text: The Product allocates the eggs from the oldest unrestricted lots and confirms the order
  kind: product
  actor: sales
  capability: confirm-sales-order
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
  contexts:
    web:
      place: farm-web::farm::sales-order
---

# Sell eggs as a Sales user

## Trigger

A customer asks for eggs.

## Outcome

The customer's order is confirmed.
