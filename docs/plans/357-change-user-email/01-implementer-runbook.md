# Runbook — #357: let Owners change a user's login email

You are the implementation owner in `/home/mforce/dev/cluckwork`. Execute
`docs/superpowers/plans/2026-08-25-change-user-email.md` from Task 0 through Task 3. This runbook is
controlling wherever it is more specific. Use strict RED → GREEN TDD, make the two planned commits,
push `feat/change-user-email`, and open the draft PR. Do not merge or mark it ready.

## Rules

- Read `AGENTS.md`, the driver handout, and the implementation plan completely before editing.
- Use the repository's `test-driven-development`/`superpowers:test-driven-development` workflow.
- The exact protected blocks below are correctness-critical. Transcribe them verbatim or stop and report;
  never repair or reshape them.
- Add the named tests before the product code, run the focused RED, and record the stable reason. A
  compile/discovery/Docker failure is not the expected RED.
- Do not add packages, migrations, schema changes, SMTP, verification mail, or unrelated refactors.
- Do not use `ChangeTracker.Clear()`, a uniqueness pre-read as race authority, a retry around the identity
  transaction, or `IgnoreQueryFilters()` for user lookup.
- Do not change existing tests to accommodate the feature. A conflicting existing guard is a stop.
- The current checkout deliberately contains the two untracked driver artifacts listed below. Preserve
  them; they are not product-code dirt.
- The configured `core.hooksPath` is stale (`/home/cesar/dev/cluckwork/.githooks`) on the driver host,
  although the repository hook at `.githooks/pre-commit` builds the working tree for staged C# and runs
  web typecheck for staged web files. Do not rewrite local Git config. The explicit gates below are the
  authority, and no commit may contain a non-compiling tree.

## Prerequisites and observed baseline

Run first:

```bash
git branch --show-current
git rev-parse HEAD
git status --short
git ls-files \
  src/Cluckwork.Application/Features/Users/ChangeUserEmail/ChangeUserEmailCommand.cs \
  src/Cluckwork.Application/Features/Users/ChangeUserEmail/ChangeUserEmailValidator.cs \
  src/Cluckwork.Application/Features/Users/ChangeUserEmail/ChangeUserEmailHandler.cs \
  tests/Cluckwork.Api.IntegrationTests/ChangeUserEmailTests.cs \
  tests/Cluckwork.Api.IntegrationTests/ChangeUserEmailRaceTests.cs
```

Expected: branch `main`, SHA `2c34771342cac2df26654ac90b2680567f2ffb1b`, status containing only
`docs/plans/357-change-user-email/` and
`docs/superpowers/plans/2026-08-25-change-user-email.md`, and no output from `git ls-files`. A SHA mismatch
is informational if the same approved plan artifacts are present; an existing create-target or unrelated
worktree change is a stop.

Driver baseline on that SHA, 2026-08-26:

- .NET SDK `10.0.111`, Node `v26.7.0`, npm `11.19.0`.
- locked restore passed; Release build: `0 Warning(s)`, `0 Error(s)`.
- full .NET suite: `Total tests: 1423`, `Passed: 1423`, `Test Run Successful.`
- schema-doc check: `docs/schema/ is up to date.`
- web coverage: `87 passed` files, `1940 passed` tests; coverage gates passed.
- web production build and `verify:sw` passed.
- npm and NuGet high+ vulnerability gates reported no advisories.

Create the branch only after the checks:

```bash
git switch -c feat/change-user-email
```

## Closed file scope

Create/edit only the files enumerated by Tasks 1–3 of the implementation plan, plus these two controlling
artifacts:

- `docs/plans/357-change-user-email/00-driver-handout.md`
- `docs/plans/357-change-user-email/01-implementer-runbook.md`
- `docs/superpowers/plans/2026-08-25-change-user-email.md`

The exhaustive product/test allow-list is: the three new `ChangeUserEmail` application files; the two new
integration-test files; `IIdentityProvider.cs`, `AuditActions.cs`, `IdentityProvider.cs`,
`UserEndpoints.cs`, `CluckworkFeatureServiceCollectionExtensions.cs`; `web/src/api/cluckwork.ts`,
`UsersPage.tsx`, `UsersPage.test.tsx`, `HelpPage.tsx`, `HelpPage.test.tsx`, `web/src/i18n/enums.ts`, and the
three locale catalogs; `specs/product/GLOSSARY.md`; and the pinned tenant-bypass registry
`tests/Cluckwork.Application.Tests/TenantBypass/Data/filter-free-set-sites.tsv`. Anything else is a stop.

