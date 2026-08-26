# Driver handout — #357 change a user's email

Driver-owned working artifact. The locked design, increment plan, runbook, verification evidence,
and review history will be added only after their corresponding feature-driver gates pass.

## 1. Dispatch contract (owner-approved 2026-08-25)

- **Mode:** feature.
- **Implementer:** isolated Codex cloud subagent, `gpt-5.6-sol`; it will receive a prescriptive
  runbook and an explicit file allow-list. Rationale: the implementation needs inference across
  Identity, credential revocation, tenant isolation, a barrier-controlled race, HTTP wiring, and SPA
  state; spec cost is comparable to diff cost, so a transcriber is not the right implementation tier.
- **Reviewers:**
  - Claude Code: repository-rule compliance across the complete changed-file set; its brief will name
    `AGENTS.md`, the applicable decision records, and the final changed files, with a five-minute
    budget and a MERGE-BLOCKING/FOLLOW-UP verdict for every finding.
  - pi `llamacpp/qwen3.8-27b-q5-xl-220k-q8kv`: authentication, authorization, and tenant-isolation
    invariants in the Identity adapter, application handler, endpoint, and integration tests; five-minute
    budget and a MERGE-BLOCKING/FOLLOW-UP verdict for every finding.
  - pi `vllm/deepseek-v4-flash-0731-nvfp4-dspark-ctx262144`: concurrency/race safety and false-green
    tests, including credential-epoch, refresh-token, step-up, and mutation coverage; five-minute budget
    and a MERGE-BLOCKING/FOLLOW-UP verdict for every finding.
  - The SPA/API field-error, accessibility, help, glossary, and `en`/`es`/`tl` contract is explicitly
    included in the Claude and Qwen briefs rather than assigned an unchosen fourth backend.
- **Seat briefed on the repo's own written rules:** Claude Code; `AGENTS.md`,
  `docs/architecture.md`, `docs/decisions/269-transient-db-retry-boundary.md`,
  `docs/decisions/364-credential-epoch-revocation.md`,
  `docs/decisions/394-write-contract-callers.md`, and
  `docs/decisions/530-multi-farm-tenancy.md`, plus any additional decision record the design walk finds.
- **Background visibility:** concise progress updates in the driving conversation.
- **External review trigger:** none; these are driver-dispatched reviewers, not a PR bot.
- **Escalation:** the same increment failing twice returns to the owner for re-routing; no third attempt
  at the same tier.
- **Git/PR authority:** the implementer may create the feature branch, commit, push, and open a draft PR.
- **Lessons log:** `/home/mforce/.local/state/cluckwork-feature-driver/357-lessons.md`, deliberately
  outside the repository and excluded from reviewer artifacts.
- **Applied by, per increment:** none yet.
- **Fix budget:** 0 shipped fixes; driver-run Phase 11 mutations may be injected and reverted.
- **Direct applications spent:** 0 of 0.
- **Correctness-critical findings so far:** the pre-signoff Claude architecture pass required
  constraint-specific duplicate mapping and restoration of tracked identity fields on every
  pre-commit failure; both are design corrections, not product-code fixes.

### Design-fix ledger

| Fix ID | Round | Finding | Invariant ID + one-line statement | Enforcement sites (symbols) | Prior entries compared | Same area as / "new" + why | SHA (filled after) |
|---|---|---|---|---|---|---|---|

## 2. Review loop

Every selected reviewer returns a MERGE-BLOCKING or FOLLOW-UP verdict per finding; neither verdict is
preferred, and inflating a follow-up to be safe costs a fix round an issue would carry. A round is clean
only when all three reviewers answer on the same unchanged head. Stop after two consecutive rounds find
no product-code defect, or when all findings are regressions created by the preceding fix.

## 3. Locked design (owner-approved 2026-08-25)

### Scope and behavior

- Add `PUT /api/v1/users/{id}/email`, returning 204 on success and on a true no-op. It remains under
  the Owner-only users group, requires `Idempotency-Key`, requires an unconditional step-up grant, and
  carries a 2048-byte request-body cap.
- The request is `{ "email": "..." }`. Validation mirrors create-user's required/email/256-character
  rules. The handler trims the value before comparing or writing it.
