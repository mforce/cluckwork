---
entities:
- entity: ai-agent
  shows:
  - App name
  - Verified domain
  - Return addresses
- entity: app-connection
  shows:
  - Read farm data
  - Record daily entries
  - Submit daily entries
  - Record farm operations
  - Record expenses
  collects:
  - Read farm data
  - Record daily entries
  - Submit daily entries
  - Record farm operations
  - Record expenses
entryPoints:
- farm-web: /connect
references:
- kind: code
  role: implementation
  target: web/src/routes/ConnectPage.tsx
---

# Connect an app

The consent screen a connected app sends a person to: the app's name with its verified domain or an Unverified app marker, each permission it asks to use with a checkbox, and where the browser returns.
