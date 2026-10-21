---
availability:
- place: farm-web::farm
domain: sales
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Commerce/Sales/AddOrderItem/AddOrderItemHandler.cs#AddOrderItemHandler
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/Commerce/Sales/SalesOrderItem.cs
- kind: code
  role: implementation
  target: web/src/routes/sales/AddLineForm.tsx
- kind: doc
  role: context
  target: docs/decisions/1160-unit-scaled-list-price.md
---

# Add an order line

Add a product to a Draft order in one of the farm's packed units. The line keeps the grade the product draws from, the unit's eggs per unit and the product's list price scaled to that unit as they are at that moment.
