---
availability:
- place: farm-web::setup
domain: farm-settings
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Farm/Accounts/UpdateFarmSettings/UpdateFarmSettingsHandler.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Farm/Accounts/UpdateFarmSettings/UpdateFarmSettingsValidator.cs
- kind: code
  role: implementation
  target: web/src/routes/SettingsPage.tsx
---

# Change farm settings

Save the farm's name, timezone, locale, currency, unit system, first day of week, date and time formats, palette, default counting unit, worker sale allocation and discount ceiling. The timezone decides the farm's operational day the moment it is saved; locale, unit system and the format overrides are stored and validated for display work that does not use them yet.
