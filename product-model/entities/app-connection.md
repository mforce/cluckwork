---
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Access/Contracts/AppConnection.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Modules/Access/Contracts/OAuthScopes.cs
- kind: doc
  role: context
  target: docs/decisions/799-connected-apps.md
- kind: doc
  role: context
  target: docs/decisions/1311-mcp-write-tools.md
---

# App connection

One person's approval of one connected app: what the app may do for them. The app acts with the person's own role and flock scope, never more. A permission stays allowed once any approval allowed it: approving the app again with fewer permissions takes nothing away, and only disconnecting does.

## Information kept

- **Read farm data** — Allowed or Not allowed: the app may read flocks, stock, egg lots, daily entries, customers and orders within the person's role
- **Record daily entries** — Allowed or Not allowed: the app may record a flock's day as a Draft
- **Submit daily entries** — Allowed or Not allowed: the app may submit a Draft day, which puts its eggs in stock
- **Record farm operations** — Allowed or Not allowed: the app may record feed use, water use and inventory purchases, which count at once
- **Record expenses** — Allowed or Not allowed: the app may record expenses, which count at once
- **Connected** — when the person approved the app
- **Last used** — when the app last made a request that passed the farm and credential checks, recorded at most every 15 minutes
