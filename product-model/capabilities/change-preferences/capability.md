---
availability:
- place: farm-web::farm
domain: account
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/Me/MeEndpoints.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Access/Users/SetLanguage/SetLanguageHandler.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Access/Users/SetStepperUnit/SetStepperUnitHandler.cs
---

# Change preferences

Choose one's own interface language, English, Spanish or Tagalog, and the counting unit Daily entry steppers count by, which follows the person across devices.
