# Change User Email (#357) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax
> for tracking. The feature-driver's Phase 9 runbook is the controlling execution artifact when it is
> more prescriptive than this plan.

**Goal:** Let an Owner safely correct a user's login email within one farm while terminating the target's
old credentials, preserving tenant isolation, and explaining the no-email-verification product contract.

**Architecture:** A dedicated `ChangeUserEmail` application slice consumes step-up proof and calls a
narrow Identity port. `IdentityProvider` serializes on the account row, rechecks the actor, updates all
four Identity login columns through the configured normalizer, rotates the stamp, bumps the credential
epoch, revokes refresh tokens, and audits atomically. A dedicated Users-page dialog surfaces the
same-farm duplicate as an accessible field error and otherwise follows existing dialog/session patterns.

**Tech Stack:** .NET 10, ASP.NET Core Identity, EF Core 10, Npgsql/PostgreSQL, FluentValidation, xUnit,
React 19, TypeScript 7, Vitest, Testing Library, i18next.

## Global Constraints

- Follow `AGENTS.md`, especially account-scoped Identity uniqueness, `SingleAttemptExecution`, direct
  `AuditActions` call-site syntax, write-contract caller review, and same-PR Help/glossary/locales.
- No migration or schema-doc regeneration: #356/#532 already shipped every required column and index.
- `PUT /api/v1/users/{id}/email`: Owner-only group, unconditional step-up, `Idempotency-Key`, 2048-byte
  body cap, 204 success/no-op, 403 proof/stale actor, 404 foreign id, 409 duplicate/concurrency, 422 sole
  Owner refusal.
- Per-account uniqueness only. A normalized duplicate in another account must succeed.
- Exact equality is `StringComparison.Ordinal` after trimming input; case-only changes are real changes.
- A real change preserves `EmailConfirmed` and disabled state, rotates `SecurityStamp` and
  `ConcurrencyStamp`, increments `CredentialEpoch` exactly once, revokes every active refresh token,
  and audits `{ oldEmail, newEmail }`.
- Catch a database duplicate only for PostgreSQL SQLSTATE `23505` plus constraint `EmailIndex` or
  `UserNameIndex`. Propagate every other `DbUpdateException`.
- Never clear the whole EF tracker. A failed operation restores only values/state it changed and detaches
  only its own unsaved audit event.
- TDD: run each named test red for the stated reason before product code that makes it green. Record the
  red reason and final summary for the implementer report.
- Warnings are errors; nullable and unused usings are build-breaking. Do not add dependencies.

---

### Task 0: Baseline and branch

**Files:** none.

- [ ] **Step 1: Confirm the create targets do not exist.**

Run:

```bash
git ls-files \
  src/Cluckwork.Application/Features/Users/ChangeUserEmail/ChangeUserEmailCommand.cs \
  src/Cluckwork.Application/Features/Users/ChangeUserEmail/ChangeUserEmailValidator.cs \
  src/Cluckwork.Application/Features/Users/ChangeUserEmail/ChangeUserEmailHandler.cs \
  tests/Cluckwork.Api.IntegrationTests/ChangeUserEmailTests.cs \
  tests/Cluckwork.Api.IntegrationTests/ChangeUserEmailRaceTests.cs
```

Expected: no output. If any path exists, stop and report instead of overwriting it.

- [ ] **Step 2: Record a clean baseline.**

Run with Docker available:

```bash
dotnet build Cluckwork.sln -c Release
dotnet test Cluckwork.sln -c Release --no-build --verbosity normal
cd web && npm test -- --run && npm run typecheck && cd ..
```

Expected: build ends `0 Warning(s) 0 Error(s)`; all tests/typecheck pass. Record exact totals and any
pre-existing failure. Do not repair a baseline failure without owner scope approval.

- [ ] **Step 3: Create the feature branch.**

```bash
git switch -c feat/change-user-email
```

Expected: branch is `feat/change-user-email`, based on the recorded baseline SHA.

---

