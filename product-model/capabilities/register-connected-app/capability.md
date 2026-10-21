---
availability:
- place: farm-mcp
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/OAuth/OAuthEndpoints.cs
- kind: doc
  role: context
  target: docs/decisions/797-oauth-client-registration.md
- kind: doc
  role: context
  target: docs/decisions/1148-client-id-metadata-documents.md
---

# Register a connected app

An outside app introduces itself to the farm's server before it can ask anyone for access, either by registering a name and return addresses or by identifying itself with the https address of a client metadata document the server fetches. Registering grants nothing: the app can only send a person to the consent screen.
