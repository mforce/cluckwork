---
appliesTo:
- type: entity
  id: audit-event
  effect: reads
permits:
- actors:
  - owner
  - manager
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Endpoints/CluckworkEndpointGroups.cs
---

# Only Owners and Managers read the audit log

The full cross-cutting trail is admin-only; a record's own Created by and Last changed by line is shown to whoever reads the record.
