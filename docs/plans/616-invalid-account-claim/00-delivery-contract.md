# Delivery contract — #616 invalid authenticated tenant claims

## 1. Identity

- **Slice:** GitHub #616 — reject authenticated requests with missing or malformed `account_id` before flock-scope resolution.
- **Mode:** bugfix.
- **Front half that produced this:** `bugfix-diagnosis`, signed off 2026-08-30.
- **Repo + default branch:** `/home/mforce/dev/cluckwork`, `main`.

## 2. Approved scope

- **Signoff artifact:** `docs/plans/616-invalid-account-claim/01-diagnosis.md`, SHA-256 `90600b28febefbd650c921f282d389139fafca8e820a8feb1dd79e973367246e`.
- **⛔ Signoff record:** repository owner approved the root cause and `TenantResolutionMiddleware` repair seam on 2026-08-30, including the `Guid.Empty` boundary, against the artifact hash above.
- **Acceptance criteria:** valid `sub` plus missing `account_id` returns a bodiless 401 with tenant/user unresolved and no downstream flock invocation or assignment query; malformed `account_id` has the same result; missing/malformed `sub` remains a matching 401 short-circuit; valid claims still resolve tenant, user, roles, logging scope and flock scope; anonymous health-shaped and `/error` paths remain database-independent; Tenant-before-Flock order remains pinned; the D1 repro changes from exact `(200, 1)` RED to `(401, 0)` GREEN and each guard has a causal mutation row.
- **Explicitly out of scope:** parseable `Guid.Empty`; changing JWT bearer validation; changing flock-scope defaults or query filters; changing credential-epoch behavior; schema, endpoint contracts, SPA code, localization, and Worker sales allocation.
- **Simplicity ceiling:** add one authenticated-only invalid-`account_id` short-circuit at `TenantResolutionMiddleware`, permanent direct middleware-chain tests, and only comments made stale by that behavior; expected product scope is two API source files plus one integration-test file, with no new abstraction, service, state, dependency, schema, endpoint, or frontend-code surface.
- **Implementation plan:** none — bugfix mode has one red-first increment; the approved diagnosis above is the plan source and Phase 9's runbook is the executable specification.

## 3. Invariants

| ID | Invariant | Enforcement sites (symbols) | Discovered | Source |
|---|---|---|---|---|
| INV-1 | An authenticated ordinary HTTP request resolves both tenant and current user from parseable required claims before flock resolution, or returns 401 first. | `TenantResolutionMiddleware.InvokeAsync`; `Program.cs`; `CredentialEpochMiddlewareOrderTests.Program_PinsTheCompleteCredentialGateSequence` | front-half signoff | #616 diagnosis, 2026-08-30 |
| INV-2 | Anonymous and non-HTTP callers may remain unresolved without a tenant database read; flock liveness and `/error` re-execution retain their explicit bypasses. | `TenantResolutionMiddleware`; `FlockScopeResolutionMiddleware`; `UnresolvedLivenessRequest_ResolvesUnrestricted_WithoutDatabaseAccess`; `ErrorReExecution_SkipsAssignmentResolution_WithoutDatabaseAccess` | front-half signoff | #616 diagnosis, 2026-08-30 |
| INV-3 | A valid authenticated account claim still resolves `TenantContext` and populates diagnostic and logger scopes for the entire downstream request. | `ResolveAccountScope`; `RequestLoggingTests.Authenticated_request_completion_carries_the_account_id` | front-half signoff | #616 diagnosis, 2026-08-30 |

## 4. Ownership map

| Surface | Owning slice | Lands first | Forward-compat carried by |
|---|---|---|---|
| Authenticated `account_id` to `TenantContext` resolution | #616 | #388/#611, #532, #364 already landed | #616 preserves their contracts |
| Flock-scope semantics, unresolved system callers and tenant/flock filters | #388 / PR #611 | landed before #616 | #616 changes no flock resolver or filter |
| Worker sale allocation policy | #612 / PR #619 | landed before #616 | #616 changes no sale path |
| Ambient-principal suppression for login and refresh | #532 | landed before #616 | #616 keeps authenticated-only validation after suppression |
| Credential-epoch revocation and suspension | #364 / #579 | landed before #616 | #616 preserves its later fresh database read |

