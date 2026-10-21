---
appliesTo:
- type: entity
  id: customer
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
---

# Owners, Managers, Sales and Workers change a customer

Customer details are edited by anyone who sells.
