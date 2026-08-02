# Credential Epoch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Deploy A's credential-epoch reader, immediate password-reset revocation, and client sign-out reason plumbing without exposing the future user-disable/email-change mutations.

**Architecture:** `ApplicationUser.CredentialEpoch` and `RefreshToken.IssuedEpoch` bind all accepted credentials to a monotonic user value. JWT and refresh validation enforce the binding; a middleware database projection enforces access-token binding before authorization. Password-reset paths increment the epoch in the transaction that already revokes refresh tokens. The user-directed migration revokes active legacy refresh tokens at cutover, as explicitly confirmed for this not-yet-deployed application.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs/middleware, EF Core/Postgres, ASP.NET Identity, xUnit/Testcontainers, React 19, Vite/Vitest, Playwright, k6.

## Global Constraints

- Do not implement #356 or #357 mutations/UI.
- Epoch is `int`, starts at `1`, and only moves upward; `0` is permanently invalid.
- `AspNetUsers.CredentialEpoch` defaults to `1`; `refresh_tokens.IssuedEpoch` defaults to `0`; every known mint site explicitly stamps the user epoch.
- The middleware must fail closed for absent or malformed claims by parsing them to `0`, query by `(UserId, AccountId)` without tracking, allow logout, and skip exception-handler re-execution.
- Pipeline order is `UseAuthentication → TenantResolution → CredentialEpoch → MustChangePassword → UseAuthorization → Idempotency`.
- Refresh mismatch must occur before grace/replay and must not revoke a family; same-epoch replay revocation is restricted to the presented token epoch.
- No credentials are hard-coded. Generated test credentials only.
- Deploy B documentation says it may not be exposed until all pre-Deploy-A processes are drained.

---

### Task 1: Storage, migration, and token issuance

**Files:**
- Modify: `src/Cluckwork.Infrastructure/Identity/ApplicationUser.cs`
- Modify: `src/Cluckwork.Infrastructure/Identity/RefreshToken.cs`
- Modify: `src/Cluckwork.Infrastructure/Identity/JwtTokenService.cs`
- Modify: `src/Cluckwork.Infrastructure/Persistence/Configurations/ApplicationUserConfiguration.cs`
- Create: `src/Cluckwork.Infrastructure/Persistence/Migrations/*_CredentialEpoch.cs`
- Modify: `src/Cluckwork.Infrastructure/Persistence/Migrations/AppDbContextModelSnapshot.cs`
- Test: `tests/Cluckwork.Api.IntegrationTests/CredentialEpochTests.cs`

**Interfaces:**
- Produces `ApplicationUser.CredentialEpoch`, `ApplicationUser.DisabledAt`, `ApplicationUser.DisabledBy`, `RefreshToken.IssuedEpoch`, and the JWT `credential_epoch` claim.

- [ ] **Step 1: Write failing integration tests** for a valid current-epoch JWT and a token minted from every login/change-password/refresh path carrying the matching epoch. Name each test for the removed claim/property/mint assignment that must make it fail.
- [ ] **Step 2: Run the focused tests** and confirm failure because the claim and columns do not exist.
- [ ] **Step 3: Implement the minimal model/configuration/JWT/mint assignments**, then generate the migration and hand-edit it only as required for the confirmed cutover revocation and lock timeout.
- [ ] **Step 4: Run focused tests** and confirm green.

### Task 2: Credential epoch enforcement and reset bumps

**Files:**
- Create: `src/Cluckwork.Api/Middleware/CredentialEpochMiddleware.cs`
- Modify: `src/Cluckwork.Api/Program.cs`
- Modify: `src/Cluckwork.Infrastructure/Identity/IdentityProvider.cs`
- Modify: `src/Cluckwork.Infrastructure/Identity/StepUpGrantService.cs`
- Test: `tests/Cluckwork.Api.IntegrationTests/CredentialEpochTests.cs`

**Interfaces:**
- Consumes the Task 1 columns/claim.
- Produces a 401 `Auth.CredentialsSuperseded` response for an access-token mismatch and generic invalid-refresh failures for rejected refresh credentials.