- A trimmed value exactly equal to the stored `Email` is a true no-op: no stamp rotation, epoch bump,
  token revocation, or audit row. A case-only correction is a real change even when normalization yields
  the same indexed value.
- A disabled target may have a typo corrected and remains disabled. The authenticated actor, not the
  target, must still be an active Owner at the point of mutation.
- A self-change is allowed when another active Owner exists. A sole Owner's self-change fails with an
  actionable message that says to add a second Owner first.
- A same-farm collision returns `409 Users.DuplicateEmail`; the SPA attaches it to the email field. The
  same normalized address in another farm succeeds. A foreign user id returns 404.
- No SMTP or verification flow is added. `EmailConfirmed` is preserved. Help and glossary copy explicitly
  say that an Owner's administrative change takes effect immediately and sends no confirmation email.

### Application and HTTP shape

- Add a `ChangeUserEmailCommand`, validator, and handler beside the existing user feature slices. The
  handler consumes the step-up grant before entering Identity and passes `accountId`, target id, acting
  user id, and the trimmed address through a new narrow `IIdentityProvider.ChangeUserEmailAsync` port.
- Add the endpoint and request record to `UserEndpoints`, map validation failures with the existing
  `ValidationResponse`, map missing/stale proof and a stale actor to 403, foreign ids to 404, duplicate or
  concurrency conflicts to 409, `Users.LastOwner` to 422, and all remaining expected Identity failures
  to 422.
- Register the handler and validator in
  `src/Cluckwork.Api/Hosting/CluckworkFeatureServiceCollectionExtensions.cs`, the current registration
  seam reached by `Program.cs`. Do not broaden `UpdateUserAsync`: display-name edit remains ungated and
  has none of this operation's credential semantics.

### Identity transaction and concurrency contract

`IdentityProvider.ChangeUserEmailAsync` uses `AmbientTransaction.RunAsync` and this order:

1. Take the account row lock unconditionally and verify it matches `accountId`.
2. Load the target by `(userId, accountId)` and re-read the actor as an active Owner inside the lock.
3. Return the exact-value no-op before any mutation.
4. For a self-target, use `CountOtherActiveOwnersAsync`; zero returns `Users.LastOwner` with “add a second
   Owner first” wording.
5. Snapshot every user scalar this operation can touch (`Email`, `NormalizedEmail`, `UserName`,
   `NormalizedUserName`, `SecurityStamp`, `ConcurrencyStamp`, and `CredentialEpoch`), together with the
   entry's incoming state/modified flags. Then assign the four email/login fields together using
   Identity's configured normalizer. Preserve `EmailConfirmed`, role, disabled state, password, and every
   other profile field.
6. Rotate `SecurityStamp` through `UserManager.UpdateSecurityStampAsync`. This runs the configured
   `AccountScopedUserValidator`, invalidates outstanding step-up grants, and rotates Identity's
   concurrency stamp. Do not add a uniqueness pre-read.
7. On a real change, increment `CredentialEpoch`, revoke every active refresh token, and append
   `AuditActions.UserEmailChanged` with `{ oldEmail, newEmail }` details before the final save and commit.

An Identity result containing `DuplicateEmail` or `DuplicateUserName` maps to the one public
`Users.DuplicateEmail` conflict. A database race maps the same way only when the inner
`PostgresException` is SQLSTATE `23505` and its `ConstraintName` is exactly `EmailIndex` or
`UserNameIndex`; every other `DbUpdateException` propagates. Concurrency failures map to
`Users.Conflict`. Restore every snapshotted value and its incoming tracking state on every pre-commit
error return or caught failure, and detach only the audit row this operation added if the final save
failed; transaction rollback alone does not rewind EF's in-memory entity for a longer-lived non-HTTP
caller. Do not clear the whole change tracker or disturb entries that belonged to the caller.

There is no inner retry and no durability probe: the request-wide idempotency transaction already runs
under `SingleAttemptExecution`. The account lock order matches role/disable/enable and makes the
last-active-Owner decision race-safe.

### SPA and documentation

- Add `changeUserEmail` to the API client and a dedicated row action/dialog to `UsersPage`; do not fold
  identity changes into the display-name dialog.
