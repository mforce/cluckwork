---
appliesTo:
- type: entity
  id: expense-category
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

# Only Owners and Managers change an expense category

Categories belong to the expense tier.
