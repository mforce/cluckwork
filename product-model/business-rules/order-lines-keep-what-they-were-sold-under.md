---
appliesTo:
- type: capability
  id: add-order-line
- type: entity
  id: sales-order
  facts:
  - Lines
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/Commerce/Sales/SalesOrderItem.cs
---

# An order line keeps the grade, eggs per unit and list price it was added with

Re-pointing a product's grade, redefining a packed unit or re-pricing the catalogue affects only lines added afterwards. Quantities are whole units, and allocation runs on units times eggs per unit.
