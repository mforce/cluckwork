---
entities:
- entity: user
  shows:
  - Email
  - Display name
  - UI language
  - Counting unit
  collects:
  - Password
  - UI language
  - Counting unit
- entity: app-connection
  shows:
  - Read farm data
  - Record daily entries
  - Submit daily entries
  - Record farm operations
  - Record expenses
  - Connected
  - Last used
- entity: ai-agent
  shows:
  - App name
entryPoints:
- farm-web: /account
references:
- kind: code
  role: implementation
  target: web/src/routes/AccountPage.tsx
---

# Account

A person's own account: their password, their language and counting unit, and the connected apps they allowed.