### Task 1: Backend vertical slice — contract, Identity mutation, and HTTP evidence

**Files:**

- Create: `src/Cluckwork.Application/Features/Users/ChangeUserEmail/ChangeUserEmailCommand.cs`
- Create: `src/Cluckwork.Application/Features/Users/ChangeUserEmail/ChangeUserEmailValidator.cs`
- Create: `src/Cluckwork.Application/Features/Users/ChangeUserEmail/ChangeUserEmailHandler.cs`
- Create: `tests/Cluckwork.Api.IntegrationTests/ChangeUserEmailTests.cs`
- Create: `tests/Cluckwork.Api.IntegrationTests/ChangeUserEmailRaceTests.cs`
- Modify: `src/Cluckwork.Application/Common/IIdentityProvider.cs`
- Modify: `src/Cluckwork.Application/Common/AuditActions.cs`
- Modify: `src/Cluckwork.Infrastructure/Identity/IdentityProvider.cs`
- Modify: `src/Cluckwork.Api/Endpoints/Users/UserEndpoints.cs`
- Modify: `src/Cluckwork.Api/Hosting/CluckworkFeatureServiceCollectionExtensions.cs`
- Modify: `tests/Cluckwork.Application.Tests/TenantBypass/Data/filter-free-set-sites.tsv`
- Modify: `web/src/i18n/enums.ts`
- Modify: `web/src/i18n/en.ts`
- Modify: `web/src/i18n/es.ts`
- Modify: `web/src/i18n/tl.ts`

**Interfaces:**

- Produces: `ChangeUserEmailCommand(Guid UserId, string Email, string? StepUpToken)`.
- Produces: `ChangeUserEmailHandler.HandleAsync(ChangeUserEmailCommand command, Guid accountId,
  Guid actingUserId, CancellationToken ct) -> Task<Result>`.
- Produces: `IIdentityProvider.ChangeUserEmailAsync(Guid accountId, Guid userId, string email,
  Guid actingUserId, CancellationToken ct = default) -> Task<Result>`.
- Produces: `AuditActions.UserEmailChanged = "User.EmailChanged"` and matching SPA audit-enum entries.
- Produces: `PUT /api/v1/users/{id}/email` request `{ email }` and status contract from Global Constraints.

- [ ] **Step 1: Write the endpoint/Identity tests before adding the route.**

`ChangeUserEmailTests.cs` uses the HTTP fixture patterns in `ChangeUserRoleTests.cs`. Add these named facts
with positive controls rather than broad status-only assertions:

```csharp
[Fact] public async Task Change_WritesAllFourColumnsThroughTheConfiguredNormalizer()
[Fact] public async Task CrossAccountDuplicate_SucceedsInBothFarms()
[Fact] public async Task SameAccountDuplicate_Is409UsersDuplicateEmail()
[Fact] public async Task Change_BumpsEpochOnce_AndKillsTheLiveAccessAndRefreshTokens()
[Fact] public async Task NewEmailLogsIn_OldEmailFails_AndOldEmailCanBeReusedInTheFarm()
[Fact] public async Task ExactTrimmedNoOp_LeavesEpochStampsTokensAndAuditUnchanged()
[Fact] public async Task CaseOnlyCorrection_IsARealChange()
[Fact] public async Task SoleOwnerSelfChange_Is422_AndNamesAddingASecondOwner()
[Fact] public async Task SelfChange_WithAnotherActiveOwner_Succeeds()
[Fact] public async Task ForeignUserId_Is404_AndForeignRowIsUnchanged()
[Fact] public async Task DisabledTarget_CanBeCorrected_AndStaysDisabled()
[Fact] public async Task Change_PreservesEmailConfirmed()
[Fact] public async Task Change_AuditsOldAndNewEmail_OnlyOnARealChange()
[Fact] public async Task Change_InvalidatesTheTargetsOutstandingStepUpGrant()
[Fact] public async Task MissingStepUp_Is403_AndLeavesIdentityColumnsUnchanged()
[Fact] public async Task InvalidEmail_Is400WithEmailFieldCode()
[Fact] public async Task OversizedBody_Is413()
```

