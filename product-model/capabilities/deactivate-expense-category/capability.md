---
availability:
- place: farm-web::farm
domain: expenses
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Finance/Expenses/UpdateExpenseCategory/UpdateExpenseCategoryHandler.cs
---

# Deactivate an expense category

Hide a category from new expenses; recorded expenses keep it.
