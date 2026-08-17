# Deployment

Cluckwork ships as a **single origin**: one container runs the .NET API, which
serves both the JSON API (`/api/*`) and the built React SPA (from `wwwroot`) on
one domain. There is no separate frontend host — no CORS, no version skew
between an app bundle and its API.

## Credential-epoch rollout (#364)

Deploy the credential-epoch release everywhere before enabling the later
user-administration mutations that increment the epoch. Do not expose those
mutations until every pre-epoch process has drained: an older process cannot
validate the epoch claim and could otherwise accept an access token that a new
process has invalidated. Because this release is folded into the pre-production
`InitialCreate` migration, there are no deployed legacy rows to cut over.

Rollback is deliberately ordered too. Drain every process running this
credential-epoch release, revoke **all active refresh tokens**, and only then
start a pre-epoch image. A pre-epoch binary ignores `IssuedEpoch`; starting it
before that revocation can resurrect an epoch-0 child written during a mixed
fleet window. Treat rollback as one forced re-login, not as a plain image
downgrade.

- `docker-compose.yml` — the production stack (app + Postgres + Redis), fronted
  by Traefik for TLS. Redis backs the #543 shared-state ports; a real
  multi-instance deploy points `SharedState__Redis__ConnectionString` at a
  managed/shared Redis instead of the co-located sidecar. See the root
  [README](../README.md) to run it, and
  [backup & restore](../docs/runbooks/backup-and-restore.md) for the dump/restore
  procedure.
- `docker-compose.dev.yml` — just Postgres, for running the API from the IDE.
- `traefik/` — reverse-proxy dynamic config (TLS, middleware).