For the four-column assertion, use `factory.WithTenantScopeAsync(accountId, ...)` and still predicate the
filter-free `db.Users` set by both target id and `AccountId`; do not use `IgnoreQueryFilters()`. Resolve
`UserManager<ApplicationUser>` in that scope and derive the normalized expectations before asserting:

```csharp
var expectedEmail = "Case.Change@Farm.Test";
var expectedNormalizedEmail = userManager.NormalizeEmail(expectedEmail);
var expectedNormalizedUserName = userManager.NormalizeName(expectedEmail);
Assert.Equal(expectedEmail, row.Email);
Assert.Equal(expectedNormalizedEmail, row.NormalizedEmail);
Assert.Equal(expectedEmail, row.UserName);
Assert.Equal(expectedNormalizedUserName, row.NormalizedUserName);
```

- [ ] **Step 2: Write the adversarial transaction/race tests before implementation.**

`ChangeUserEmailRaceTests.cs` reuses the account-fence and handler-resolution shapes from
`DisableUserRaceTests.cs`/`ChangeUserRoleRaceTests.cs`. Add:

```csharp
[Fact] public async Task RefreshInFlightAcrossEmailChange_LeavesNoUsableChild()
[Fact] public async Task ConcurrentSameFarmClaims_OneWins_AndLoserIsDuplicateEmail()
[Fact] public async Task ConcurrentStampChange_DuringEmailMutation_Is409()
[Fact] public async Task DuplicateFailure_RestoresTrackedFieldsAndStampsBeforeLaterSave()
[Fact] public async Task FinalConcurrencyFailure_RemovesEpochAndPendingAuditFromTracker()
[Fact] public async Task DisabledCoOwner_DoesNotSatisfySoleOwnerSelfChangeGuard()
[Fact] public async Task ActorDemotedWhileQueued_Is403_AndTargetIsUnchanged()
[Fact] public void IsUserEmailConflict_AcceptsOnlyTheTwoNamedUniqueConstraints()
```

The refresh test must deterministically park the email change on the account fence, mint a child from the
pre-change session, release the fence, await the successful change, then present both child faces:

```csharp
Assert.Equal(HttpStatusCode.Unauthorized,
    (await factory.CreateAuthedClient(child.AccessToken).GetAsync("/api/v1/flocks")).StatusCode);
Assert.Equal(HttpStatusCode.Unauthorized,
    (await factory.CreateClient().PostRefreshAsync(
        child.RefreshToken, expectedAccount: accountId.ToString())).StatusCode);
```

The tracker test must keep the same DI scope/`AppDbContext`, receive `Users.DuplicateEmail`, modify and
save an unrelated entity, then query through a fresh scope and prove the old email and both old stamps
remain. A test that disposes the failed context before the later save is invalid.

- [ ] **Step 3: Run the new tests red.**

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~ChangeUserEmail' --verbosity normal
```

Expected RED: the route returns 404 and direct handler/provider references do not yet compile. Record
those reasons. A database/Docker startup failure is not the required red.

- [ ] **Step 4: Add the application contract and validation.**

Use these exact public shapes:

```csharp
public sealed record ChangeUserEmailCommand(Guid UserId, string Email, string? StepUpToken);

