---
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/Finance/Expenses/Expense.cs#Expense
---

# Expense

A money-out record.

## Information kept

- **Date** — the farm day spent, never in the future
- **Description** — what the money was spent on
- **Amount** — in the farm currency at the time
- **Currency** — snapshotted at creation and never editable
- **Flock** — an optional flock the expense belongs to
- **Note** — optional
