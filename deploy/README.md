# Deployment

Cluckwork ships as a **single origin**: one container runs the .NET API, which
serves both the JSON API (`/api/*`) and the built React SPA (from `wwwroot`) on
one domain. There is no separate frontend host — no CORS, no version skew
between an app bundle and its API.

- `docker-compose.yml` — the production stack (app + Postgres), fronted by
  Traefik for TLS. See the root [README](../README.md) for the full run/backup
  walkthrough.
- `docker-compose.dev.yml` — just Postgres, for running the API from the IDE.
- `traefik/` — reverse-proxy dynamic config (TLS, middleware).

Demo sample data (#280/#284) is **command-only** — there is no `Seed__Demo` boot
toggle. Against an already base-seeded, non-Production database, run
`dotnet Cluckwork.Api.dll seed --profile demo` (see `AGENTS.md`).

## Container health check (#266)

The runtime image **and** the compose `app` service both declare a `HEALTHCHECK`
that runs the in-process `healthcheck` verb — `dotnet Cluckwork.Api.dll
healthcheck`. The hardened runtime image ships no `curl`/`wget`, so the probe
rides the same binary: it GETs `/health/ready` over loopback (port from
`ASPNETCORE_URLS`, default `8080`) and exits `0` on a 2xx, `1` on any other
status or an unreachable server.

`/health/ready` is 503 while the database is unreachable or a migration is
pending (#263), so `docker compose ps` shows the app **unhealthy** until it can
actually serve — and any orchestrator that gates on container health stops
routing to a stale or still-booting instance. Tune the cadence on the
`HEALTHCHECK` (`--interval` / `--timeout` / `--start-period` / `--retries`) or
the compose `healthcheck:` block.

Behind an orchestrator, point its HTTP readiness probe at `/health/ready` as
well (liveness at `/health/live`, which runs no checks); the host's readiness
path, wait-for-CI gate, and post-deploy smoke test live in the deployment repo.

## Database connection string (#261 / #262)

`ConnectionStrings__Default` accepts **two forms**:

- **Npgsql key-value** — `Host=db;Port=5432;Database=cluckwork;Username=…;Password=…`.
- **libpq URI** (#261) — `postgresql://user:pass@host:5432/dbname?sslmode=require`.
  Many managed-Postgres platforms emit this form; it is translated to key-value
  before use (host — IPv6 `[::1]` kept bracketed, port — default `5432` if omitted,
  URL-decoded password, database from the path, `sslmode`/cert query params mapped,
  and legacy `ssl=true` → `sslmode=Require`). A libpq param with no Npgsql equivalent
  (`channel_binding`, `target_session_attrs`, `gssencmode`, …) is **ignored with a
  warning** rather than failing the connection, so managed URLs (e.g. one carrying
  `channel_binding=require`) work. Both `postgres://` and `postgresql://` schemes are
  accepted.

**TLS is required in Production (#262).** When the app runs as Production
(`ASPNETCORE_ENVIRONMENT` unset → Production), it inspects `sslmode` once **at boot**
and **fails to start** unless the mode guarantees TLS. The floor is an **allow-list**
(fail closed) — only the modes below pass; anything else, *including an undefined mode*,
is rejected:

| `sslmode` | Production behaviour |
| --- | --- |
| `VerifyCA` / `VerifyFull` | Boots silently — certificate-validated TLS (preferred; `VerifyFull` + a host CA is the recommended posture). |
| `Require` | Boots, but **logs a warning** — encrypted but the server cert is not verified. |
| `Disable` / `Allow` / `Prefer` (and *unset* — Npgsql defaults to `Prefer`), or any undefined value | **Boot fails** with a clear message — no guarantee of encryption (MITM risk). |

The app never auto-injects or silently upgrades `sslmode` — set it explicitly on
the connection string. TLS is **not** enforced outside Production, so local dev
and the Testcontainers integration suite keep using plaintext connections.

**Opt-out — `Database:AllowInsecureConnection`** (default `false`). Setting it `true`
downgrades the boot failure above to a **loud warning** and boots anyway (a
`Require`-only connection still warns; `VerifyCA`/`VerifyFull` stay silent). It exists
for a co-located plaintext database over a private network. The bundled
`docker-compose.yml` sets `Database__AllowInsecureConnection=true` on **both** the
`app` and `migrate` services: that stack runs Production against a Postgres on the
private compose bridge (`Host=db;…`, never published), where plaintext is acceptable.
A real deployment uses TLS/managed Postgres (`sslmode=Require` min, `VerifyFull` + CA
preferred) and **never** sets this flag.

## Static asset caching

The API serves the SPA with cache headers tuned for a content-hashed build
(#141), so browsers and any fronting cache can hold assets aggressively without
ever serving a stale app:

| Response | `Cache-Control` | Why |
| --- | --- | --- |
| `/assets/*` (Vite output) | `public, max-age=31536000, immutable` | Content-hashed filename — the bytes never change under a given name, so cache forever and skip revalidation. |
| `index.html` (direct **and** the SPA fallback) | `no-cache` | Unhashed entry point that names the current bundle — always revalidate so a new deploy propagates immediately. `no-cache` still allows conditional (ETag) requests; it is **not** `no-store`. |
| other root files (favicon, manifest, …) | `no-cache` | Unversioned but cheap — revalidate rather than pin. |
| `/api/*` | *(none set by us)* | Dynamic responses carry no static cache header. |

## CDN in front (optional)

Because the origin already emits correct `Cache-Control`, you get edge caching
by proxying the **whole domain** through any CDN / reverse proxy — no need to
split the frontend onto a separate CDN origin. Whatever CDN you put in front
must satisfy these requirements (the specifics are provider-neutral on purpose):

1. **Validate the origin certificate.** If the CDN terminates TLS and
   re-originates to Traefik, use a *strict* mode that **validates the origin
   cert** — never a mode that accepts any cert, or an on-path attacker could
   present any certificate and intercept the (authenticated) API traffic. Give
   Traefik a publicly-trusted (or the CDN's origin-CA) certificate for the host,
   and ideally lock the origin to the CDN (authenticated origin pulls or an IP
   allowlist) so it can't be reached directly.
2. **Respect origin cache headers.** Configure the edge to honour the origin's
   `Cache-Control` — hold the `immutable` assets and revalidate `index.html` as
   above — rather than override with a blanket edge TTL.
3. **Bypass `/api/*`.** Add a rule so paths under `/api/` are never edge-cached;
   API responses are dynamic and per-tenant. (They carry no cache header, but an
   explicit bypass is belt-and-braces.)
4. **Bot / DDoS layer.** A CDN can add a bot-management / DDoS shield in front of
   the login endpoint. This is a **partial** mitigation only — API-side per-IP
   rate limiting on the auth endpoints (#143) is the real control and is already
   in place; keep both.

> Provider-specific setup (DNS records, dashboard settings, origin-lock config)
> is out of scope for this repo — it belongs in the deployment repo / ops
> location. This repo only ships the origin headers and this provider-neutral
> topology contract.
