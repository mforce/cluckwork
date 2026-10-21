---
availability:
- place: farm-web::setup
domain: export
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Insights/Export/ExportEndpoints.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Insights/Export/CsvExport.cs
---

# Download a dataset

Download one of the farm's datasets as a CSV file: a copy of what is stored, with money in minor units beside its currency.
