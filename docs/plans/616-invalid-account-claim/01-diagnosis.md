# #616 diagnosis — reject invalid authenticated tenant claims

Status: diagnosis and repair seam approved by the owner on 2026-08-30.

## Reported behavior

An authenticated HTTP principal with a valid `sub` and a missing or malformed `account_id` is not
rejected by `TenantResolutionMiddleware`. The middleware leaves `TenantContext` unresolved, resolves
`CurrentUserContext`, and invokes the next middleware. Because `FlockScopeResolutionMiddleware` is next
in the pinned request pipeline, a plain Worker can reach its live `UserRoleAssignments` query while the
tenant key is still `Guid.Empty`; other effective roles can resolve an unrestricted flock scope without
that query.

Independent tenant query filters and the later credential-epoch gate prevent tenant data exposure today.
The defect is nevertheless correctness-critical: the flock-scope resolver receives an HTTP identity
state that its documented fail-open branch reserves for non-HTTP/system callers.

## D1 — minimized red-capable reproduction

Diagnostic worktree base: `1690db89f69982fdb1b5a7017c6a0dcdf21787c6` (`origin/main`).

Transient test:
`tests/Cluckwork.Api.IntegrationTests/Debug616TenantResolutionReproTests.cs` in the isolated diagnosis
worktree. It constructs the real tenant middleware with an authenticated `ClaimsPrincipal`, a valid
`sub`, and either no `account_id` or `account_id=not-a-guid`. The downstream delegate counts invocations.

Command, run twice:

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  -c Release --no-restore \
  --filter 'FullyQualifiedName~Debug616TenantResolutionReproTests.AuthenticatedValidSub_InvalidAccountId_IsRejectedBeforeDownstream'