- The dialog identifies the target, explains that the new address becomes the login immediately and no
  confirmation email is sent, collects the new email and the acting Owner's current password, and uses
  the existing `Dialog`, `BusyButton`, `useDialogErrors`, `usePendingAction`, target-ref, step-up, and
  idempotency-key patterns. Clear the password from state immediately after sending it for step-up and on
  close/logout.
- Render `Users.DuplicateEmail` through dialog-local `emailFieldError` state, not `useDialogErrors` (whose
  contract is page/dialog strings only). Bind the input with `aria-invalid` and described error text;
  clear the field error on a new attempt, input change, close, and target change, while `activeEmail`
  prevents a late response from attaching to a reopened dialog. Other failures remain dialog errors. On
  a self-change, the next authenticated request intentionally follows the existing
  credentials-superseded sign-in path; no replacement tokens are minted.
- Add complete `en`, `es`, and `tl` catalog entries, Help-page guidance, the in-app glossary entry, and
  the corresponding product definition in `specs/product/GLOSSARY.md`.

### Pre-signoff architecture review

Claude Code (`claude-sonnet-5`) returned two merge-blocking design findings after reading the
relevant Identity and persistence seams:

1. A concurrent duplicate must be mapped to `Users.DuplicateEmail` only when the inner PostgreSQL
   exception is SQLSTATE `23505` and names `EmailIndex` or `UserNameIndex`; a blanket
   `DbUpdateException` catch would misreport unrelated database failures as an email conflict.
2. Before mutating the four identity fields, snapshot `Email`, `NormalizedEmail`, `UserName`, and
   `NormalizedUserName`; restore them on every early-return or caught-failure path. Rolling back a
   database transaction does not repair EF's tracked in-memory entity in longer-lived non-HTTP callers.

Two non-blocking decisions are explicit in the revised design: preserve `EmailConfirmed` because the
product has no verification flow, and let a self-change invalidate the current session through the
existing credential-superseded UX rather than minting replacement credentials.

DeepSeek V4 Flash approved the locked design with two test-strengthening follow-ups, both incorporated:
the tracker-restoration test must reuse the same `AppDbContext` and perform a later save, and the refresh
race must present the late child after the email-change commit and assert 401 rather than merely inspect
revocation rows. The first Qwen design-review attempt reached its five-minute cap without a verdict; its
single permitted retry completed through a Herdr wrapper and approved with two follow-ups, also
incorporated: explicitly map `Users.LastOwner` to 422 and use dedicated accessible field-error state for
the SPA duplicate conflict.

## 4. Increment plan

Full checkbox plan: [`docs/superpowers/plans/2026-08-25-change-user-email.md`](../../superpowers/plans/2026-08-25-change-user-email.md).

1. **Baseline and branch:** record backend/frontend baseline on the untouched base, verify create targets,
   and create `feat/change-user-email` only after owner plan signoff.
2. **Backend vertical slice:** write the HTTP/Identity/race tests red, then add the command, validator,
   handler, narrow Identity port, account-locked mutation, exact duplicate/concurrency mapping, audit
   vocabulary/readers, endpoint, body cap, and DI registrations; commit one independently green backend
   unit because the port/provider/endpoint cannot compile honestly as separate partial commits.
3. **SPA + documentation slice:** write Users/Help tests red, then add the client call, dedicated accessible
   dialog, field-specific duplicate handling, late-response protection, `en`/`es`/`tl`, Help/in-app
   glossary, and product glossary; commit one independently green user-facing unit.
4. **Verification and draft PR:** run CI-equivalent backend/frontend gates in the foreground, verify no
   migration/schema or mutation debris, push, and open a draft PR before review. The driver—not the
   implementer—then performs the recorded mutation matrix and independent acceptance verification.

### Increment-plan review disposition

- Qwen found one accepted blocker: the duplicate classifier was specified `private` while its named
  precedent-style test requires the Infrastructure assembly's existing `internal` test seam. The plan now
  specifies `internal static`, matching `AccountProvisioner.IsSlugConflict`.
- Qwen's claimed `ApplicationUser` global-filter blocker was rejected: `ApplicationUser` is deliberately
  filter-free in `AppDbContext`. The plan nevertheless names `WithTenantScopeAsync` plus an explicit
  `(Id, AccountId)` predicate so the test cannot drift into `IgnoreQueryFilters` or an unscoped lookup.