- [ ] **Step 1: Write failing integration tests** for a live access token rejected immediately after each password reset path, separately absent/malformed epoch claims, logout allowance, exception-handler re-execution, disabled-user login/refresh/step-up refusal, and precise middleware ordering.
- [ ] **Step 2: Run the focused tests** and confirm each fails at the missing enforcement/bump behavior.
- [ ] **Step 3: Add the middleware and reset epoch mutation** in their existing transactions; place refresh checking before grace/replay and scope replay family invalidation by issued epoch.
- [ ] **Step 4: Run focused tests** and confirm green.

### Task 3: Cutover and concurrency contracts

**Files:**
- Modify: `tests/Cluckwork.Api.IntegrationTests/CredentialEpochTests.cs`
- Modify: `tests/Cluckwork.Api.IntegrationTests/RefreshTokenFlowTests.cs`
- Modify: `tests/Cluckwork.Api.IntegrationTests/MigrationSecurityReviewTests.cs` as needed

**Interfaces:**
- Consumes runtime epoch enforcement and migration artifacts.
- Produces coverage for legacy `IssuedEpoch=0`, migration-time legacy revocation, race-safe child rejection, no cross-epoch replay revocation, and preserved same-epoch theft detection.

- [ ] **Step 1: Write failing database-backed tests** that seed pre-migration-shaped legacy rows and use barriers around refresh/reset interleavings; assert child refresh usability by presenting it.
- [ ] **Step 2: Run the focused tests** and confirm they fail under the pre-epoch or incorrectly ordered/scoped implementations.
- [ ] **Step 3: Add only the synchronization test hooks already established by the integration harness, and complete the production query predicates required by these observable contracts.**
- [ ] **Step 4: Run focused tests and mutation checks** (remove/check-bypass mutations locally) and restore production code after each confirmed red result.

### Task 4: SPA teardown reason and documentation

**Files:**
- Modify: `web/src/api/client.ts`
- Modify: `web/src/auth/AuthContext.tsx`
- Modify: `web/src/routes/Login.tsx`
- Modify: `web/src/api/client.test.ts`
- Modify: `web/src/auth/AuthContext.lifecycle.test.tsx`
- Modify: `web/src/routes/Login.test.tsx`
- Modify: `web/src/i18n/en.ts`, `web/src/i18n/es.ts`, `web/src/i18n/tl.ts`, and translation namespace metadata
- Modify: `web/src/routes/HelpPage.tsx` and its test
- Modify: `specs/product/GLOSSARY.md`
- Modify: the repository deployment documentation selected by existing deployment guidance

**Interfaces:**
- `onUnauthenticated` receives the original 401 problem title.
- Login maps `Auth.CredentialsSuperseded` and `Auth.AccountDisabled` to translated copy while retaining generic invalid-credential behavior for a direct failed sign-in.

- [ ] **Step 1: Write failing Vitest cases** showing 401 titles survive refresh exhaustion/auth teardown and are rendered on Login, including generic 401 compatibility.
- [ ] **Step 2: Run focused Vitest files** and confirm failure.
- [ ] **Step 3: Implement typed reason propagation, localized messages, Help/Glossary/deploy-drain prose, and only the related test changes.**
- [ ] **Step 4: Run focused Vitest files** and confirm green.

### Task 5: Non-CI harness contracts and final verification

**Files:**
- Inspect/update only when the changed token/cookie contract requires it: `tools/simulation/ui/specs/session-refresh.spec.ts`, `tools/simulation/ui/specs/session-races.spec.ts`, `tools/simulation/k6/auth.js`, `tools/simulation/k6/auth-smoke.js`, `tools/simulation/k6/config.js`, `tools/simulation/reset.sh`, `tools/simulation/bootstrap.sh`.

- [ ] **Step 1: Inspect the direct refresh/401/cookie assertions and adjust only behavior made intentionally different by the issue.**
- [ ] **Step 2: Run every required command exactly as requested:** `dotnet build Cluckwork.sln`, `dotnet test Cluckwork.sln`, `cd web && npm run typecheck && npm test`, `bash tools/simulation/reset.sh`, and `cd tools/simulation/ui && npm test && npm run mutation`.
- [ ] **Step 3: Review `git diff --check`, inspect the diff against the issue checklist, commit the complete scoped change, push `feat/364-credential-epoch`, and open the PR.**
