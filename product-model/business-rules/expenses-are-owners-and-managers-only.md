---
appliesTo:
- type: entity
  id: expense
  effect: reads
permits:
- actors:
  - owner
  - manager
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Endpoints/CluckworkEndpointGroups.cs
---

# Only Owners and Managers read expenses

Expenses are admin-only, reads included: the money and production split. Recording one follows the same split.
