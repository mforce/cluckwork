---
appliesTo:
- type: entity
  id: egg-grade
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
  target: src/Cluckwork.Api/Modules/EggOperations/EggGrades/EggGradeEndpoints.cs
---

# Only Owners and Managers change an egg grade

Editing, deactivating and activating grades are operational catalog work.
