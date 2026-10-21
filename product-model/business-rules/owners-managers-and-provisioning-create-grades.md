---
appliesTo:
- type: entity
  id: egg-grade
  effect: creates
permits:
- actors:
  - owner
  - manager
- actors:
  - operator
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/AuthPolicies.cs#AuthPolicies
---

# Owners, Managers and farm provisioning create egg grades

Grades are an operational catalog; a new farm receives its ten standard grades when an operator provisions it.
