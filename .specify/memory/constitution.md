<!--
Sync Impact Report
- Version change: unratified scaffold -> 1.0.0
- Modified principles:
  - Template Principle 1 -> I. Domain Integrity and Layered Boundaries
  - Template Principle 2 -> II. Tenant Isolation and Credential Safety
  - Template Principle 3 -> III. Fail-Closed Data and Operations
  - Template Principle 4 -> IV. Evidence-Backed Change
  - Template Principle 5 -> V. Reproducible, Host-Portable Delivery
- Added sections:
  - Additional Constraints
  - Development Workflow and Quality Gates
- Removed sections: None
- Follow-up TODOs: None
-->
# Cluckwork Constitution

## Core Principles

### I. Domain Integrity and Layered Boundaries
Cluckwork MUST preserve inward dependencies: the API may depend on Application and
Infrastructure, Application and Infrastructure may depend on Domain, and Domain MUST depend on
nothing outside itself. Expected business failures MUST use `Result` or `Result<T>`; exceptions
are reserved for invariant violations. Each feature MUST have a directly invoked handler and a
FluentValidation validator; MediatR is prohibited. Every aggregate mutation MUST increment its
`Version`, and every new mutation MUST include a test that proves a concurrent writer is rejected.
Changes to aggregate states or request-pipeline ordering MUST follow `docs/architecture.md`.

Rationale: explicit boundaries keep business rules independent of delivery and persistence, while
versioned mutations prevent silent lost updates.

### II. Tenant Isolation and Credential Safety
Every tenant-owned entity MUST carry `AccountId`, be protected by the structural global query
filter required by the EF model, and be stamped and write-validated by `TenantStampInterceptor`.
Flock-scoped reads MUST be derived from the mapped model; exclusions and parent-derived gates MUST
have an explicit rationale and a causal mutation test. Authentication and authorization controls
MUST fail closed: credential revocation requires a fresh database check, audit writes require a
resolved actor, and serving credentials MUST be non-blank, importable, and validated at boot.
Secrets, real credentials, private keys, and environment-specific values MUST NOT be committed;
test credentials MUST be generated at runtime.

Rationale: farms coexist in one deployment, so a missed filter or stale credential is a direct
cross-tenant or account-compromise risk rather than a recoverable presentation defect.

### III. Fail-Closed Data and Operations
`InitialCreate` MUST remain frozen; every schema change MUST use a new migration. Base reference
data MUST use guarded raw SQL and MUST NOT use `HasData` or `InsertData`. Every migration MUST
regenerate and commit `docs/schema/`. Production serving processes MUST NOT run migrations or seed
data on startup; explicit one-shot verbs perform those tasks and then exit. Boot guards MUST be
scoped by `ProcessRoles`, and every new serving-only violation MUST be represented in the process
role guard registry. Stateful work that cannot be replayed MUST stop EF transient retries at the
documented boundary. Runtime checks for proxy trust, database TLS, timezone support, credentials,
and readiness MUST fail closed unless an explicit documented opt-out exists.

Rationale: silent schema drift, replayed stateful work, and partially configured serving processes
produce plausible but incorrect behavior; explicit process roles and readiness gates make failure
visible before traffic is accepted.

### IV. Evidence-Backed Change
Claims about correctness MUST be backed by tests at the boundary where the behavior matters.
Database integration tests MUST use real PostgreSQL through Testcontainers, never SQLite. A guard
test MUST be mutation-tested before its protection is claimed; after two misses of the same shape,
the detection method MUST be replaced with exhaustive discovery plus explicit exclusions. Registry
changes MUST find guards by searching registry readers rather than by recalling a list. Contract
changes MUST inspect and update non-CI callers, including seeders, simulation scripts, and SPA E2E
flows. The repository MUST build without warnings, and applicable tests, type checks, generated-file
checks, and security gates MUST pass before work is declared complete.

Rationale: a green but irrelevant test suite is false assurance. Causal tests, adversarial guard
checks, and caller discovery demonstrate the stated invariant instead of merely exercising code.

