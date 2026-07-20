# Cluckwork

Poultry farm management — starting with egg-producing layer operations, with architectural headroom for broilers, pullets, breeders, live bird sales, meat products, and hatchery modules.

Cluckwork helps a farm run its daily operation from one system: record production, track egg lots from the hen through to the sale with full traceability, block medication-restricted lots, manage sales and customers, and see the numbers that matter (hen-day rate, saleable %, stock on hand).

## Stack

- **Backend:** C# / .NET 10 (ASP.NET Core minimal APIs)
- **Database:** PostgreSQL (EF Core)
- **Frontend:** React 19 + Vite (TypeScript), served by the API in production

The API and the built SPA ship as a **single container**; in production the API serves the SPA and the JSON API from the same origin.

## Repo layout

```
cluckwork/
  src/               .NET solution
    Cluckwork.Domain          aggregates, value objects (no dependencies)
    Cluckwork.Application     feature handlers, repository interfaces, validators
    Cluckwork.Infrastructure  EF Core, Identity/JWT, repositories, seeding
    Cluckwork.Api             endpoints, middleware, Program.cs
  web/               React + Vite SPA (see web/README.md)
  tests/             domain, application, and API integration tests
  deploy/            Docker Compose (prod + dev DB), Traefik, .env.example
  specs/             product & technical specification, wireframes, CHANGELOG
```

## Getting started

Prerequisites: **.NET 10 SDK**, **Docker**, **Node 22+**.

### Run the whole app (Docker)

```bash
cp deploy/.env.example deploy/.env
# edit deploy/.env: set POSTGRES_PASSWORD, a JWT RSA keypair (Jwt__*KeyPem),
# and Seed__AdminEmail / Seed__AdminPassword (password must be >=12 chars,
# upper/lower/digit/symbol)

docker compose -f deploy/docker-compose.yml up --build
```

The app comes up on **http://localhost:8080**. On first boot it applies migrations and seeds the default account + admin user, so you can log in with the `Seed__Admin*` credentials. Optionally set `Seed__WorkerEmail` / `Seed__WorkerPassword` to also seed a login **without** the Admin role — useful for testing the worker experience (corrective actions hidden and refused).

### Frontend development

```bash
cd web
npm install
npm run dev        # http://localhost:5173, proxies /api to the API
```

### Run the API from the IDE / CLI

Start just the database, then run the API (config comes from user-secrets):

```bash
docker compose -f deploy/docker-compose.dev.yml up -d   # Postgres on :5432
dotnet run --project src/Cluckwork.Api
```

### Backup &amp; restore (self-hosted)

Two complementary layers (spec §17.5):

- **In-app**: an Admin can download any dataset as CSV — or the whole
  account as a zip — from the **Export** screen (`/api/v1/export/...`).
  Good for spreadsheets and keeping an offline copy; not a restore format.
- **Database dump**: the real backup for disaster recovery.

```bash
# Backup (compressed custom format; uses the credentials from deploy/.env).
# -T is required: a pseudo-TTY would corrupt the binary dump.
docker compose -f deploy/docker-compose.yml --env-file deploy/.env exec -T db \
  sh -c 'pg_dump -U "$POSTGRES_USER" -Fc "$POSTGRES_DB"' > cluckwork-$(date +%Y%m%d).dump

# Verify the dump is restorable before trusting it
pg_restore --list cluckwork-$(date +%Y%m%d).dump > /dev/null && echo OK

# Restore into a fresh database (stop the API first)
docker compose -f deploy/docker-compose.yml --env-file deploy/.env exec -T db \
  sh -c 'pg_restore -U "$POSTGRES_USER" --clean --if-exists -d "$POSTGRES_DB"' < cluckwork-YYYYMMDD.dump
```

Dumps contain everything — credentials hashes, tokens, all tenants — so
store them as secrets, not as shared files. Scheduled backups and health
checks are Phase 1.5.

### Tests

```bash
dotnet test Cluckwork.sln    # integration tests spin up Postgres via Docker
```

Optional: `git config core.hooksPath .githooks` enables a fast pre-commit hook
(unit tests for staged .NET changes, typecheck for staged `web/` changes).

## Architecture

Multi-tenant from the root so the system scales past a single farm:

```
Account / Tenant
  Users
  Farms (localized: timezone, locale, currency)
    Houses (cage, deep litter, free range, aviary…)
      Flocks (any species / production purpose — not hardcoded to layers)
```

Flock classification is extensible: `species` (chicken, duck, quail…), `production_purpose` (layer, broiler, pullet, breeder…), and `production_model` (egg, meat, raising, breeding, mixed). Tenant isolation is enforced in the data layer (EF query filters + an insert-time tenant stamp).

## Specs & roadmap

The canonical product and technical specification — data model, business rules, transaction boundaries, KPI formulas, and the **phase plan (Phase 1.0 MVP through Phase 5)** — lives in [`specs/product/specs.md`](specs/product/specs.md). New to the domain? Start with the
[**glossary of key concepts**](specs/product/GLOSSARY.md) — flocks, daily entries, egg lots,
culls, FIFO allocation, and friends. Work is tracked as GitHub issues (epics + slices).

## Contributing / agents

Conventions, build/run details, and the workflow coding agents should follow are in [`AGENTS.md`](AGENTS.md).
