---
appliesTo:
- type: entity
  id: flock-assignment
  effect: creates
permits:
- actors:
  - owner
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/Users/UserEndpoints.cs
---

# Only an Owner adds a flock assignment

Flock scope widens or narrows a Worker's durable access.