public sealed class ChangeUserEmailHandler(IIdentityProvider identity, IStepUpGrantService stepUp)
{
    public async Task<Result> HandleAsync(
        ChangeUserEmailCommand command, Guid accountId, Guid actingUserId, CancellationToken ct)
    {
        var proof = await stepUp.ValidateAsync(accountId, actingUserId, command.StepUpToken, ct);
        if (!proof.IsSuccess) return proof;
        return await identity.ChangeUserEmailAsync(
            accountId, command.UserId, command.Email.Trim(), actingUserId, ct);
    }
}
```

The validator applies required, `EmailAddress()`, and maximum 256 rules with the existing dotted codes
`User.Email.Required`, `User.Email.Format`, and `User.Email.MaxLength`. Register validator and handler in
`CluckworkFeatureServiceCollectionExtensions.AddValidators/AddHandlers`.

- [ ] **Step 5: Add the narrow Identity port, audit vocabulary, and direct registry readers.**

Add the method signature above to `IIdentityProvider`. Add `UserEmailChanged` directly to `AuditActions`.
Update all three audit label catalogs and the three named `web/src/i18n/enums.ts` structures:
`AUDIT_ACTION_VALUES`, `AUDIT_ACTION_KEYS`, and `AUDIT_ACTION_ENTITY_TYPE`, so the value, translation
key, entity type, and parity tests remain 1:1. Do not forward the audit action through a helper parameter;
`AuditVocabularyCoverageTests` accepts the direct constant at the write call.

- [ ] **Step 6: Implement the Identity transaction.**

Follow the locked order in `docs/plans/357-change-user-email/00-driver-handout.md`: ambient single attempt,
unconditional account lock, scoped target lookup, active-Owner recheck, exact no-op, sole-Owner guard,
snapshot, four-column assignment through `userManager.NormalizeEmail/NormalizeName`, stamp rotation,
epoch increment, full refresh revocation, direct audit, final save, commit.

The database-race discriminator is `internal static` so the integration-test assembly can exercise it
through the existing `InternalsVisibleTo` seam, matching `AccountProvisioner.IsSlugConflict`:

```csharp
internal static bool IsUserEmailConflict(DbUpdateException exception) =>
    exception.InnerException is PostgresException
    {
        SqlState: PostgresErrorCodes.UniqueViolation,
        ConstraintName: "EmailIndex" or "UserNameIndex"
    };
```

Map validator errors `DuplicateEmail` or `DuplicateUserName` to:

```csharp
Error.Conflict("Users.DuplicateEmail", "A user with this email already exists.")
```

Before assigning any email/login field and before calling `UpdateSecurityStampAsync`, snapshot
`Email`, `NormalizedEmail`, `UserName`, `NormalizedUserName`, `SecurityStamp`, `ConcurrencyStamp`, and
`CredentialEpoch`, plus incoming `EntityState` and per-property modified flags. Snapshot the set of
already-tracked `AuditEvent` instances before writing this operation's audit. On failure, restore those
exact user values/flags and detach only the newly Added matching audit entry. Do not call
`ChangeTracker.Clear()` and do not disturb caller entries. Preserve `EmailConfirmed` without assigning it.

- [ ] **Step 7: Wire the HTTP endpoint.**

Add:

```csharp
group.MapPut("/{id:guid}/email", ChangeUserEmail)
    .WithMaxRequestBodyBytes(2048)
    .WithName("ChangeUserEmail")
    .WithSummary("Change a user's login email and revoke their sessions.");

public sealed record ChangeUserEmailRequest(string Email);
```

The delegate builds the command from the route, body, and step-up header; validates it; invokes the
handler with tenant/actor ids; returns 204/403/404/409/422 exactly as specified. Match
`result.Error.Code == "Users.DuplicateEmail"` literally for 409 before the sibling
`EndsWith(".Conflict", StringComparison.Ordinal)` concurrency branch; `Users.DuplicateEmail` does not
carry the suffix and would otherwise fall into the trailing 422. Do not add a self-target HTTP
short-circuit: the provider's in-lock active-Owner count decides whether self-change is safe. The 413 is
produced by `.WithMaxRequestBodyBytes(2048)` before the delegate runs; do not add a second manual
body-length check.

- [ ] **Step 8: Run focused backend tests green, then the backend safety guards.**

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~ChangeUserEmail' --verbosity normal
dotnet test tests/Cluckwork.Application.Tests/Cluckwork.Application.Tests.csproj \
  --filter 'FullyQualifiedName~AuditVocabularyCoverageTests|FullyQualifiedName~ValidatorErrorCodeCoverageTests' \
  --verbosity normal
dotnet build Cluckwork.sln -c Release
```

