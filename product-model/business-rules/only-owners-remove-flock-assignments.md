---
appliesTo:
- type: entity
  id: flock-assignment
  effect: removes
permits:
- actors:
  - owner
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/Users/UserEndpoints.cs
---

# Only an Owner removes a flock assignment

Removing the last assignment restores farm-wide Worker scope.