```

Captured symptom, identical for both cases and on both runs:

```text
Assert.Equal() Failure: Values differ
Expected: Tuple (401, 0)
Actual:   Tuple (200, 1)
Failed: 2, Passed: 0, Total: 2
```

The no-build rerun completed in 0.9 seconds; the two test cases themselves completed in 19–21 ms.
Removing the optional email claim did not change the symptom. The authentication type and valid `sub`
are load-bearing: they establish the authenticated HTTP path and isolate the invalid account claim.

## D2 — hypotheses and probes

| Rank | Hypothesis | Prediction | Result |
|---|---|---|---|
| 1 | `ResolveAccountScope` collapses an invalid authenticated account claim into the same `null` result used for an anonymous principal, leaving `InvokeAsync` unable to distinguish rejection from “no logging scope.” | An authenticated-only account-claim guard before downstream invocation makes both cases return 401 without invoking `next`. | Confirmed. A tagged transient guard made both cases green in 14 ms; it was reverted and the loop returned to the exact RED above. |
| 2 | The test principal is not authenticated. | `Identity.IsAuthenticated` is false. | Falsified. An explicit assertion is green while the reproduction remains red. |
| 3 | Middleware registration puts flock resolution before tenant resolution. | The source fence reports the reverse order or fails. | Falsified. `CredentialEpochMiddlewareOrderTests.Program_PinsTheCompleteCredentialGateSequence` passes and pins Tenant → Flock. |
| 4 | `AmbientPrincipalMiddleware` clears principals on ordinary authenticated endpoints. | The marker applies generally. | Falsified. It clears only endpoints carrying `IgnoresAmbientPrincipalAttribute`; the only mapped callers are login and refresh. |

## Root cause

`TenantResolutionMiddleware.ResolveAccountScope()` uses `null` for two semantically different outcomes:

1. no authenticated HTTP principal, where unresolved tenancy is valid and the pipeline should continue;
2. an authenticated principal whose required `account_id` cannot be parsed, where the request must stop.

`InvokeAsync()` treats both outcomes as “no disposable log scope,” validates only `sub`, resolves the
current user, and calls `next`. This violates the boundary established by the existing malformed-`sub`
branch: an authenticated HTTP identity must never reach downstream authorization with a partially
resolved tenant/user pair.

## Proposed repair seam

Fix `TenantResolutionMiddleware`, the component that owns conversion of the authenticated `account_id`
claim into `TenantContext`. For authenticated principals, parse `account_id` before resolving the current
user or calling downstream middleware. If it is missing or malformed, set 401 and return. Preserve the
existing anonymous path unchanged. Keep `FlockScopeResolutionMiddleware`'s unresolved-user behavior
unchanged because it is intentional for seeders, one-shot verbs, background jobs, anonymous health
requests, and `/error` re-execution.

Smallest viable production change: one authenticated-only invalid-`account_id` short-circuit in
`TenantResolutionMiddleware`; no new abstraction, state, service, schema, endpoint, or frontend-code
surface. The response changes from CredentialEpoch's `Auth.CredentialsSuperseded` ProblemDetails to a
bodiless 401 for this unmintable incomplete external-token state; existing SPA title consumers are
recorded in the implementer runbook, but require no code or localization change.

Alternatives rejected:

- Fixing lower in `FlockScopeResolutionMiddleware` is too late: it makes flock resolution interpret an
  invalid tenant claim, duplicates tenant-claim ownership, and risks changing intentional unresolved
  non-HTTP behavior.
- Fixing later in `CredentialEpochMiddleware` preserves the unwanted assignment query and unrestricted
  intermediate result.
- Requiring the claim globally in JWT bearer configuration is broader than the defect seam and would
  couple token validation to endpoints that deliberately erase ambient authentication.
- Changing `TenantContext` to fail closed while unresolved would alter seeders, one-shot verbs, health
  paths, global query filters, and every scoped database access; #616 does not require that blast radius.

## Invariants

- **INV-1 — authenticated tenant completeness:** before an authenticated ordinary HTTP request reaches
  flock resolution, both `TenantContext` and `CurrentUserContext` are resolved from parseable claims, or
  the request has already returned 401. Enforcement: `TenantResolutionMiddleware.InvokeAsync`; ordering:
  `Program.cs` plus `CredentialEpochMiddlewareOrderTests.Program_PinsTheCompleteCredentialGateSequence`.
- **INV-2 — unresolved non-HTTP/anonymous preservation:** an unauthenticated request and a non-HTTP caller
  may retain unresolved tenant/user state without a tenant database read; flock resolution preserves its
  explicit unresolved/error-re-execution behavior. Enforcement: `TenantResolutionMiddleware`,
  `FlockScopeResolutionMiddleware`, `UnresolvedLivenessRequest_ResolvesUnrestricted_WithoutDatabaseAccess`,
  and `ErrorReExecution_SkipsAssignmentResolution_WithoutDatabaseAccess`.
- **INV-3 — tenant claim logging parity:** a valid authenticated `account_id` still resolves
  `TenantContext` and populates both diagnostic and logger scopes for the lifetime of downstream request
  execution. Enforcement: `ResolveAccountScope` and
  `RequestLoggingTests.Authenticated_request_completion_carries_the_account_id`.

## Regression-test seam and mutation obligations

Use direct middleware-chain tests in `Cluckwork.Api.IntegrationTests`; they exercise the real
`TenantResolutionMiddleware` call boundary and can prove downstream non-invocation without relying on a
healthy database.

- valid `sub` + missing `account_id` → `(401, downstream calls 0)`;
- valid `sub` + malformed `account_id` → `(401, downstream calls 0)`;
- valid `account_id` + missing/malformed `sub` → existing matching 401 boundary, downstream calls 0;
- valid claims → tenant and user resolved, downstream called, and the existing flock-resolution tests
  preserve scope outcomes;
- anonymous health-shaped request → tenant/user remain unresolved and downstream runs; existing flock
  liveness and error-re-execution tests prove database independence.

Mutation rows must independently cover the invalid-account short-circuit, the existing invalid-`sub`
short-circuit, the valid authenticated path, and the anonymous bypass. The existing complete pipeline
order fence must remain green; moving flock resolution above tenant resolution is its causal mutation.

## Sibling occurrence inventory

Repo-wide claim parsing found these sibling paths:

- `CredentialEpochMiddleware` parses `sub`, `account_id`, and `credential_epoch` together; any missing or
  malformed identifier yields `credentialState=null` and a 401 before its `Users` query. It is fail closed,
  but is deliberately later than flock resolution and therefore cannot substitute for this fix.
- `StepUpGrantService` rejects missing/mismatched binding claims before its user read and shared-store
  consume. No matching defect.
- Refresh's expected-account header rejects a malformed present value before reading any cookie. No
  matching defect.
- Logout treats a missing/malformed account selector as “no selected per-farm cookie” by documented
  idempotent design; it is an explicit exception, not an authenticated authorization resolver.
- `ReportConcurrencyLimitFilter` currently documents that an unresolved tenant falls through to its
  handler. After this fix, the “authenticated JWT with no usable account_id” case becomes unreachable for
  ordinary HTTP requests; the comment should be corrected only if the implementation touches that file,
  otherwise file a focused documentation follow-up rather than widening #616.

## Why the suite missed it

The suite pins valid account logging, valid flock-scope outcomes, unresolved anonymous liveness,
`/error` database independence, and the complete middleware order. It has no test for the cross-product
that creates the defect: `IsAuthenticated=true`, valid `sub`, invalid `account_id`. The existing malformed
identity guard covers `sub` only, while the account helper silently returns `null`; no causal test asserted
that downstream middleware was not invoked. The later credential-epoch 401 and tenant query filters made
endpoint-level status/data tests stay green, masking the earlier fail-open intermediate state.

## Ownership map

| Surface | Owner | #616 treatment |
|---|---|---|
| Authenticated `account_id` → `TenantContext` resolution | #616 | Fix and causal tests here. |
| Flock-scope semantics, unresolved system callers, query filters | #388 / PR #611 | Preserve; use existing tests as guards. |
| Worker sales allocation policy | #612 / PR #619 | Closed sibling; no change. |
| Ambient-principal suppression for login/refresh | #532 | Preserve; no new marker. |
| Credential-epoch revocation and suspension | #364 / #579 | Preserve; do not move or cache its DB read. |

## D2b independent review

Independent reviewer: `gpt-5.6-sol`, read-only, fresh context, 2026-08-30.

Verdict: root mechanism and proposed seam confirmed. The reviewer independently reran the exact D1
command, observed both `(200, 1)` failures, walked the `UserRoleAssignment` global filter and the later
credential-epoch backstop, and confirmed the current pipeline order. It recommended the same narrow seam:
validate the claim itself inside the authenticated branch of `TenantResolutionMiddleware`, before
resolving `CurrentUserContext` or invoking `next`; do not use `tenant.IsResolved` as a proxy because
re-execution or a pre-resolved scope could mask an invalid current claim.

The reviewer also identified two test obligations not explicit in the first draft:

- assert that invalid account claims leave both tenant and current-user contexts unresolved;
- for the strongest no-query proof, compose Tenant → Flock against an unreachable database rather than
  relying only on a counted generic downstream delegate.

Existing order, anonymous liveness, and `/error` tests were judged sufficient and should not be
duplicated. One distinct edge was recorded for owner disposition: `Guid.Empty` is parseable and therefore
outside a literal missing/malformed check, although no server-minted access token can contain it.

## Baseline

Base `1690db89f69982fdb1b5a7017c6a0dcdf21787c6`:

- `dotnet build Cluckwork.sln -c Release` — succeeded, 0 warnings, 0 errors.
- `dotnet test Cluckwork.sln -c Release --no-build` — 2072/2072 passed: Domain 361,
  Application 175, AppHost 10, API integration 1526.
- D1 repro — expected RED on both cases with exact `(401, 0)` versus `(200, 1)` symptom.

## D3 owner signoff

The owner approved the root cause and proposed `TenantResolutionMiddleware` seam on 2026-08-30,
including the explicit decision that parseable `Guid.Empty` remains outside #616's
missing/malformed-claim scope.
