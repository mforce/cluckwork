---
availability:
- place: farm-web::setup
domain: connected-apps
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Farm/Accounts/SetConnectedApps/SetConnectedAppsHandler.cs
- kind: doc
  role: context
  target: docs/decisions/1146-connected-apps-switch.md
---

# Turn off connected apps

Switch Allow connected apps off for the whole farm. Every app is refused on its next request with Connected apps are off and the consent screen refuses every app; nothing is disconnected.