Expected: focused tests pass; build ends `0 Warning(s) 0 Error(s)`.

- [ ] **Step 9: Commit the backend vertical slice.**

```bash
git add src/Cluckwork.Application src/Cluckwork.Infrastructure/Identity/IdentityProvider.cs \
  src/Cluckwork.Api/Endpoints/Users/UserEndpoints.cs \
  src/Cluckwork.Api/Hosting/CluckworkFeatureServiceCollectionExtensions.cs \
  tests/Cluckwork.Api.IntegrationTests/ChangeUserEmailTests.cs \
  tests/Cluckwork.Api.IntegrationTests/ChangeUserEmailRaceTests.cs \
  web/src/i18n/enums.ts web/src/i18n/en.ts web/src/i18n/es.ts web/src/i18n/tl.ts
git commit -m "feat: let owners change user email addresses"
```

---

### Task 2: Users-page dialog, field error, and localized no-verification guidance

**Files:**

- Modify: `web/src/api/cluckwork.ts`
- Modify: `web/src/routes/UsersPage.tsx`
- Modify: `web/src/routes/UsersPage.test.tsx`
- Modify: `web/src/routes/HelpPage.tsx`
- Modify: `web/src/routes/HelpPage.test.tsx`
- Modify: `web/src/i18n/en.ts`
- Modify: `web/src/i18n/es.ts`
- Modify: `web/src/i18n/tl.ts`
- Modify: `specs/product/GLOSSARY.md`

**Interfaces:**

- Produces: `changeUserEmail(id, { email }, key?, stepUpToken?) -> Promise<void>`.
- Produces: dedicated Change-email row action/dialog with `emailFieldError` and `activeEmail` late-response
  protection.
- Produces: Help roles guidance plus an in-app glossary row and product glossary definition explaining
  immediate login-address replacement and no confirmation email.

- [ ] **Step 1: Write the SPA tests red before importing the new client function.**

Extend the `vi.mock("../api/cluckwork")` contract and add these facts to `UsersPage.test.tsx`:

```typescript
it("opens a dedicated Change email dialog for the selected row")
it("requires step-up, clears the password, and sends trimmed email with one idempotency key")
it("renders Users.DuplicateEmail beside the email input with aria-invalid")
it("clears the field error on edit, close, and target change")
it("does not attach a late duplicate response to a reopened dialog")
it("a successful non-self change refreshes the row and reports the new email")
it("does not claim that a confirmation email will be sent")
```

The conflict test returns `new ApiError(409, "Users.DuplicateEmail", "A user with this email already exists.")`, then asserts the dialog's
email input has `aria-invalid="true"`, `aria-describedby` points to the visible translated field error,
and the generic `<DialogError>` is empty. The late-response test uses a controllable promise and switches
dialog target before resolving it.

- [ ] **Step 2: Write Help/glossary tests red.**

Add tests to `HelpPage.test.tsx` that override the new English term/definition keys and prove the rendered
row reads from i18n, then assert all three locales have non-English-placeholder definitions containing
their equivalent of “no confirmation email.” Add a product-glossary assertion only if an existing test
already reads `specs/product/GLOSSARY.md`; otherwise verify that file in Step 7 with `rg` rather than
creating a new filesystem-coupled Vitest test.

- [ ] **Step 3: Run the frontend tests red.**

```bash
cd web
npm test -- --run src/routes/UsersPage.test.tsx src/routes/HelpPage.test.tsx
```

Expected RED: no Change-email control/client mock and no new Help glossary row. An i18n initialization
failure is not the required red.

- [ ] **Step 4: Add the API client.**

```typescript
export const changeUserEmail = (
  id: string, body: { email: string }, key?: string, stepUpToken?: string,
) => apiPut<void>(
  `/users/${id}/email`, body, key,
  stepUpToken ? { [STEP_UP_HEADER]: stepUpToken } : undefined,
);
```

