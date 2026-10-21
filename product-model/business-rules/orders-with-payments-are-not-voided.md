---
appliesTo:
- type: capability
  id: void-sales-order
- type: entity
  id: payment
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Commerce/Sales/VoidSale/VoidSaleHandler.cs#VoidSaleHandler
---

# A confirmed order with live payments cannot be voided

Each payment is voided first, with its own reason.
