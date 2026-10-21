---
entities:
- entity: daily-entry
  shows:
  - Date
  - Total eggs
  - Cracked
  - Dirty
  - Discarded
  - Sellable
  - Mortality
  - Grading
- entity: flock
  shows:
  - Name
  - Placement date
  - Current birds
- entity: sales-order
  shows:
  - Lines
  - Total
  - Outstanding
- entity: expense
  shows:
  - Amount
- entity: feed-usage
  shows:
  - Estimated cost
entryPoints:
- farm-web: /reports
references:
- kind: code
  role: implementation
  target: web/src/routes/ReportsPage.tsx
- kind: code
  role: implementation
  target: web/src/routes/reports/ProductionTab.tsx
- kind: code
  role: implementation
  target: web/src/routes/reports/MoneyTab.tsx
---

# Reports

Production and money over a chosen range, each figure beside its change against the previous period. The Production tab holds daily production with hen-day percentage, the period's sellable and egg loss percentages, a breakdown by flock and grade totals. The Money tab, for Owners and Managers, holds revenue, eggs sold, expenses, feed cost per dozen and basic profit.
