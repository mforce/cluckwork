---
appliesTo:
- type: entity
  id: product
  effect: creates
permits:
- actors:
  - owner
  - manager
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/AuthPolicies.cs#AuthPolicies
---

# Only Owners and Managers create a product

Products are an operational catalog.
