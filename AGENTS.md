# AGENTS.md — Cluckwork

Poultry egg-farm management system. Backend: **.NET 10** (C#), layered DDD. Frontend: **React 19 + Vite** SPA in `web/`. Postgres via EF Core.

This file is the shared brief for any coding agent (Claude Code, Codex, etc.).

## Layout

```
src/
  Cluckwork.Domain          aggregates, value objects, domain events (no deps)
  Cluckwork.Application      feature handlers, repository interfaces, validators
  Cluckwork.Infrastructure   EF Core, Identity/JWT, repositories, seeding, jobs
  Cluckwork.Api             minimal-API endpoints, middleware, Program.cs
web/                        React/Vite SPA (see web/README.md)
deploy/                     docker-compose (.yml prod, .dev.yml dev DB), .env.example
specs/                      product + technical specs, wireframes
tests/                      Domain.Tests, Application.Tests, Api.IntegrationTests
```

Dependencies point inward: Api → Application/Infrastructure → Domain. Domain depends on nothing.

## Build / test / run

```bash
dotnet build Cluckwork.sln                 # warnings are errors — keep it clean
dotnet test  Cluckwork.sln                 # 42 tests; integration needs Docker
```

- **Integration tests** spin up a real Postgres via Testcontainers (`docker` required). No SQLite — EF SQL semantics differ.
- **Run full stack (prod-like):** `docker compose -f deploy/docker-compose.yml up --build` → SPA + API on http://localhost:8080 (single container; API serves the built SPA from `wwwroot`).
- **Run frontend dev:** `cd web && npm run dev` → http://localhost:5173 (proxies `/api` → :8080).
- **Debug API (no docker stack):** `docker compose -f deploy/docker-compose.dev.yml up -d` (Postgres on :5432), then run/debug `Cluckwork.Api` (Development env). Dev secrets live in **user-secrets**, not files.

## Conventions (follow these)

- **Result pattern:** domain/handlers return `Result` / `Result<T>` (see `Domain/Common`). Don't throw for expected failures; throw only for invariant violations (e.g. `Flock.Create` guards).
- **Every aggregate mutation must bump `Version`.** `Version` is an EF concurrency token (`IsConcurrencyToken`) — EF puts the *original* value in the UPDATE's WHERE but never auto-increments it. A mutation without `Version++` silently loses concurrent races (both writers match `WHERE Version = N`) instead of 409ing. This bug shipped three times (`RecordProduction` was correct; `Submit`/`Lock` and `SalesOrder.AddItem` weren't) — each now has a parallel-race integration test; add one for any new mutation.
- **Handler per feature**, invoked directly from endpoints — **no MediatR**. Register handlers/validators/repos in `Program.cs`.
- **Validation:** FluentValidation validators (`*Validator`), one per command; endpoints call `ValidateAsync` and return `ValidationProblem`.
- **Endpoints:** minimal APIs grouped under `/api/v1/...` via `Map<Feature>Endpoints`; writes require auth + an `Idempotency-Key` (middleware).
- **Multi-tenancy:** every tenant-owned entity has `AccountId`; enforced by an EF **global query filter** + a **`TenantStampInterceptor`** (stamps on insert). `TenantContext` is resolved per-request from the JWT `account_id` claim. At startup it's unresolved → use `IgnoreQueryFilters()` in seeders.
- **Auth:** asymmetric JWT + rotating refresh tokens. PEM keys come from config with escaped `\n`; normalize via `PemKey.Normalize` before `ImportFromPem`.
- **Migrations + seed:** EF migrations auto-apply on startup (`Database:MigrateOnStartup`, default true). `DatabaseSeeder` seeds the default account + admin user + `Admin` role — **credentials only from `Seed:*` config, never a fallback secret**; failures log + skip (don't crash).
- **Nullable enabled**, no unused usings (both are build-breaking).

## Secrets — never commit

- `deploy/.env` is gitignored (real values). `deploy/.env.example` holds placeholders only.
- Local API debug config uses `dotnet user-secrets` (keyed by `UserSecretsId` in `Cluckwork.Api.csproj`).
- No hardcoded passwords/keys in source — GitGuardian scans PRs. Generate test credentials at runtime.

## Pre-commit hook (opt-in)

`git config core.hooksPath .githooks` enables a ~2s pre-commit hook: unit tests
(domain + application) when `.cs`/`.csproj`/`.sln` files are staged, `npm run typecheck` when
`web/` files are staged. Integration tests are deliberately excluded (Docker,
slow) — CI is the authority. Skip once with `--no-verify`.

## CI security gates (#146)

CI fails a PR when a dependency carries a known **high+** advisory:

- **NuGet** — `dotnet list package --vulnerable` (parsed; the CLI always exits 0).
- **npm, production deps** — `npm audit --omit=dev`. Dev-only advisories (vite,
  vitest, eslint…) are **advisory only** — logged, never blocking, since they
  don't ship to users. Promote to blocking, or bump the dep, when one appears.
- **Dependency review** — PR-only; fails when the diff *introduces* a vulnerable dep.
- **CodeQL** (`.github/workflows/codeql.yml`) — SAST, **advisory** (reports to the
  Security tab; not a required check). Make it blocking via branch protection.

Both audit gates run through `.github/scripts/vuln-gate.mjs` (self-tested with
`node --test`), which shares one **escape hatch**: `.github/security-exceptions.json`.
Add a `{ id: GHSA-…, ecosystem, reason, expires }` entry to mute one advisory
until a **required** date — past it, the advisory blocks again and CI warns the
entry is stale. Reach for it only when there's no fixed version to move to; prefer
bumping the package or pinning a patched transitive version (npm `overrides` /
direct NuGet reference) first. The same file feeds dependency-review's allowlist,
so the gates never disagree.

## Git / PR workflow

- `origin` = GitHub (`github.com/mforce/cluckwork`); `gitea` = backup mirror. Use `gh` for PRs.
- **`main` is protected** — branch, push, open a PR; don't commit to `main`.
- Branch names: `feat/…`, `chore/…`, `spec/…`. PRs squash-merge.
- Only commit/push when the human asks.
- **Keep phase epics in sync**: when filing a slice issue, add it to the phase epic's checklist (epic #14 = Phase 1.1, #15 = Phase 1.5) with its issue number; when its PR merges, check it off. Milestone assignment alone is not enough — the epics are how work is navigated.
- **Keep documentation in sync** (owner directive, 2026-07-17): every PR that adds or changes user-visible behavior updates, in the same PR: (1) `specs/product/GLOSSARY.md` when a concept appears or changes meaning, and (2) the SPA Help page + in-app glossary (once #71 lands). Treat a missing doc update like a missing test — reviewers should flag it.

## Phase context

**Phase 1.0 (MVP) is shipped** — epic #13 closed. The egg loop runs end-to-end from the SPA: daily entry (by grade) → submit → egg lots → stock → customer → sales order → FIFO allocation → stock decremented. Single-farm login (multi-tenant infra present but dormant), customers without payments, draft orders cancellable/editable.

Domain terms (flock lifecycle, daily entry states, egg lots, grades, culls, FIFO allocation) are defined in `specs/product/GLOSSARY.md` — read it before renaming or modeling anything.

Current phase: **Phase 1.1** (epic #14, `specs/product/specs.md` §6) — RBAC UI, product catalog / egg-grade management, inventory movement ledger, feed/water/mortality, expenses, payments, dashboard, reports, audit UI, exports, i18n infrastructure (#45; English-only, translations land in 1.5). Known deferred item with an issue: farm-local timezone boundaries (#35). Work is tracked as GitHub issues (epics + slices).
