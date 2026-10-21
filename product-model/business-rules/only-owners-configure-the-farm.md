---
appliesTo:
- type: entity
  id: farm
  effect: changes
  facts:
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
  - Connected apps
  - Logo
  - Banner
permits:
- actors:
  - owner
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Farm/Accounts/AccountEndpoints.cs
- kind: doc
  role: context
  target: docs/decisions/729-owner-only-farm-configuration.md
---

# Only an Owner changes the farm's settings, branding and connected apps switch

Farm configuration and identity are the Owner's; Managers cannot change the settings, logo or banner. Every signed-in person reads the formatting and branding values.
