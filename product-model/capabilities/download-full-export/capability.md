---
availability:
- place: farm-web::setup
domain: export
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Insights/Export/ExportEndpoints.cs
---

# Download a full export

Download every dataset of the farm in one zip with a manifest of row counts.