- [ ] **Step 5: Implement the dedicated dialog using existing primitives.**

Add a row action distinct from Edit name. State includes target user, working email, current password,
`emailFieldError`, and `activeEmail`. The submit flow is:

```typescript
errors.beginAttempt("change-email");
setEmailFieldError(null);
const password = emailStepUpPassword;
setEmailStepUpPassword("");
const grant = await stepUp(password);
await changeUserEmail(target.id, { email: emailValue.trim() }, keyFor(scope), grant.token);
```

Capture `const targetId = target.id` before the first await and guard every post-await write with
`if (activeEmail.current !== targetId) return;`; opening sets `activeEmail.current` synchronously, and
close sets it to null before clearing state. Use `try/catch` with that exact active-target check. Only
`err instanceof ApiError && err.status === 409 && err.title === "Users.DuplicateEmail"` writes
`emailFieldError`; all other errors go through `errors.report("change-email", errText(err))`. On input
change clear the field error. On close/logout/target change clear password, field error, idempotency scope,
and active target consistently with neighboring dialogs.

Use `useId()` for hint/error ids. The input must carry:

```tsx
aria-invalid={emailFieldError !== null}
aria-describedby={emailFieldError ? emailErrorId : emailHintId}
```

Render the field error as `<p className="error" role="alert" id={emailErrorId}>…</p>`. Keep
`DialogError` for non-field failures. The hint says the new address is the next login and no confirmation
email is sent. The acting Owner's password is `autoComplete="current-password"`.

- [ ] **Step 6: Add all user-facing copy in English, Spanish, and Tagalog.**

Add Users namespace keys for action, title, hint, email/password labels, submit/success, duplicate-field
message, and sole-Owner refusal if the API description is surfaced. Add Help roles guidance and
`glossaryLoginEmailTerm`/`glossaryLoginEmailDef` in all three catalogs. Add the corresponding table row in
`HelpPage.tsx`. Spanish and Tagalog must be genuine translations, not English placeholders.

Update `specs/product/GLOSSARY.md` with “Login email”: the address used with a farm code to sign in; an
Owner can replace it immediately; old sessions end; no confirmation email is sent.

- [ ] **Step 7: Run focused UI, i18n, and documentation checks green.**

```bash
cd web
npm test -- --run src/routes/UsersPage.test.tsx src/routes/HelpPage.test.tsx \
  src/i18n/catalogParity.test.ts src/i18n/enums.test.ts
npm run typecheck
npm run i18n:scan
cd ..
rg -n 'Login email|confirmation email' specs/product/GLOSSARY.md
```

Expected: all tests/typecheck/scan pass and the product glossary names both the term and no-email rule.

- [ ] **Step 8: Commit the SPA and documentation slice.**

```bash
git add web/src/api/cluckwork.ts web/src/routes/UsersPage.tsx web/src/routes/UsersPage.test.tsx \
  web/src/routes/HelpPage.tsx web/src/routes/HelpPage.test.tsx \
  web/src/i18n/en.ts web/src/i18n/es.ts web/src/i18n/tl.ts specs/product/GLOSSARY.md
git commit -m "feat(web): add change-email administration flow"
```

---

### Task 3: Full verification and draft PR

**Files:**

- Modify: `docs/plans/357-change-user-email/00-driver-handout.md` only as directed by the driver/runbook.
- Modify: `docs/superpowers/plans/2026-08-25-change-user-email.md` only to tick executed checkboxes if the
  Phase 9 runbook asks for it.

- [ ] **Step 1: Check scope and generated-artifact expectations.**

```bash
git status --short
git diff --check
git diff --name-only HEAD~2
git diff --name-only HEAD~2 | rg 'Migrations|docs/schema' && exit 1 || true
rg -n 'MUTANT|\[DEBUG-' src tests web || true
```

Expected: only allowed implementation/plan/doc paths; no migration/schema changes; no mutation/debug tags.

