---
availability:
- place: farm-web::setup
domain: farm-settings
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Farm/Accounts/FarmLogoEndpoints.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/Farm/Media/ImageSanitizer.cs
---

# Set the farm logo

Upload the image shown as branding in the app's chrome: a still PNG, JPEG or WebP within the size cap and 4096 pixels a side. The image is rebuilt, dropping embedded metadata and anything appended after it.
