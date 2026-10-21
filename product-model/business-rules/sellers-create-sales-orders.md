---
appliesTo:
- type: entity
  id: sales-order
  effect: creates
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
  target: src/Cluckwork.Api/Modules/Commerce/Sales/SaleEndpoints.cs
---

# Owners, Managers, Sales and Workers create a sales order

Selling the day's eggs is recording the day's work, so Workers build orders; Read-only people cannot.
