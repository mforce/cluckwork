---
appliesTo:
- type: capability
  id: void-sales-order
- type: entity
  id: sales-order
  facts:
  - Allocations
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Commerce/Sales/VoidSale/VoidSaleHandler.cs#VoidSaleHandler
---

# Voiding a sale returns every egg to the lot it was drawn from

The recorded allocations decide where eggs go back, preserving oldest-first order and any restriction. The voided order keeps its lines and total.
