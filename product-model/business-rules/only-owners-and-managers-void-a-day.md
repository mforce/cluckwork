---
appliesTo:
- type: entity
  id: daily-entry
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

# Only Owners and Managers void an official daily entry

Undoing a day is the corrective tier.