### V. Reproducible, Host-Portable Delivery
The repository MUST remain hosting-provider agnostic: application code, committed configuration,
and normative documentation MUST express portable requirements and MUST NOT branch on or embed a
provider. Production dependencies MUST use committed lock files; third-party GitHub Actions MUST be
pinned to full commit SHAs. Runtime images MUST be non-root, digest-pinned, include tzdata and ICU,
and pass the high/critical vulnerability gate. A release MUST promote the already tested commit
image by server-side retagging, never rebuild it. Deployment MUST use the release digest, verify its
attestation and source, and confirm the promoted tag still resolves to that digest. The serving
topology and Postgres pooling mode MUST satisfy the single-leader and shared-state guarantees
documented in `AGENTS.md` and decision 271.

Rationale: portability prevents deployment policy from leaking into the product, while immutable
inputs and digest-based promotion preserve the identity of the artifact that CI actually tested.

## Additional Constraints

- The backend stack is .NET 10, C#, EF Core, and PostgreSQL; the SPA is React 19 with Vite.
- API writes MUST require authentication and an `Idempotency-Key`; endpoints MUST remain under the
  versioned `/api/v1` minimal-API groups.
- Nullable analysis and warnings-as-errors MUST remain enabled; unused imports are build failures.
- Production logs MUST be compact structured JSON on stdout and MUST preserve trace context.
- The full glibc runtime with tzdata and ICU is mandatory; Alpine, chiseled images, and invariant
  globalization are prohibited.
- The portable operational contract belongs in this repository. Provider manifests, infrastructure
  as code, secret-store wiring, concrete network values, and provider-specific runbooks belong in a
  separate deployment repository.
- Durable jobs are at-least-once and MUST be idempotent. Single-leader claims require a
  session-pinned PostgreSQL endpoint; transaction pooling MUST NOT be described as providing that
  guarantee.

## Development Workflow and Quality Gates

1. Before changing a rule in `AGENTS.md` that links to a decision record, contributors MUST read
   that decision and preserve its earned invariant or explicitly amend the accepted risk.
2. Before changing middleware order, aggregate state, domain terminology, or a syntax-inspecting
   guard, contributors MUST read the governing architecture, glossary, decision, or guard test.
3. Changes MUST be narrowly scoped. Application behavior, tests, generated artifacts, documentation,
   and known callers MUST remain synchronized in the same change.
4. Every user-visible concept or meaning change MUST update the product glossary, SPA Help page, and
   in-app glossary. Every migration MUST update generated schema documentation.
5. Verification MUST match risk: run focused tests during development, then the applicable solution
   build, test suites, frontend checks, schema checks, and security gates before merge. Integration
   checks that require Docker MUST not be replaced with weaker substitutes.
6. Work MUST occur on a branch and reach `main` through a pull request. PR titles and commits MUST
   follow the repository's conventional-commit and release rules. Agents MUST commit or push only
   when explicitly authorized by a human.
7. Reviewers MUST treat a violated constitutional rule, a stale caller or document, an unproved
   guard, and a provider-specific repository dependency as blocking defects.

## Governance

This constitution distills the non-negotiable project rules. `AGENTS.md` is the canonical execution
manual, and its linked records in `docs/decisions/` contain the authoritative rationale and exact
constraints behind earned and accepted-risk rules. A contributor who finds a conflict among these
sources MUST stop and resolve it explicitly; silently choosing one source is prohibited.

Amendments require a documented proposal, review of every affected decision record and operational
caller, and synchronized updates to this constitution and `AGENTS.md` where their scopes overlap.
Removing or redefining a principle requires a MAJOR version bump. Adding a principle or materially
expanding governance requires a MINOR bump. Clarifications that do not change obligations require a
PATCH bump. The amendment date MUST be the date the constitutional text changes; the original
ratification date MUST remain unchanged.

Every specification, implementation plan, pull request, and review MUST check compliance with these
principles. Any exception MUST identify the exact rule, scope, owner, expiry or removal condition,
and supporting decision record. Unrecorded exceptions are invalid. Reviewers MUST reject changes
whose complexity, risk acceptance, or verification evidence is not justified.

**Version**: 1.0.0 | **Ratified**: 2026-08-31 | **Last Amended**: 2026-08-31
