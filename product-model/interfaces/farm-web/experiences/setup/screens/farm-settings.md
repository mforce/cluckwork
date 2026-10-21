---
entities:
- entity: farm
  shows:
  - Name
  - Farm code
  - Timezone
  - Locale
  - Currency
  - Unit system
  - First day of week
  - Date and time formats
  - Palette
  - Default counting unit
  - Worker sale allocation
  - Discount ceiling
  - Logo
  - Banner
  collects:
  - Name
  - Timezone
  - Locale
  - Currency
  - Unit system
  - First day of week
  - Date and time formats
  - Palette
  - Default counting unit
  - Worker sale allocation
  - Discount ceiling
  - Logo
  - Banner
entryPoints:
- farm-web: /settings
references:
- kind: code
  role: implementation
  target: web/src/routes/SettingsPage.tsx
---

# Farm settings

The Owner's settings for how the farm reads and behaves, and its logo and banner.
