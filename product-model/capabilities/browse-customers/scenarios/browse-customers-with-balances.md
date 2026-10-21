---
kind: primary
routes:
  web: Web
steps:
- text: The Manager opens Customers
  kind: actor
  actor: manager
  entities:
  - entity: customer
    effect: reads
    facts: []
  contexts:
    web:
      place: farm-web::farm::customers
- text: The Product lists the customers with their contact details and each one's outstanding balance across confirmed orders
  kind: product
  actor: manager
  entities:
  - entity: customer
    effect: reads
    facts:
    - Name
    - Phone
    - Email
    - Address
    - Note
    - Outstanding balance
  contexts:
    web:
      place: farm-web::farm::customers
---

# Browse customers and what they owe

## Trigger

An Owner or Manager reviews who the farm sells to.

## Outcome

They see every customer and what each owes; a customer's name links to their orders on Sales.

## Edge cases

- Sales users and Workers see the list without the balance column.
