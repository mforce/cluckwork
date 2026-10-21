---
entities:
- entity: farm
  shows:
  - Connected apps
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
- entity: user
  shows:
  - Email
entryPoints:
- farm-web: /connected-apps
references:
- kind: code
  role: implementation
  target: web/src/routes/ConnectedAppsPage.tsx
---

# Connected apps

The Owner's page for the farm's connected apps: the Allow connected apps switch and every app connection on the farm, filterable by person.
