---
appliesTo:
- type: capability
  id: confirm-sales-order
- type: entity
  id: farm
  facts:
  - Discount ceiling
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/Commerce/Contracts/DiscountCeiling.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/Commerce/Sales/SalesOrder.cs#FindCeilingBreach
---

# Sales users and Workers cannot confirm a line discounted beyond the farm's ceiling

When the farm sets a discount ceiling, a Sales user or Worker confirming an order with a line further below list than the ceiling is refused, naming the line's grade; exactly at the ceiling is allowed, and zero means no discount. Owners and Managers confirm the same order untouched.
