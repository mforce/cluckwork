---
appliesTo:
- type: entity
  id: flock
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

# Only Owners and Managers change a flock

Editing, depleting, archiving and reactivating a flock are administration.
