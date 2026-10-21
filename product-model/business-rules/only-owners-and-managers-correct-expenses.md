---
appliesTo:
- type: entity
  id: expense
  effect: changes
permits:
- actors:
  - owner
  - manager
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Finance/Expenses/ExpenseEndpoints.cs
---

# Only Owners and Managers correct an expense

Corrections are made in place under a version check.
