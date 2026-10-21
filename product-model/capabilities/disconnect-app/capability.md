---
availability:
- place: farm-web::farm
- place: farm-web::setup
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/Me/MeEndpoints.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/Users/UserEndpoints.cs
- kind: code
  role: implementation
  target: web/src/routes/ConnectedAppsPage.tsx
---

# Disconnect an app

End a connected app's access: every approval the person gave it and every token from them is revoked, and the app is refused on its next request. People disconnect their own apps from Account; the Owner disconnects anyone's from Setup.
