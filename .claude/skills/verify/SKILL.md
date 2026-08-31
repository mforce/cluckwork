---
name: verify
description: Build, launch, and drive the Cluckwork app (API + SPA) locally to verify a change end-to-end in a real browser.
---

# Verifying Cluckwork changes at runtime

## Launch the stack

**First: is an Aspire AppHost already running?** Ask the AppHost, not the container names:

```sh
pgrep -af 'Cluckwork\.AppHost'                  # the AppHost process itself
ss -ltn | grep -E ':18888\b'                    # its pinned dashboard (#565)
aspire describe --apphost src/Cluckwork.AppHost/Cluckwork.AppHost.csproj \
  --format Json --non-interactive               # the endpoints THIS run actually holds
```

Do **not** detect it with `docker ps | grep -E 'postgres-|redis-'` — that matches
`deploy-redis-1` and `cluckwork-sim-redis-1` too, so Compose or the sim stack reads as Aspire.
And do not treat `5433`/`6380`/`8080`/`5173` as more than **committed defaults**: `LocalPorts:*`
can be overridden per machine or set empty for a random port, so a running AppHost can be sitting
on none of them. `aspire describe` is the authority for a given run; the dashboard port is pinned
and so is a reliable presence check.

If one is running it already owns the API and SPA ports, so the manual launch below collides with
it, and Aspire's Postgres is a **different database** with a generated credential (#565). Either
drive the running Aspire stack at its advertised web endpoint and skip steps 1-3 entirely, or stop
the AppHost first. Do not run both.


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

A freshly reset database has no admin at all — provision one with
[first-admin provisioning](../../../docs/runbooks/first-admin-provisioning.md) (form 4 if the
database is Aspire's), then use that generated password rather than `Seed:*`.

Login credentials depend on which stack you are driving, and the two are not interchangeable:

- **Compose dev database, seeded** — `dotnet user-secrets list --project src/Cluckwork.Api | grep '^Seed:'`
  (email `admin@cluckwork.local`). These are the **Compose** credentials only; they do not exist in
  Aspire's database.
- **Aspire's database, or any freshly reset one** — there is no `Seed:*` answer. The sign-in password
  is the one `bootstrap-admin` printed **to stdout on the run that created the Owner** (form 4), and
  it is not recoverable afterwards: it is stored hashed and never logged. If it was not captured, mint
  a new one with `recover-admin` rather than hunting for it. The AppHost user-secret
  `Parameters:postgres-password` is the **database** password — it is what form 4 passes in
  `ConnectionStrings__Default`, and it is never a login credential.

Don't print either password into reports.

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
