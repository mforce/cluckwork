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
                     (see deploy/README.md: topology, static caching, CDN-in-front)
  specs/             product & technical specification, wireframes, CHANGELOG
```

## Getting started

Prerequisites: **.NET 10 SDK**, **Docker**, **Node 26+**.

### Run the whole app (Docker)

```bash
cp deploy/.env.example deploy/.env
# edit deploy/.env: set POSTGRES_PASSWORD and a JWT RSA keypair (Jwt__*KeyPem)

docker compose -f deploy/docker-compose.yml up --build
```

The app comes up on **http://localhost:8080**. Base data (the default account, roles, default egg grades) ships as part of the EF migrations, so it's already there — but there is no admin user yet (no credential is ever baked into the repo). Provision the first one:

```bash
docker compose -f deploy/docker-compose.yml run --rm app \
  bootstrap-admin --email admin@example.com
```

Pass the **verb only** — the image's `ENTRYPOINT` is already
`dotnet Cluckwork.Api.dll`, and `docker compose run` *appends* to it. Repeating
`dotnet Cluckwork.Api.dll` here would make `args[0]` be `dotnet`, which matches
no CLI verb, so the container would boot the web server instead of provisioning
anything.

This writes the generated one-time password to **stdout only** — never the application logger or the OTLP pipeline. A host's stdout collector (docker logs, journald, a platform log pipeline) may still capture it, so treat that output as sensitive while the password is valid. Sign in with it — the app immediately shows a **Set your password** screen and refuses everything else until you pick your own. Re-running the command against an already-provisioned account is a safe no-op.

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

## Releases & container images

Releasing has two stages: **CI publishes an image for every merge; you decide when
those become a version.**

### 1. Merging a PR into `main`

CI builds the image, scans it for vulnerabilities, boots it against a throwaway
database — and if all of that passes, publishes it under the commit it came from:

```
ghcr.io/mforce/cluckwork:sha-<commit>
```

That image is deployable immediately. It just doesn't have a version yet.

Meanwhile a bot keeps a **"Release vX.Y.Z" pull request** up to date, accumulating a
`CHANGELOG.md` from the commits since the last release and working out the next
version number.

### 2. Merging the Release PR

That's the release. It tags the commit, publishes a GitHub release with the
changelog, and **promotes** the already-published image to a version:

```
ghcr.io/mforce/cluckwork:v0.4.0          # same image, now with a version
ghcr.io/mforce/cluckwork@sha256:…        # the digest — what you deploy
```

Promotion adds a name to an image that already exists in the registry. Nothing is
rebuilt, so the bytes carrying `v0.4.0` are provably the bytes that passed CI.

### What decides the version

Your **PR title** — it becomes the commit subject when the PR is squashed:

| PR title starts with | Effect on `v0.4.0` |
|---|---|
| `fix:` / `perf:` | `v0.4.1` |
| `feat:` | `v0.5.0` |
| `feat!:` or a `BREAKING CHANGE` footer | `v1.0.0` |
| `chore:` / `docs:` / `test:` / `ci:` | **no release at all** |

That last row is deliberate: a docs-only merge doesn't manufacture a version, so the
numbers keep meaning something. The image is still published as `:sha-<commit>` and
is still deployable.

### Deploying

**Deploy by digest, never by tag.** Tags can be moved; a digest cannot. Every
release's notes end with the exact reference:

```bash
gh release view v0.4.0        # the digest is at the bottom of the notes
```

That digest is the only identifier that provably refers to the bytes CI scanned and
booted. Deployment configuration itself lives in the separate deploy repo, not here.

### Notes

- **Pull requests publish nothing.** The publish job only runs on `main`.
- **No version files to edit.** `version.txt` and `.release-please-manifest.json` are
  machine-maintained — editing them by hand desynchronises the bot from reality.
- **The Release PR has no CI checks**, because GitHub doesn't run workflows for
  bot-created PRs. It only touches the changelog and version files.
- **A release that can't find its image fails** rather than tagging a version with
  nothing behind it.

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
