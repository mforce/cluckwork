---
appliesTo:
- type: capability
  id: confirm-sales-order
- type: entity
  id: sales-order
  facts:
  - Discount reason
  - Discount reason note
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/Commerce/Sales/SalesOrder.cs#CheckCanConfirm
---

# An order sold below list price is confirmed only with a discount reason

An order with at least one line below its comparable list price cannot be confirmed without a reason, and an order with none refuses a reason, so the field never claims a discount that was not given.
