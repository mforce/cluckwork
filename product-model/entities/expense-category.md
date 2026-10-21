---
relations:
- entity: expense
  verb: classifies
  cardinality: one-to-many
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/Finance/Expenses/ExpenseCategory.cs
---

# Expense category

A farm's bucket for spending, such as Feed or Vet.

## Information kept

- **Name** — unique within the farm, ignoring case

## States

### Active

Offered for new expenses.

### Inactive

Hidden from new expenses; recorded expenses keep it.
