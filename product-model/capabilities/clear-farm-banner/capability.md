---
availability:
- place: farm-web::setup
domain: farm-settings
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Farm/Accounts/FarmBannerEndpoints.cs
---

# Clear the farm banner

Remove the banner; the splash is then skipped entirely.
