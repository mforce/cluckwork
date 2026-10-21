---
appliesTo:
- type: entity
  id: sales-order
  effect: changes
  to: Voided
permits:
- actors:
  - owner
  - manager
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/AuthPolicies.cs#AuthPolicies
---

# Only Owners and Managers void a sales order

Undoing a confirmed sale is the corrective tier.
