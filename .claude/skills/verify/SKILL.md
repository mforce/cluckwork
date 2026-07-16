---
name: verify
description: Build, launch, and drive the Cluckwork app (API + SPA) locally to verify a change end-to-end in a real browser.
---

# Verifying Cluckwork changes at runtime

## Launch the stack

```sh
# 1. Dev Postgres (loopback 5432, creds cluckwork/cluckwork/cluckwork)
docker compose -f deploy/docker-compose.dev.yml up -d --wait

# 2. API — MUST set ASPNETCORE_ENVIRONMENT=Development or user-secrets
#    (connection string, Seed:*, JWT keys) don't load and startup crashes
#    with "ConnectionString property has not been initialized".
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://127.0.0.1:8080 \
  dotnet run --project src/Cluckwork.Api --no-launch-profile

# 3. SPA dev server (proxies /api -> 8080)
cd web && npm run dev   # http://localhost:5173
```

Login credentials come from user-secrets: `dotnet user-secrets list --project src/Cluckwork.Api | grep '^Seed:'` (email `admin@cluckwork.local`). Don't print the password into reports.

## Drive the browser

Playwright MCP fails here (wants Google Chrome at /opt/google/chrome; machine has chromium). Instead script `playwright-core` against system chromium:

```sh
cd <scratchpad> && npm i playwright-core
```

```js
import { chromium } from "playwright-core";
const browser = await chromium.launch({ executablePath: "/usr/bin/chromium" });
```

Login flow: fill `getByLabel(/email/i)` + `getByLabel(/password/i)`, click `getByRole("button", { name: /sign in/i })`, then wait for the target heading. Collect `page.on("pageerror")` + console errors; screenshot for evidence.

## API-level seeding

Get a token once, then curl with `Authorization: Bearer` + `Idempotency-Key: $(uuidgen)` on every POST/PUT/DELETE (middleware rejects writes without it). Useful for setting up scenario data (flocks, entries, orders) faster than clicking.

## Gotchas

- Dev DB persists between sessions — expect residual data; create what the scenario needs rather than assuming empty state.
- First cold page load may log one or two transient 404 console errors (vite dev asset probing); ignore unless reproducible on reload.
- Teardown: kill dotnet/vite background tasks, `docker compose -f deploy/docker-compose.dev.yml down`.
