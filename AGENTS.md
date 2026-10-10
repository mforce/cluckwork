# AGENTS.md: Cluckwork

Poultry egg-farm management system. Backend: .NET 10 (C#), layered DDD, Postgres via EF Core. Frontend: React 19 + Vite SPA in `web/`.

This file is the index and the rules that apply everywhere. Read [`CONTRIBUTING.md`](CONTRIBUTING.md) for setup, tests, schema changes, commit messages and dependencies. Read [`docs/architecture.md`](docs/architecture.md) before you move middleware or add an aggregate state, and [`specs/product/GLOSSARY.md`](specs/product/GLOSSARY.md) before you name or model a domain concept.

A `→` link points to the rule's decision record in [`docs/decisions/`](docs/decisions/). Read it before you change or work around the rule.

## Scoped rule files

Before you edit a path below, read its file, whether or not your harness loads it.

- `src/**`, `tests/**`, `tools/simulation/**`, `Dockerfile`, `deploy/**`, `.editorconfig`, `Directory.Build.props` → [`src/AGENTS.md`](src/AGENTS.md): module boundaries, data and tenancy, auth, boot guards, config callers, the single serving instance.
- `web/**` → [`web/AGENTS.md`](web/AGENTS.md): SPA text, help prose, styles, lint. Role-gated navigation also needs `src/AGENTS.md`.
- `src/Cluckwork.Infrastructure/Persistence/**`, `docs/schema/**`, `tools/schema-docs/**` → [`src/Cluckwork.Infrastructure/Persistence/AGENTS.md`](src/Cluckwork.Infrastructure/Persistence/AGENTS.md): migrations, reference data, schema docs.
- `.github/**`, `docs/releasing.md`, `deploy/**` → [`.github/AGENTS.md`](.github/AGENTS.md): workflows, Action pins, image publishing, releases.

## Layout

```
src/Cluckwork.Domain          aggregates, value objects (no deps)
src/Cluckwork.Application     handlers, repository interfaces, validators
src/Cluckwork.Infrastructure  EF Core, Identity, repositories, seeding, jobs
src/Cluckwork.Api             minimal-API endpoints, middleware, CLI verbs
src/Cluckwork.AppHost         Aspire local orchestration, dev only
src/Cluckwork.Analyzers       module-edge analyzer, never shipped
tests/                        Domain, Application, Api.IntegrationTests, AppHost
tools/simulation/             sim stack, k6, Playwright E2E (tools/simulation/ui)
```

## Build / test / run

- `dotnet build Cluckwork.slnx`. Warnings are errors.
- Run only the tests your change touches, with `--filter`. CI runs the full suite.
- Integration tests use real Postgres through Testcontainers, never SQLite. They need Docker.
- A new test project needs a leg in `ci.yml`'s `tests` matrix and a row in `SolutionTestProjectSplitTests`. A missing leg never runs and nothing fails. → [`775-ci-test-matrix.md`](docs/decisions/775-ci-test-matrix.md)

## Before you start

- Read the issue's comments as well as its body. Requirements and amendments land in comments.
- Do not copy issue state (open, closed, remaining) into these files or other docs. Use `gh issue view <n> --json state`.

## Writing a guard (a test that asserts an invariant)

A wrong guard is worse than none, because it looks safe. → [`407-writing-a-guard.md`](docs/decisions/407-writing-a-guard.md)

- Assert the invariant, not a message. A test that pins error text passes when the behavior breaks.
- Mutation first, claim second. Break the code the guard protects and watch the guard go red before you write "this catches X". Report the mutations in the PR.
- Walk everything and exclude deliberately. Never list the cases you thought of. Two misses of the same shape mean the method is wrong.
- Classify by what the code declares about itself (attributes, interfaces, symbols, token hashes), never by namespace, path or `file:line`. A namespace shortcut fails open when code moves. → [`859-typed-rule-registries.md`](docs/decisions/859-typed-rule-registries.md#allow-list-rows-carry-a-token-hash-1072)
- A walk that finds nothing must fail. Give it a floor.
- For a pinned or golden value, prove it is portable across machines.
- Prefer the boring guard.
- Run an adversarial pass before the first push: mutations, or a second agent told to refute the guard.
- When you add a registry entry (`CliDispatcher.Commands`, `ProcessRoles.OneShotVerbs`, `AuditActions`, an enum mirrored in `web/src/i18n/enums.ts`), grep `tests/` for the registry's name to find every guard that reads it. Do not rely on memory.
- Some guards check call-site syntax, for example `AuditVocabularyCoverageTests` on `IAuditWriter.WriteAsync`. Read the guard before you write the call.
- Some guards walk every tracked file (image pins, tenancy wording). They apply to a doc as soon as you commit it. Run them before you commit docs.

## UI changes: screenshots and verification

- A PR that changes what a user sees attaches screenshots: before and after at the same viewport and scenario, after-only for new UI. Capture at 1:1 from a stack rebuilt at the PR head; a long-running sim stack serves the build it started with. Attach with `gh pr comment --attach` or `gh pr create --attach`, never by committing images to a branch.
- Use the [`verify`](tools/verify/SKILL.md) skill to launch the sim stack, drive it and capture frames. Never reset a sim stack another agent is using.
- Playwright E2E lives in `tools/simulation/ui/` (its README has the suite's rules). Reason about `inert` and the accessibility tree through CDP (`src/ax.ts`), because Playwright's own APIs do not model `inert`. → [`277-spa-e2e.md`](docs/decisions/277-spa-e2e.md)
- Every user-visible change updates, in the same PR, `specs/product/GLOSSARY.md` when a concept appears or changes meaning, and the SPA Help page and in-app glossary.

## Secrets

Never commit a secret. Real values go in gitignored `deploy/.env` or `dotnet user-secrets`. Generate test credentials at runtime; never write a credential literal, even in a test.

## Host-agnostic repo (deployment boundary)

Code, config and docs never name or branch on a hosting provider. Provider manifests, IaC and concrete environment values (proxy CIDRs, CA bundles, connection URLs) belong in the separate deployment repo.

## Git / PR workflow

- Never commit to `main`. Branch as `feat/…`, `fix/…`, `chore/…`, `docs/…` or `spec/…`. A git hook refuses merges, force pushes, refspec pushes and pushes to `main`; its message tells you the allowed form.
- The PR title is the release note: it becomes the squash commit subject that release-please parses. Use a conventional title (`feat(scope): …`). A bad title drops the changelog entry with a green run.
- Close an issue with `Closes #N` on its own line in the PR body, never in the title. Check what will close with `gh api graphql` on `closingIssuesReferences`.
- Never hand-edit `.release-please-manifest.json` or `version.txt`.
- A package add or bump commits the regenerated `packages.lock.json` files in the same commit. NuGet versions live only in `Directory.Packages.props`.
- If your PR ships work that another open issue claims, add an amendment comment to that issue in the same PR: what shipped, where, and what remains. Leave its body as written.

## graphify

`graphify-out/` holds a knowledge graph of the code. For codebase questions, `graphify query "<question>"`, `graphify path "<A>" "<B>"` and `graphify explain "<concept>"` return a focused subgraph. Do not commit `graphify update .` output with unrelated changes.
