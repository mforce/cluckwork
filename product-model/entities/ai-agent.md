---
kind: system
acts: external
relations:
- entity: app-connection
  verb: holds
  cardinality: one-to-many
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/Access/OAuth/OAuthEndpoints.cs
- kind: doc
  role: context
  target: docs/decisions/1148-client-id-metadata-documents.md
---

# Connected app

An outside app, such as an AI assistant, that connects to the farm's MCP address and acts for a person who approved it. Its name is self-chosen at registration.

## Information kept

- **App name** — the name the app gave itself; shown as plain text and not proof of who made it
- **Verified domain** — the website the app's details came from when it identified itself with a published client metadata document; absent for a self-registered app, which is marked Unverified app
- **Return addresses** — where the browser returns after an approval
