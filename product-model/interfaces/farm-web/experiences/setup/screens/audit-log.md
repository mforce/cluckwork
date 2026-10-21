---
entities:
- entity: audit-event
  shows:
  - Actor
  - When
  - Action
  - Record
  - Reason
  - Details
  - Connected app
entryPoints:
- farm-web: /audit
references:
- kind: code
  role: implementation
  target: web/src/routes/AuditPage.tsx
---

# Audit log

The farm's audit log, filtered by record type, action, date range and connected app, or scoped to one record from its Audit history link.
