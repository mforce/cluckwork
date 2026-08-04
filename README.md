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

A fresh database has no admin user here either — base data is migration-baked,
credentials never are. Provision one against the dev database the same way:

```bash
ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project src/Cluckwork.Api -- bootstrap-admin --email admin@cluckwork.local
```

Note the `--` separator: it stops `dotnet run` from consuming the arguments and
passes them to the app. (The Docker form above omits it because the image's
`ENTRYPOINT` already supplies the binary.) Everything else matches the Docker
path — the generated password goes to stdout only, and the app forces you to
replace it at first sign-in. Until you run this, the login page says so.

`ASPNETCORE_ENVIRONMENT` matters: unset means Production, which fails the boot
against a plaintext local Postgres (the #261/#262 TLS floor).

**Resetting the dev database.** Always wipe through Compose:

```bash
docker compose -f deploy/docker-compose.dev.yml down -v
docker compose -f deploy/docker-compose.dev.yml up -d
```

Do **not** `docker volume rm` a volume name from memory. The live volume is
`cluckwork-dev_cluckwork-dev-pg18`, and an older `…-pg` volume may still be
lying around from before the Postgres 18 bump — removing that one succeeds,
prints the name back, and leaves the real database untouched, so a wipe can
look like it worked when nothing happened.

You need a wipe (not a migration) if boot fails with
`42P07: relation "Accounts" already exists`. That means the database still
carries the pre-squash migration history: the 34 migrations were replaced by a
single `InitialCreate`, which EF then sees as pending and tries to apply over
tables that already exist. Such a database cannot migrate forward — recreate it.

### Changing the database schema

**Add a migration. Never edit `InitialCreate`.**

```bash
# Against the local dev Postgres. The connection is fail-closed (#318): there is
# no default, and every target is held to the same TLS floor as a Production
# boot — the loopback opt-out below is what permits plaintext, and only for
# localhost/127.0.0.1/::1.
CLUCKWORK_MIGRATIONS_CONNECTION='Host=localhost;Port=5432;Database=cluckwork;Username=…;Password=…' \
CLUCKWORK_MIGRATIONS_ALLOW_INSECURE_LOOPBACK=true \
  dotnet ef migrations add <Name> \
  -p src/Cluckwork.Infrastructure -s src/Cluckwork.Api
```

Before PR #407 the repo held exactly one migration and schema changes were
hand-folded into it. That was only safe while the app had never been deployed —
no database had applied the file yet, so rewriting it was free. **#407 was the
cutover.** Now that a database exists which has already applied `InitialCreate`,
EF will never re-run it, so a hand-folded column is a column that silently does
not exist. It surfaces as broken behaviour (a failing login, a missing field),
not as a migration error — which is precisely what makes it worth a rule.

`InitialCreate` is also **not regenerable**: it carries four `lower("Name")`
expression indexes EF cannot model and 21 rows of guarded reference-data SQL,
and re-adding it would mint a new timestamp that desynchronises
`__EFMigrationsHistory` everywhere. `MigrationSecurityReviewTests` fails if it
stops being the first migration or loses its recorded id.

A dev database created **before #407 merged** predates those folded-in columns —
wipe and recreate it as above.

The full reasoning — why the freeze, what the fingerprint guard covers, and what
made the wrong versions of it pass — is in
[`docs/decisions/407-migration-freeze.md`](docs/decisions/407-migration-freeze.md).

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

Optional: `git config core.hooksPath .githooks` enables two fast hooks —
**pre-commit** (unit tests for staged .NET changes, typecheck for staged `web/`
changes) and **commit-msg** (rejects a message release-please would silently drop
from the changelog; see [Writing a commit message](#writing-a-commit-message)).

## Releases & container images

Releasing has two stages: **CI publishes an image for every merge; you decide when
those become a version.**

> This section is the **how-to**. The **invariants** — what not to break, and why
> each step is shaped the way it is — live in the release section of
> [`AGENTS.md`](AGENTS.md#releases-and-image-publishing-351); the full internal
> mechanism (promotion, the release-please split, the App token, the commit-body
> parser) is in [`docs/decisions/351-releases.md`](docs/decisions/351-releases.md).

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

That's the release. It drafts a GitHub release with the changelog, **promotes** the
already-published image to a version, and — only once that succeeds — publishes the
release and creates the tag:

```
ghcr.io/mforce/cluckwork:v0.4.0          # same image, now with a version
ghcr.io/mforce/cluckwork@sha256:…        # the digest — what you deploy
```

Promotion adds a name to an image that already exists in the registry. Nothing is
rebuilt, so the bytes carrying `v0.4.0` are provably the bytes that passed CI.

### What decides the version

Your **PR title** — or, on a **one-commit** branch, that commit's own subject,
which GitHub uses instead. Either way it becomes the squashed commit subject:

While the version is **below 1.0.0**, everything is deliberately damped one level —
the project is pre-1.0 and shouldn't burn major digits on Phase 1.x churn:

| PR title starts with | Effect on `v0.3.2` |
|---|---|
| `feat!:` or a `BREAKING CHANGE` footer | `v0.4.0` |
| `feat:` | `v0.3.3` |
| anything else (`fix:`, `perf:`, `chore:`, `docs:`, `ci:` …) | `v0.3.3` |

So below 1.0.0 only a **breaking change** moves the minor digit; everything else,
features included, is a patch. `chore`/`ci`/`test`/`style` are hidden from the
changelog *text*, but they still move the number.

**Once the version reaches 1.0.0 this changes**, and it changes silently — the two
`*-pre-major` settings in `release-please-config.json` stop applying, so `feat:`
starts bumping the minor digit and a breaking change bumps the major. Getting to
1.0.0 is therefore a deliberate act: bump it with a `Release-As: 1.0.0` footer when
you mean it, not by accident.

That is not as noisy as it sounds, because the bump lands in the **pending release
PR**, not in a release. Several chore merges accumulate into one proposed patch, and
nothing is released until you merge that PR.

### Writing a commit message

**Subject:** `type(scope): summary`, lowercase type, no space before the colon.
The scope is free-form — the area touched. Real examples from this repo:

| type | changelog section | example |
|---|---|---|
| `feat` | Features | `feat(eggs): make cracked and dirty eggs sellable stock via condition grades` |
| `fix` | Bug fixes | `fix(sales): reject fractional order-line quantities` |
| `perf` | Performance | `perf(reports): stream the CSV export instead of buffering it` |
| `refactor` | Refactoring | `refactor(api): extract service registration from Program` |
| `docs` | Documentation | `docs(agents): record the guard-writing rules #407 paid five rounds for` |
| `ci` | *hidden* | `ci(e2e): workflow_dispatch job for the Playwright smoke suite` |
| `test` | *hidden* | `test(e2e): Playwright smoke suite for the SPA over the #243 sim fixture` |
| `build` | *hidden* | `build(deps): bump Npgsql to 10.0.2` |
| `chore` | *hidden* | `chore(web): drop the unused date-fns dependency` |
| `style` | *hidden* | `style(web): apply Prettier to the untouched settings screens` |

Add `!` for a breaking change — `feat(api)!: drop the v0 endpoints` — or a
`BREAKING CHANGE:` footer. *Hidden* types stay out of the changelog text but
**still bump the patch digit** (see above); they cost a number, not a deploy.

**Body:** release-please parses the whole message, body included, and the squash
body is every branch commit message concatenated. One unparseable line drops the
**entire commit** from the changelog — no entry, no version bump, green run. Two
commits have already been lost this way.

**Never start a line with `something(` that has another `(` inside it.** Ordinary
code prose, and backticks do not protect it:

```text
fix(x): summary

The fence is the test:
Assert.Single(AllMigrations()) fails when a second appears.
^^^^^^^^^^^^^^             ^
│                          └─ a second "(" before the first one closes
└─ line STARTS with  word(
```

release-please reads a line-initial `word(` as a `type(scope):` header. A scope
cannot contain `(`, so the parse fails — and a failed parse means **this whole
commit is skipped**, not just this line.

Move the line off column 1 and it is fine. Nothing else changes:

```text
fix(x): summary

The fence is the test:

  Assert.Single(AllMigrations())
^^
└─ two spaces. The line no longer starts with the shape.

fails when a second appears.
```

A `- ` list item or any word in front works equally well. Only line *starts*
matter, so `see foo(x) and bar(y())` mid-sentence was never a problem.

`.githooks/commit-msg` catches this and prints the rewrites applied to your own
line. It cannot see a **PR title**, so a non-conventional title is still yours and
the reviewer's to catch. [`AGENTS.md`](AGENTS.md) is canonical.

### Deploying

**Deploy by digest, never by tag.** Tags can be moved; a digest cannot.

Two steps, answering two different questions — *which* image, and whether it is
really ours:

```bash
# 0. gh needs registry credentials for an oci:// subject. The token needs
#    read access to the package (`read:packages` on a PAT); GHCR authenticates
#    the token and ignores the username, so any username value works.
echo "$GITHUB_TOKEN" | docker login ghcr.io -u x-access-token --password-stdin

# 1. Obtain the digest (machine-readable; no prose to parse)
gh release download vX.Y.Z -p image.json -R mforce/cluckwork
REF=$(jq -r .reference image.json)

# 2. Verify those bytes came from this repo's CI
gh attestation verify "oci://$REF" \
  --repo mforce/cluckwork \
  --signer-workflow mforce/cluckwork/.github/workflows/ci.yml \
  --source-ref refs/heads/main \
  --bundle-from-oci
```

All three flags matter and none is the default — each narrows *whose* claim is
accepted (the registry copy, one workflow, one branch). Copy the command as-is.

Step 2 is the one that matters, and step 1 cannot substitute for it. Knowing a
digest tells you *what* you are deploying but nothing about *where it came from*
— if someone pushed those bytes by hand, the digest is still a perfectly valid,
perfectly immutable digest. The attestation is a signed claim by this repo's CI
workflow, so anything pushed by hand has no such claim and fails the check.

That covers a credential that can push to the registry, and stops a branch
writer getting *their own* bytes deployed. It proves **origin, not currency**,
though — "did CI on `main` build these bytes", not "are these the bytes this
release promoted" — so also confirm the tag still agrees with what you verified:

```bash
# 3. Confirm the release's tag still resolves to the digest you just verified.
#    Compare against $REF, NOT against `jq -r .digest`: `image`, `digest` and
#    `reference` are independent fields of one attacker-writable file, so a
#    rewritten asset can point `.reference` at an old digest while leaving
#    `.digest` matching the tag — and a check reading `.digest` would pass while
#    you deploy the old one. $REF is what step 2 verified and what you deploy.
TAGGED=$(docker buildx imagetools inspect ghcr.io/mforce/cluckwork:vX.Y.Z \
  --format '{{json .Manifest.Digest}}' | tr -d '"')
[ "$TAGGED" = "${REF##*@}" ] || exit 1
```

Step 3 catches an asset rewritten on its own. It does **not** catch someone who
can also push to the registry and move the tag to match, and it says nothing
about a change merged to `main`. **Read the deploy bullet in
[`AGENTS.md`](AGENTS.md) before relying on any of this** — it is the canonical
statement of what each step does and does not prove, and of why each flag is
required.

The digest also appears at the bottom of the release notes, for humans.
Deployment configuration itself lives in the separate deploy repo, not here.

**Releases cut before this landed support neither step.** Their images were
published before attestation existed, so step 1 404s and step 2 finds nothing to
verify — the digest in the release notes is all there is, and it carries no
proof of origin. This applies to `v0.0.1` only; every release from the next one
on has both.

### Notes

- **Pull requests publish nothing.** The publish job only runs on `main`.
- **No version files to edit.** `version.txt` and `.release-please-manifest.json` are
  machine-maintained — editing them by hand desynchronises the bot from reality.
- **The Release PR is built and tested like any other PR.** It's opened with a
  GitHub App token rather than the default Actions token, which matters twice.
  The default token can't open a PR at all unless the repo turns on a setting
  that lifts that restriction for *every* workflow — so instead, opening PRs
  stays behind a credential a job has to ask for by name. And PRs opened by the
  default token get no CI run, so you'd only see a problem after merging rather
  than before. (The released image is verified either way: promotion refuses to
  run unless CI recorded a digest for that commit.)
- **A release that can't find its image never goes public.** It stays a draft, and
  GitHub doesn't create the tag for a draft — so you get no version pointing at a
  missing image. To finish it: Actions → **Release** → *Run workflow*, with the tag.
- **If a commit was never built at all**, run Actions → **CI** → *Run workflow* with
  that commit's sha first, then the Release step above. This happens when a commit
  message contains `[skip ci]` (GitHub matches it anywhere in the message, so it can
  arrive via a changelog entry) — no run is created, so there is nothing to re-run.
  The dispatch only accepts commits already on `main`, and **must itself be run
  from `main`** (the default branch in the *Run workflow* dropdown). The sha you
  type names the commit to build; the branch you dispatch from decides which
  workflow definition runs, and an image built from a branch dispatch carries
  provenance naming that branch — which the release workflow, and any deploy
  that verifies, both reject.

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
