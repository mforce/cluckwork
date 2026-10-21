---
appliesTo:
- type: capability
  id: view-production-report
- type: capability
  id: view-dashboard
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Infrastructure/Modules/Insights/Repositories/ReportQueries.Production.cs
- kind: doc
  role: intent
  target: specs/product/GLOSSARY.md
---

# Hen-day percentage is rated only over the flocks that recorded the day

Hen-day percentage is rated eggs over recorded hen-days, counting only flocks with an official entry that day and whose birds the bird ledger holds for that date; a bird movement takes effect from the next day. The same rule rates each flock in the production report's flock breakdown over its own recorded hen-days. A day or flock nobody recorded has no figure, and a figure above 100% is shown and flagged rather than capped.
