---
availability:
- place: farm-web::farm
- place: farm-mcp
domain: expenses
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Finance/Expenses/CreateExpense/CreateExpenseHandler.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Finance/Expenses/CreateExpense/CreateExpenseValidator.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Mcp/ExpenseTools.cs
---

# Record an expense

Record money spent: date, category, description, amount, an optional flock and note. The currency is the farm's at the time and never changes. A connected app with the Record expenses permission records expenses too, and they count at once.
