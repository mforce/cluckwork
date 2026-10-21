---
entities:
- entity: expense
  shows:
  - Date
  - Description
  - Amount
  - Currency
  - Flock
  - Note
  collects:
  - Date
  - Description
  - Amount
  - Flock
  - Note
- entity: expense-category
  shows:
  - Name
  collects:
  - Name
entryPoints:
- farm-web: /expenses
references:
- kind: code
  role: implementation
  target: web/src/routes/ExpensesPage.tsx
---

# Expenses

The farm's spending for a period with its total, where expenses are recorded and corrected and categories are managed. It opens on the current farm month.
