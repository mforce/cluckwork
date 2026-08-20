# Contributing

Humans start here. Coding agents start at [`AGENTS.md`](AGENTS.md), which is the
canonical rule set — this file is the short human-facing path through it and
links there rather than restating rationale.

## Once per clone

```bash
git config core.hooksPath .githooks
```

Enables two fast hooks: **pre-commit** (unit tests for staged .NET changes,
`npm run typecheck` for staged `web/` changes) and **commit-msg** (rejects a
message release-please would silently drop from the changelog). Integration tests
are deliberately excluded — Docker, slow; CI is the authority. Skip once with
`--no-verify`.

## Local development

Prerequisites: **.NET 10 SDK**, **Docker**, **Node 26+**.

**Frontend:**

```bash
cd web
npm install
npm run dev        # http://localhost:5173, proxies /api to the API
```

**API from the IDE / CLI** — start just the database, then run the API (config
comes from user-secrets, never files):

```bash
docker compose -f deploy/docker-compose.dev.yml up -d   # Postgres on :5432
dotnet run --project src/Cluckwork.Api
```

`ASPNETCORE_ENVIRONMENT` matters: unset means Production, which fails the boot
against a plaintext local Postgres (the #261/#262 TLS floor).

A fresh database has no admin user — base data is migration-baked, credentials
never are. Provision one: [first admin provisioning](docs/runbooks/first-admin-provisioning.md).

**Aspire local stack:** see [Aspire local development](docs/runbooks/aspire-local-development.md)
to orchestrate Postgres, Redis, API and Vite locally. It is not a deployment path.

### Resetting the dev database

Always wipe through Compose:

```bash
docker compose -f deploy/docker-compose.dev.yml down -v
docker compose -f deploy/docker-compose.dev.yml up -d
```

Do **not** `docker volume rm` a volume name from memory. The live volume is
`cluckwork-dev_cluckwork-dev-pg18`, and an older `…-pg` volume may still be lying
around from before the Postgres 18 bump — removing that one succeeds, prints the
name back, and leaves the real database untouched, so a wipe can look like it
worked when nothing happened.

You need a wipe (not a migration) if boot fails with
`42P07: relation "Accounts" already exists`. That database still carries the
pre-squash migration history: the 34 migrations were replaced by a single
`InitialCreate`, which EF then sees as pending and tries to apply over tables
that already exist. Such a database cannot migrate forward — recreate it.

## Tests

```bash
dotnet test Cluckwork.sln    # integration tests spin up Postgres via Docker
cd web && npm test           # Vitest + Testing Library
```

Expectations, enforced at review like a missing feature:

- every change under `src/` ships with tests in the same PR;
- every change under `web/` ships with Vitest tests in the same PR;
- **every aggregate mutation bumps `Version`** and gets a parallel-race
  integration test — see the `Version` bullet in [`AGENTS.md`](AGENTS.md#conventions-follow-these);
- a new **guard** (a test whose job is to fail when someone later does the wrong
  thing) is mutation-checked before you claim it catches anything —
  [`docs/decisions/407-writing-a-guard.md`](docs/decisions/407-writing-a-guard.md).

## Changing the database schema

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

EF never re-runs an applied migration, so a column hand-folded into
`InitialCreate` **silently does not exist** on any booted database. It surfaces as
broken behaviour — a failing login, a missing field — not as a migration error,
which is what makes it worth a rule. `InitialCreate` is also not regenerable: it
carries four `lower("Name")` expression indexes EF cannot model plus the guarded
reference-data SQL, and re-adding it mints a new timestamp that desynchronises
`__EFMigrationsHistory` everywhere. `MigrationSecurityReviewTests` fails if it
stops being the first migration or loses its recorded id.

**Same PR:** regenerate the schema docs (`tools/schema-docs/generate.sh`) and
commit them — CI runs `generate.sh --check` and fails a stale PR (#417).

Full reasoning: [`docs/decisions/407-migration-freeze.md`](docs/decisions/407-migration-freeze.md).

## Branches and PRs

- **`main` is protected.** Branch, push, open a PR — never commit to `main`.
- Branches: `feat/…`, `fix/…`, `chore/…`, `docs/…`, `spec/…`. PRs squash-merge.
- The PR body is prefilled from
  [`pull_request_template.md`](.github/pull_request_template.md). Delete lines
  that do not apply rather than ticking them.
- **Keep the phase epic in sync**: a new slice issue goes on the epic's checklist
  (epic #14 = Phase 1.1, #15 = Phase 1.5); tick it when the PR merges. Milestone
  assignment alone is not enough — the epics are how work is navigated.

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/). **Subject:**
`type(scope): summary`, lowercase type, no space before the colon. The scope is
free-form — the area touched.

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
**still bump the patch digit**; they cost a number, not a deploy. What each type
does to the version number is in [`docs/releasing.md`](docs/releasing.md#what-decides-the-version).

Two traps, both producing a **green run with no changelog entry and no bump**:

**1. The PR title is the release note.** On a multi-commit PR the squashed subject
comes from it, and no local hook can see it. A non-conventional title silently
costs the bump.

**2. A body line starting with `word(` that has another `(` inside it** breaks the
parser — and an unparseable commit is dropped *entirely*, not just that line. Two
commits have already been lost this way. Backticks do not protect it:

```text
Assert.Single(AllMigrations())      ← breaks the parser
  Assert.Single(AllMigrations())    ← fine (indented)
- Assert.Single(AllMigrations())    ← fine (list item)
see Assert.Single(AllMigrations())  ← fine (word in front)
```

Only line *starts* matter, so `see foo(x) and bar(y())` mid-sentence was never a
problem. `.githooks/commit-msg` catches this in your own message and prints the
rewrite; it cannot see a PR title.

## Reviewing

Block on these like a missing test:

- a hardcoded credential, in application **or** test code (GitGuardian scans PRs);
- a hardcoded hosting-provider name in code, config, or a committed doc — see
  **Host-agnostic repo** in [`AGENTS.md`](AGENTS.md#host-agnostic-repo-deployment-boundary);
- a third-party Action pinned to a tag rather than a full commit SHA;
- an aggregate mutation with no `Version++`;
- a new Production boot guard that does not also update the sim harness (#370);
- a user-visible change with no matching doc update — `specs/product/GLOSSARY.md`
  when a concept appears or changes meaning, plus the SPA Help page and in-app
  glossary.

## Dependencies

- A package add or bump commits the regenerated `packages.lock.json` **in the same
  commit** — CI restores `--locked-mode` and otherwise fails with `NU1004`.
- A known-vulnerable production dependency fails CI. The only mute is a dated
  entry in `.github/security-exceptions.json` — see [`SECURITY.md`](SECURITY.md).