Demo sample data (#280/#284) is **command-only** — there is no `Seed__Demo` boot
toggle. Against an already base-seeded, non-Production database, run
`dotnet Cluckwork.Api.dll seed --profile demo` (see `AGENTS.md`).

**Run `bootstrap-admin` first (#500).** The demo profile signs every record it
seeds with the default account's Owner, so it refuses to run when there is none —
exit `1`, with a message naming the command to run. On a database whose Owner was
provisioned by an earlier `bootstrap-admin`, that generated password was printed
once and is never reprinted; recover it with the `recover-admin` break-glass
procedure rather than re-running `bootstrap-admin`, which silently no-ops once an
Owner exists.

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
  and legacy `ssl=true` → `sslmode=Require`). Params Npgsql supports only under a
  different spelling (`channel_binding`, `target_session_attrs`, `gssencmode`, …) are
  **mapped**, so managed URLs (e.g. one carrying `channel_binding=require`) work; a param
  with genuinely no Npgsql equivalent (`sslcompression`, …) is **ignored with a warning**
  rather than failing the connection. Both `postgres://` and `postgresql://` schemes are
  accepted. Not yet mapped, so currently ignored-with-warning: the `keepalives*` family
  (Npgsql spells these `Tcp Keepalive` / `Tcp Keepalive Time` / `Tcp Keepalive Interval`)
  and `client_encoding` — set them in key-value form if you need them.

**GSS/Kerberos negotiation is off by default (#332).** Npgsql's `GssEncryptionMode`
defaults to `Prefer`, so every connector probes the GSSAPI stack before authenticating.
The runtime image deliberately carries no `libgssapi-krb5-2` (#267 keeps it minimal and
Trivy-scanned), so that probe made .NET's native security shim print two **unstructured**
lines to stderr — emitted before Serilog exists, so they cannot be filtered or shipped as
structured events — on every connecting process, reading like a failure during deploys:

```
Cannot load library libgssapi_krb5.so.2
Error: libgssapi_krb5.so.2: cannot open shared object file: No such file or directory
```

Cluckwork authenticates with a password, so the app now appends
`GSS Encryption Mode=Disable` **unless you set it yourself** (via `gssencmode=…` in a URI
or `GSS Encryption Mode=…` in key-value form) — a Kerberos-fronted deployment opts back in
and keeps its value. This is orthogonal to `sslmode`: the TLS floor below is unaffected.

Confirmed by loader trace (`LD_DEBUG=libs`, scram-sha-256 server): `Prefer` loads
`libgssapi_krb5.so.2`, `libkrb5.so.3` and `libkrb5support.so.0` even though the server
never offers GSS; `Disable` produces zero gssapi/krb5 loader activity and connects
identically. On an image that *has* the libraries the load is silent — the two error lines
above are what the same attempt looks like where the libraries are absent.

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

## Design-time migrations (`dotnet ef`) (#318)

`AppDbContextDesignTimeFactory` is what `dotnet ef migrations add` / `dotnet ef
database update` use to build a `DbContext` on a developer's machine — separate
from the `migrate` CLI verb above, which runs against the built host's own
configuration. It reads the target from an environment variable, never a
default:

```sh
export CLUCKWORK_MIGRATIONS_CONNECTION="Host=<host>;Database=<db>;Username=<user>;Password=<password>"
dotnet ef migrations add <Name> --project src/Cluckwork.Infrastructure --startup-project src/Cluckwork.Api
```

`CLUCKWORK_MIGRATIONS_CONNECTION` also accepts the libpq URI form, e.g.
`postgresql://<user>:<password>@<host>:5432/<db>?sslmode=verify-full`.

- **Unset or blank fails immediately** with a message naming the variable —
  there is no fallback connection, so a typo can never silently point tooling
  at an unintended database.
- **The same allow-list TLS floor as a Production boot** (#261/#262) applies to
  every target: `VerifyCA`/`VerifyFull` pass silently, `Require` passes with a
  warning, anything weaker fails.
- **Loopback plaintext escape hatch** — set
  `CLUCKWORK_MIGRATIONS_ALLOW_INSECURE_LOOPBACK=true` to permit a plaintext
  connection, but *only* when `CLUCKWORK_MIGRATIONS_CONNECTION` targets a
  loopback host (`localhost` / `127.0.0.1` / `::1`), e.g. the
  `docker-compose.dev.yml` Postgres on `localhost:5432`. Setting it against any
  other host fails the tooling instead of silently widening scope.

## Static asset caching

The API serves the SPA with cache headers tuned for a content-hashed build
(#141), so browsers and any fronting cache can hold assets aggressively without
ever serving a stale app:

| Response | `Cache-Control` | Why |
| --- | --- | --- |
| `/assets/*` (Vite output) | `public, max-age=31536000, immutable` | Content-hashed filename — the bytes never change under a given name, so cache forever and skip revalidation. |
| `index.html` (direct **and** the SPA fallback) | `no-cache` | Unhashed entry point that names the current bundle — always revalidate so a new deploy propagates immediately. `no-cache` still allows conditional (ETag) requests; it is **not** `no-store`. |
| other root files (favicon, manifest, …) | `no-cache` | Unversioned but cheap — revalidate rather than pin. |
| `/api/*`, auth, validation/error responses, exports | `private, no-store` | #312 — a safe-by-default origin policy: authenticated JSON, CSV/zip exports and auth responses (even pre-authentication) may carry tenant data, so nothing about them may be stored by a browser cache or an intermediary. |
| `/health/*` | *(unaffected)* | The health-check middleware sets its own no-caching headers; #312's default explicitly excludes `/health` rather than override that framework-owned contract. |

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
   API responses are dynamic and per-tenant. The origin already sends
   `Cache-Control: private, no-store` on them (#312), but an explicit edge
   bypass stays required as defense in depth — a misconfigured edge rule or a
   future CDN default that doesn't fully honour `no-store` must not be the only
   thing standing between a forgetful config and cached tenant data.
4. **Bot / DDoS layer.** A CDN can add a bot-management / DDoS shield in front of
   the login endpoint. This is a **partial** mitigation only — API-side per-IP
   rate limiting on the auth endpoints (#143) is the real control and is already
   in place; keep both.

> Provider-specific setup (DNS records, dashboard settings, origin-lock config)
> is out of scope for this repo — it belongs in the deployment repo / ops
> location. This repo only ships the origin headers and this provider-neutral
> topology contract.
