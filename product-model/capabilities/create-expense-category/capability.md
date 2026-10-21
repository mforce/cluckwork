---
availability:
- place: farm-web::farm
domain: expenses
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Finance/Expenses/CreateExpenseCategory/CreateExpenseCategoryHandler.cs
---

# Create an expense category

Add a bucket for spending with a name unique within the farm.
