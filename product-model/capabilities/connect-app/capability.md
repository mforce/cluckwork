---
availability:
- place: farm-web::farm
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/OAuth/OAuthEndpoints.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/OAuth/ConsentSelection.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Hosting/CluckworkMcp.cs#UsableScopesAsync
- kind: code
  role: implementation
  target: web/src/routes/ConnectPage.tsx
- kind: doc
  role: context
  target: docs/decisions/798-oauth-consent.md
- kind: doc
  role: intent
  target: specs/product/GLOSSARY.md
---

# Connect an app

Approve an outside app to act for oneself through OAuth, ticking which of the permissions it asks for to grant and re-entering one's current password. The app gets the person's role and flock scope and only the permissions ticked, never more, and its access never expires on its own. A permission the person's role cannot use is shown as not allowed and is never granted.