- Qwen's stamp-snapshot ordering concern was already satisfied by the locked sequence; the plan now says
  “before any assignment and before `UpdateSecurityStampAsync`” so it cannot be misread. Its 413,
  audit-registry, active-target, and normalizer suggestions were accepted as clarity improvements.
- Claude reached its eight-turn limit with no result; DeepSeek reached the five-minute cap with no output.
  Neither silence is counted as approval.
- Qwen's focused confirmation approved the corrected plan. Its only note was the mechanical registry-name
  typo `AUDIT_ACTION_ENTITY_TYPES`; the plan now names the real singular `AUDIT_ACTION_ENTITY_TYPE`.
- The user-authorized fresh Claude retry approved the corrected plan with no blockers. Its one follow-up
  is incorporated: the endpoint must match `Users.DuplicateEmail` literally for 409 because the code does
  not satisfy the siblings' `.Conflict` suffix test.

## 5. Standing repo rules that bite here

- `AccountScopedUserValidator` remains the sole friendly uniqueness check; the two composite database
  indexes remain the race authority.
- `AuditActions.UserEmailChanged` must be passed directly at the `IAuditWriter.WriteAsync` call site, and
  every reader of the audit-action registry (including `web/src/i18n/enums.ts` and its parity guards)
  must be updated.
- Adding the handler/validator updates `CluckworkFeatureServiceCollectionExtensions.cs` registrations
  and their registration guards.
- The endpoint must use the existing body-cap syntax before tests are written, because the guard inspects
  call-site syntax. Its write contract requires review of API client and non-CI callers; no seeder or k6
  caller currently invokes an email-change route.
- This changes user-visible behavior, so the Help page, in-app glossary/locales, and
  `specs/product/GLOSSARY.md` move in the same PR.
- No migration or generated schema-doc change is expected: all credential and uniqueness storage shipped
  in #356/#532.

## 6. Acceptance criteria

1. A valid Owner with fresh step-up changes any same-account user's login email and receives 204.
2. All four identity columns contain the configured-normalizer result; a case-only edit is not treated as
   a no-op, while an exact trimmed match has no credential or audit side effect.
3. A cross-account normalized duplicate succeeds; a same-account duplicate returns
   `409 Users.DuplicateEmail`, including when two writes race at the unique index.
4. The target's pre-change access token fails on its next request, all old refresh tokens are unusable,
   and an in-flight refresh cannot leave a usable child. Outstanding step-up grants are invalidated.
5. The new address can log in, the old address cannot, and the old address is released for another user
   in the same farm.
6. A sole Owner cannot change their own address and the response says to add a second Owner first; with a
   second active Owner, the self-change succeeds and follows the existing signed-out UX.
7. A foreign target id returns 404. A disabled target can be corrected without becoming enabled.
8. The audit event contains old and new addresses and occurs only for a real successful change.
9. The dedicated accessible SPA dialog performs step-up, clears password state, updates the row for a
   non-self target, localizes all copy in `en`/`es`/`tl`, and binds duplicate conflict copy to the email
   field.
10. Help and both glossaries state that no confirmation email is sent. Relevant backend, integration,
    frontend, catalog-parity, typecheck, and build tests pass.
11. A friendly duplicate failure followed by an unrelated save on the same `AppDbContext` does not flush
    the rejected email/user-name values or rotate either stamp. A final concurrency failure leaves neither
    a changed epoch nor this operation's pending audit entry in the tracker.
12. The barrier-controlled refresh race presents the child minted on the far side of the change commit
    and receives 401; inspecting `RevokedAt` alone is insufficient evidence.

## 7. Merge evidence

| Slot | Value |
|---|---|
| PR number + head SHA | pending |
| Driver-verified | pending |
| Implementer-attested, NOT driver-verified | pending |
| Reviewers run / rounds / stop-rule count | pending |
| Review threads: all dispositioned? | pending |
| Reviewers that never answered, and why | pending |
| Deferred findings + issue numbers | pending |
| Documentation surfaces rendered — locales + SHA | pending |
| Acceptance criteria: delivered or filed | pending |
| Retrospective | pending |
| Applied by, per increment + budget spent | pending |

## 8. First moves

Pending the locked design and increment plan.