## 5. Baseline

- **Base commit:** `1690db89f69982fdb1b5a7017c6a0dcdf21787c6`.
- **Baseline result:** `dotnet build Cluckwork.sln -c Release` succeeded with 0 warnings/errors; `dotnet test Cluckwork.sln -c Release --no-build` passed 2072/2072 (361 Domain, 175 Application, 10 AppHost, 1526 API integration); the focused transient D1 command deterministically failed both cases with expected `(401, 0)` and actual `(200, 1)`.
- **Already failing at baseline:** committed suites have no failures, verified; only the intentional uncommitted #616 D1 reproduction is RED and defines this bug.

## 6. Agents (the Phase 0 answers)

- **Risk class and review budget:** high — authentication and tenant-isolation failure class, reversible code-only change with no migration or concurrency mutation; owner approved 2026-08-30. Three review seats: auth/tenant isolation, false-green mutation coverage, and repository-rule compliance.
- **Implementer:** isolated collaboration subagent, model `gpt-5.6-terra`, dispatched against the Phase 9 runbook in the dedicated #616 worktree. Rationale: correctness-critical code will be driver-authored verbatim, while the surrounding causal test chain needs capable codebase inference and an independent second pair of hands.
- **Reviewers:** fresh-context `gpt-5.6-sol` hunts auth/tenant-isolation and pipeline-order defects; Claude Code CLI `opus` hunts false-green tests and ineffective mutations; GitHub Codex connector hunts violations of the repository's written rules.
- **Reviewer brief shape:** one defect class per seat, limited to the changed API/test files, five-minute target, maximum three findings, and a MERGE-BLOCKING or FOLLOW-UP verdict per finding; both verdicts are legitimate and severity inflation costs an unnecessary round.
- **Seat briefed on the repo's own written rules:** GitHub Codex connector reads `AGENTS.md`, `CONTRIBUTING.md`, `docs/architecture.md`, and the directly linked decisions governing tenant isolation and guard mutation checks.
- **Driver fix budget:** 0 shipped-code fixes, chosen by the owner; transient Phase 11/12 verification mutations remain allowed.
- **Background visibility:** separate worker UI declined by approval of concise driver updates; the driver reports starts, blockers, and completion in this conversation.
- **Approval-prompt route:** collaboration-agent blocked prompts arrive as authenticated agent messages to the driver; the driver checks the mailbox at every turn boundary and after each tool completion.
- **Repo conventions, restated:** Api depends on Application/Infrastructure and those point inward to dependency-free Domain; use handlers without MediatR, FluentValidation, Result for expected failure, and minimal endpoint groups; every tenant entity is protected by global filters plus `TenantStampInterceptor`; authenticated tenant/user resolution precedes flock scope and credential epoch; do not change intentional unresolved non-HTTP behavior; migrations are one-per-change and `InitialCreate` is frozen, though this slice has no schema change; aggregate mutations require `Version++` plus parallel-race tests, though this slice mutates no aggregate; nullable and unused-using warnings break the build; all `src/` changes ship with tests, guards are mutation-checked, full .NET tests use real Postgres through Testcontainers, and a clean build has zero warnings/errors.
- **Lessons file:** `/home/mforce/.codex/feature-driver/616/lessons.md`, durable and excluded from every reviewer read root.

## 7. Merge authority

- **Who merges:** the repository owner at feature-driver Phase 13; the driver never merges or arms auto-merge.
- **Standing authorisation, if any:** none — this is an ordinary bugfix with no incident authority.
- **Disclosure constraints:** none — issue #616 and its non-exploitable current boundary are public repository context.
- **Follow-up and removal condition:** n/a — this is not an incident or temporary mitigation, so no removal issue exists.
