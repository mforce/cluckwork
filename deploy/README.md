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

## CDN in front (Cloudflare free tier)

Because the origin already emits correct `Cache-Control`, you get edge caching
by proxying the **whole domain** through Cloudflare — no need to split the
frontend onto a separate CDN origin.

1. **Proxy the domain.** Point the DNS record for your Cluckwork host at the
   origin with the orange-cloud proxy **on**. Traefik still terminates TLS at
   the origin — use Cloudflare **"Full (strict)"**, not plain "Full": Full does
   **not** validate the origin certificate, so an on-path attacker could present
   any cert and intercept the (authenticated) API traffic. Give Traefik a
   publicly-trusted or Cloudflare Origin CA certificate for the host, and
   ideally lock the origin to Cloudflare (Authenticated Origin Pulls or an
   IP allowlist) so it can't be reached directly.
2. **Respect origin headers.** Leave Cloudflare's caching on "Standard" /
   "respect existing headers" so it honours the `immutable` assets and
   revalidates `index.html` as above — don't override with a blanket Edge TTL.
3. **Bypass `/api/*`.** Add a cache rule: for `URI Path starts with /api/`,
   **Bypass cache**. API responses are dynamic and per-tenant; they must never
   be edge-cached. (They carry no cache header, but an explicit bypass rule is
   the safe belt-and-braces.)
4. **Bot / DDoS layer.** The proxy adds a bot-management and DDoS shield in
   front of the login endpoint. This is a **partial** mitigation only —
   API-side per-IP rate limiting on the auth endpoints (#143) is the real
   control and is already in place; keep both.

> Provisioning the Cloudflare account/zone is out of scope here — this repo only
> ships the origin headers and this topology guide.
