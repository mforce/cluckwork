---
appliesTo:
- type: entity
  id: sales-order
  effect: changes
permits:
- actors:
  - owner
  - manager
  - sales
  - worker
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/AuthPolicies.cs#AuthPolicies
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Commerce/Sales/ConfirmSale/ConfirmSaleHandler.cs
---

# Owners, Managers, Sales and Workers change a sales order

Anyone who sells edits a Draft's lines, cancels it or confirms it. Each confirmation re-reads the seller's role at that moment.