`IdentityProvider.cs` line movement invalidates that registry even when the classified queries are
unchanged. Update only its exact `IdentityProvider.cs` line numbers to match the scanner output from
`TenantBypassRealTreeTests.RealSourceTree_FilterFreeSetSitesAreStableAndClassified`; preserve every detail
and reason verbatim, and classify any genuinely new candidate before proceeding. This is a pinned guard
artifact required by the existing test, not permission to edit the guard itself.

Before changing `IIdentityProvider`, preserve every existing implementer/decorator found by:

```bash
rg -l 'IIdentityProvider' src tests web --glob '!**/bin/**' --glob '!**/obj/**'
```

Before adding the audit action, run and save the reader enumeration:

```bash
rg -l 'AuditActions|AUDIT_ACTION_VALUES|AUDIT_ACTION_KEYS|AUDIT_ACTION_ENTITY_TYPE' \
  src tests web --glob '!**/bin/**' --glob '!**/obj/**'
```

The direct registry readers that must move together are
`tests/Cluckwork.Application.Tests/Common/AuditVocabularyCoverageTests.cs`,
`web/src/i18n/enums.ts`, and the three locale catalogs. Do not edit unrelated audit call sites.

## Increment 1 — backend contract and mutation

Follow Task 1 exactly, including every named test and the deterministic race/tracker fixtures. The focused
RED command is:

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~ChangeUserEmail' --verbosity normal
```

Record separate RED evidence for route behavior and direct provider behavior; a missing type compile error
is allowed only during the test-authoring checkpoint and must not be committed.

### PROTECTED — application shapes

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

### PROTECTED — database duplicate discriminator

```csharp
internal static bool IsUserEmailConflict(DbUpdateException exception) =>
    exception.InnerException is PostgresException
    {
        SqlState: PostgresErrorCodes.UniqueViolation,
        ConstraintName: "EmailIndex" or "UserNameIndex"
    };
```

### PROTECTED — duplicate result and endpoint precedence

The validator's `DuplicateEmail` and `DuplicateUserName`, and only the filtered database exception above,
map to:

```csharp
Error.Conflict("Users.DuplicateEmail", "A user with this email already exists.")
```

In the endpoint result mapper, this literal branch must precede the suffix branch:

```csharp
if (result.Error.Code == "Users.DuplicateEmail")
    return Results.Problem(result.Error.Description,
        statusCode: StatusCodes.Status409Conflict, title: result.Error.Code);
return result.Error.Code.EndsWith(".Conflict", StringComparison.Ordinal)
    ? Results.Problem(result.Error.Description,
        statusCode: StatusCodes.Status409Conflict, title: result.Error.Code)
    : Results.Problem(result.Error.Description, statusCode: 422, title: result.Error.Code);
```

### Transaction invariants

Inside one `AmbientTransaction.RunAsync` single attempt: lock the account unconditionally; verify the
locked account id; load target by `(Id, AccountId)`; re-read the actor as active Owner inside the lock;
perform trimmed ordinal no-op and sole-active-Owner self guard; snapshot all seven identity values plus
incoming entity state/property modified flags and pre-existing tracked audit instances; assign all four
email/login columns through configured normalizers; rotate the security/concurrency stamps; bump epoch
once; revoke all refresh tokens; write `AuditActions.UserEmailChanged` directly with
`new { oldEmail, newEmail }`; save and commit. Preserve `EmailConfirmed` and disabled fields.

Every failure after snapshot must restore only this user's seven values and incoming flags, and detach only
this operation's newly-added `AuditEvent`. Expected `IdentityResult` failures are restored before return;
filtered duplicate and EF concurrency failures are restored before mapping; every other exception is
restored then rethrown. Do not clear, reload, or detach the target and do not disturb caller-tracked state.

Run the focused backend and audit/validator guards from Task 1, then clean Release build. Commit exactly:

```bash
git commit -m "feat: let owners change user email addresses"
```

## Increment 2 — dedicated SPA dialog and guidance

Follow Task 2 exactly. Add all named tests first and record the focused RED. Supported locales are derived
from `web/src/i18n/index.ts` and parity-checked against the catalogs; the effective set is `en`, `es`,
`tl`. Use genuine translations.

### PROTECTED — API client

```typescript
export const changeUserEmail = (
  id: string, body: { email: string }, key?: string, stepUpToken?: string,
) => apiPut<void>(
  `/users/${id}/email`, body, key,
  stepUpToken ? { [STEP_UP_HEADER]: stepUpToken } : undefined,
);
```

### PROTECTED — field accessibility

```tsx
aria-invalid={emailFieldError !== null}
aria-describedby={emailFieldError ? emailErrorId : emailHintId}
```

Only a 409 titled `Users.DuplicateEmail` writes `emailFieldError`; generic failures remain in
`DialogError`. Capture the target id before the first await and gate every later state write on
`activeEmail.current`. Clear the password immediately before step-up. Close/target changes clear the active
target, password, field error, generic dialog attempt, and idempotency scope.

Run Task 2's focused UI/i18n/doc checks, then commit exactly:

```bash
git commit -m "feat(web): add change-email administration flow"
```

## Documentation/runtime surfaces

| Surface | Path / key | Locales | Increment | Phase-11 evidence required |
|---|---|---|---|---|
| Users dialog | `UsersPage.tsx`, new Users keys | en/es/tl | 2 | Open Change email: row identity, hint says immediate next login/no confirmation email, field-local duplicate and ARIA wiring render |
| In-app Help | `HelpPage.tsx`, `glossaryLoginEmailTerm/Def`, roles guidance | en/es/tl | 2 | Render Help in each locale and verify the translated login-email row and no-confirmation rule |
| Product glossary | `specs/product/GLOSSARY.md` Login email | English | 2 | Render/read committed Markdown; verify farm-code login, immediate replacement, session termination, no confirmation mail |
| API metadata | `UserEndpoints.cs` ChangeUserEmail summary | API | 1 | Open generated OpenAPI document and verify route, PUT, body, and summary |
| Audit UI | `User.EmailChanged` enum/catalog labels | en/es/tl | 1/2 | Render an actual audit event after a change and verify localized label and User entity type |

## Mutation checks

After both increments are green, run every mutation row in the plan. Each guard mutant must compile, carry
a temporary `// MUTANT M<n>:` marker, make its named test RED at the discriminating assertion, then be
restored and rebuilt/retested green. Run one at a time. Do not commit mutants. In particular, the tracker
restoration mutant is valid only if the same scoped `AppDbContext` performs the unrelated later save.

