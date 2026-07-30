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
