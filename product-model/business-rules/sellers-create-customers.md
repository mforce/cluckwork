---
appliesTo:
- type: entity
  id: customer
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
  target: src/Cluckwork.Api/Modules/Commerce/Customers/CustomerEndpoints.cs
---

# Owners, Managers, Sales and Workers create a customer

Building orders is open to Workers, so adding the customer is too; Read-only people cannot.
