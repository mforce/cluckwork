---
availability:
- place: farm-web::setup
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Insights/Audit/AuditEndpoints.cs
- kind: code
  role: implementation
  target: web/src/routes/AuditPage.tsx
- kind: code
  role: implementation
  target: web/src/routes/AuditFilters.tsx
---

# Browse the audit log

Read the farm's audit log filtered by record type, action, UTC day range and connected app, or every event on one record from that record's Audit history link.