- [ ] **Step 2: Run the CI-equivalent backend gates in the foreground.**

```bash
dotnet restore Cluckwork.sln --locked-mode
dotnet build Cluckwork.sln --configuration Release --no-restore
dotnet test Cluckwork.sln --configuration Release --no-build --verbosity normal
tools/schema-docs/generate.sh --check
```

Expected: restore/build/test/schema check all pass; record the full final test summary, not a background
process status.

- [ ] **Step 3: Run the CI-equivalent frontend gates in the foreground.**

```bash
cd web
npm ci
npm run test:coverage
npm run build
npm run verify:sw
cd ..
```

Expected: coverage gate, TypeScript/Vite build, and service-worker verification pass.

- [ ] **Step 4: Push and open the draft PR before review.**

```bash
git push -u origin feat/change-user-email
gh pr create --draft \
  --title "feat: let owners change user email addresses" \
  --body-file docs/plans/357-change-user-email/00-driver-handout.md
```

Expected: draft PR exists at the exact pushed head. Report branch, head SHA, PR URL, per-commit applied-by
attribution, red/green summaries, and anything not run. Do not mark ready and do not merge.

## Mutation rows reserved for driver Phase 11

| Mutant | Named test that must go red |
|---|---|
| Single-layer: delete explicit `NormalizedUserName` assignment; expect GREEN because `UpdateSecurityStampAsync` re-normalizes `UserName`. Then combined M1c: set `NormalizedUserName = null` immediately after successful stamp rotation; expect RED | `Change_WritesAllFourColumnsThroughTheConfiguredNormalizer` |
| Remove `AccountId` from target lookup | `ForeignUserId_Is404_AndForeignRowIsUnchanged` |
| Single-layer: remove either accepted constraint name and expect the concurrent-claim test GREEN because account-lock + validator absorb it; isolated-layer: run each removal against the direct classifier test and expect RED | `ConcurrentSameFarmClaims_OneWins_AndLoserIsDuplicateEmail`; `IsUserEmailConflict_AcceptsOnlyTheTwoNamedUniqueConstraints` |
| Broaden `IsUserEmailConflict` beyond the two named unique constraints | `IsUserEmailConflict_AcceptsOnlyTheTwoNamedUniqueConstraints` |
| Delete `CredentialEpoch++` | `Change_BumpsEpochOnce_AndKillsTheLiveAccessAndRefreshTokens` and the refresh-race child assertions |
| Delete `UpdateSecurityStampAsync` | `Change_InvalidatesTheTargetsOutstandingStepUpGrant` |
| Remove tracker restoration | `DuplicateFailure_RestoresTrackedFieldsAndStampsBeforeLaterSave` |
| Count disabled co-Owner as active | `DisabledCoOwner_DoesNotSatisfySoleOwnerSelfChangeGuard` |
| Treat normalized equality as no-op | `CaseOnlyCorrection_IsARealChange` |
| Remove `activeEmail` settle guard | `does not attach a late duplicate response to a reopened dialog` |
| Route duplicate through `DialogError` | `renders Users.DuplicateEmail beside the email input with aria-invalid` |
| Delete Help-page glossary row | Help override/wiring test added in Task 2 |

## Self-review

- Spec coverage: API/auth/body cap → Task 1; per-account duplicates/four columns → Task 1; epoch, refresh,
  stamp, no-op, sole Owner, disabled target, foreign id, audit → Task 1; barrier and tracker false-green
  cases → Task 1; SPA field error/accessibility → Task 2; `en`/`es`/`tl`, Help, in-app/product glossaries
  → Task 2; CI/PR evidence → Task 3. No acceptance criterion is unassigned.
- Placeholder scan: clean; every code and test step names its exact implementation or assertion.
- Type consistency: the command, handler, port, request body, client body, audit value, and error codes are
  named once and consumed unchanged.
- Scope: no migration, SMTP, email verification, display-name refactor, shared dialog-error redesign, or
  unrelated credential work.
