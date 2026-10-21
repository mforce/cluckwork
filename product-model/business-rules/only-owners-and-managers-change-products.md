---
appliesTo:
- type: entity
  id: product
  effect: changes
permits:
- actors:
  - owner
  - manager
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/AuthPolicies.cs#AuthPolicies
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Commerce/Catalog/ProductEndpoints.cs
---

# Only Owners and Managers change a product

Editing, deactivating and activating products are operational catalog work.