M1 has two layers and uses a corrected protocol established by the first empirical run. Deleting the
explicit `NormalizedUserName` assignment is a **single-layer** mutant and must stay GREEN: the required
`UpdateSecurityStampAsync` call runs Identity's update pipeline and re-normalizes `UserName`, absorbing
that deletion. Then restore it and apply combined M1c: immediately after a successful stamp rotation,
assign `user.NormalizedUserName = null; // MUTANT M1c: corrupt final normalized user name after every
normalizer layer`. `Change_WritesAllFourColumnsThroughTheConfiguredNormalizer` must go RED on the final
`NormalizedUserName` assertion. Restore and prove that named test GREEN. The original M1 GREEN is not a
survivor; it is evidence that the second layer is live. This correction supersedes the first mutation row
in the implementation plan and records the observed mechanism rather than deleting the behavior guard.

M3 is also layered. Removing either accepted constraint name while running
`ConcurrentSameFarmClaims_OneWins_AndLoserIsDuplicateEmail` is a **single-layer** mutant and must stay
GREEN: the unconditional account lock serializes the endpoint calls, so `AccountScopedUserValidator`
reports the loser before PostgreSQL's unique violation reaches the classifier. Restore between variants.
Then run each removal against the isolated-layer test
`IsUserEmailConflict_AcceptsOnlyTheTwoNamedUniqueConstraints`; each variant must go RED at the matching
positive assertion. That direct test is the guard for the exception-classifier layer. This correction
supersedes the third mutation row in the implementation plan.
Finish with:

```bash
rg -n 'MUTANT|\[DEBUG-' src tests web
```

Expected: no output. A surviving mutant is a finding: report it and stop rather than inventing a fix—the
driver's shipped-fix budget is zero.

## Full foreground gates and PR

Run Task 3 plus the two CI self-tests and both blocking vulnerability gates:

```bash
dotnet restore Cluckwork.sln --locked-mode
dotnet list package --vulnerable --include-transitive --format json --output-version 1 \
  | node .github/scripts/vuln-gate.mjs --ecosystem nuget --level high
dotnet build Cluckwork.sln --configuration Release --no-restore
dotnet test Cluckwork.sln --configuration Release --no-build --verbosity normal
tools/schema-docs/generate.sh --check
cd web
node --test ../.github/scripts/vuln-gate.test.mjs
node --test ../.github/scripts/lockfix.test.mjs
npm ci
npm audit --omit=dev --json \
  | node ../.github/scripts/vuln-gate.mjs --ecosystem npm --level high \
      --exceptions ../.github/security-exceptions.json
npm run test:coverage
npm run build
npm run verify:sw
cd ..
git diff --check
```

Push and open the draft PR exactly as Task 3 specifies. Include the two driver artifacts and this runbook
in the PR. Report branch, two commit SHAs, pushed head, PR URL/number, exact full-suite summaries, every
RED/GREEN checkpoint, every mutation result, runtime surfaces not yet verified, and any deviation. Do not
implement review fixes; return findings to the driver.
