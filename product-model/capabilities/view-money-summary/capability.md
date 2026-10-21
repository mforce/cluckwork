---
availability:
- place: farm-web::farm
domain: reports
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Insights/Reports/ReportEndpoints.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Infrastructure/Modules/Insights/Repositories/ReportQueries.cs
- kind: code
  role: implementation
  target: web/src/routes/ReportsPage.tsx
- kind: code
  role: implementation
  target: web/src/routes/reports/MoneyTab.tsx
- kind: code
  role: implementation
  target: web/src/routes/reports/periodComparison.ts
- kind: doc
  role: intent
  target: specs/product/GLOSSARY.md
---

# View the money summary

See, on the Money tab of Reports for a range, confirmed order revenue with what was paid and is outstanding, the eggs sold on those orders with a split by grade, recorded expenses by category, feed cost per dozen eggs collected, and basic profit: confirmed revenue less recorded expenses, with no cost of goods. Revenue, eggs sold, expenses, feed cost per dozen and profit are each compared with the previous period of the same length.
