---
appliesTo:
- type: entity
  id: packed-unit
  effect: changes
permits:
- actors:
  - owner
  - manager
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/AuthPolicies.cs#AuthPolicies
---

# Only Owners and Managers change a packed unit

Packed units are an operational catalog.
