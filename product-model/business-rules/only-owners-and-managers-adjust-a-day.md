---
appliesTo:
- type: entity
  id: daily-entry
  effect: changes
  to: Adjusted
permits:
- actors:
  - owner
  - manager
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/AuthPolicies.cs#AuthPolicies
---

# Only Owners and Managers adjust an official daily entry

Correcting a day after it became official is the corrective tier.
