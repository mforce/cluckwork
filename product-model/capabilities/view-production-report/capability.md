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
  target: src/Cluckwork.Infrastructure/Modules/Insights/Repositories/ReportQueries.Production.cs
- kind: code
  role: implementation
  target: web/src/routes/ReportsPage.tsx
- kind: code
  role: implementation
  target: web/src/routes/reports/ProductionTab.tsx
- kind: code
  role: implementation
  target: web/src/routes/reports/periodComparison.ts
- kind: doc
  role: intent
  target: specs/product/GLOSSARY.md
---

# View the production report

See official production per day over a range, defaulting to the last seven days and capped at 366: eggs, losses, sellable, condition, deaths, hen-days, recorded hen-days, rated eggs and hen-day percentage, with period totals, the period's sellable and egg loss percentages, grade totals and a breakdown by flock. Each headline figure is compared with the previous period of the same length. Drafts and voided days do not count.
