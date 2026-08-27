# Implementation plan — #606 step-up for durable flock scope

> **Status:** adversarially reviewed; awaiting owner implementation approval.
> Implement only after that approval. Execute red-first and keep each green
> increment independently reviewable.

**Goal:** Require a fresh, single-use step-up grant before every interactive
flock assignment and unassignment while preserving tenant isolation, audit
semantics, simulation data, idempotency, and the existing zero-assignment
authorization meaning.

**Architecture:** The HTTP adapter carries the existing step-up header and
current actor into small application commands. Both handlers validate the
grant as their first observable action, before any target lookup. The simulation
one-shot caller no longer calls the interactive handler; it provisions the same
row and audit event explicitly at the lower layer. The SPA clears the password
before awaiting grant issuance and guards continuations by dialog target plus
generation.

**No schema change:** This plan adds no entity, migration, seed migration, or
schema-doc change. It does not alter the step-up grant implementation, role
policy, Worker read filtering, or the meaning of zero assignment rows.

## Fixed contracts

The invariant identifiers are defined in `01-threat-model.md` and must not be
renumbered. The implementation must satisfy INV-1 through INV-10.

| Invariant | Plan enforcement and evidence |
|---|---|
| INV-1 | Tasks 1.1–1.4: assignment first-line validation, known/unknown/duplicate 403 guard, and ordering mutations. |
| INV-2 | Tasks 1.1–1.4: unassignment first-line validation, last-row preservation guard, and ordering mutations. |
| INV-3 | Tasks 1.1/1.3: uniform `Identity.StepUpRequired` mapping before target/domain results; syntactically invalid empty `FlockId` remains the documented 400 exception. |
| INV-4 | Tasks 1.1/1.4 and fixed contracts: cross-route replay, post-409 consumption, success-cache/non-2xx-reentry semantics. |
| INV-5 | Tasks 2.1–2.3: explicit seeder repository/audit/single-save path and focused audit/actor mutations. |
| INV-6 | Tasks 1.2–1.4/2.2: preserve filters, unique pair, route-user mismatch, zero-row meaning, audit details, and role-matrix state transitions. |
| INV-7 | Tasks 3.1–3.4: clear password before issuance await and attach one returned proof to one POST/DELETE. |
| INV-8 | Tasks 3.1/3.3/3.4: target-plus-generation guards on open, write, refresh, and error continuations, including same-target reopen tests. |
| INV-9 | Tasks 1.3/2.3/final inventory: adapt all seven raw writes, preserve trusted simulation fixture, and verify k6/Playwright are consume-only. |
| INV-10 | Tasks 4.1–4.3: endpoint/comments, Help/glossary, Users copy, all locales, semantic tests, and stale-phrase search. |

Use these exact application interfaces:

```csharp
public sealed record AssignFlockCommand(
    Guid UserId, Guid FlockId, string? StepUpToken = null);

public sealed record UnassignFlockCommand(
    Guid UserId, Guid AssignmentId, string? StepUpToken = null);

Task<Result<Guid>> AssignFlockHandler.HandleAsync(
    AssignFlockCommand command,
    Guid accountId,
    Guid actingUserId,
    CancellationToken ct);

Task<Result> UnassignFlockHandler.HandleAsync(
    UnassignFlockCommand command,
    Guid accountId,
    Guid actingUserId,
    CancellationToken ct);
```

`accountId` and `actingUserId` stay separate from the command because they are
trusted request-context inputs, not client form fields. `StepUpToken` is nullable
so the handler, rather than model binding, produces the uniform fail-closed
`Identity.StepUpRequired` result for missing proof.

The proof is consumed at handler admission. A valid proof followed by 404,
409, or 422 remains spent. A request rejected by proof validation does not
consume a grant. An idempotent replay of an already completed request returns
the middleware's cached response and does not re-enter the handler. No non-2xx
response is cached, so retry after 403/404/409/422 re-enters the handler; a
post-validation failure therefore requires a fresh proof even with the same
idempotency key.

## Increment 1 — fail-closed application and HTTP boundary

### Task 1.1: Add red integration guards for both mutation routes

**Modify:**

- `tests/Cluckwork.Api.IntegrationTests/StepUpAuthTests.cs`

Extend `SendWithStepUpAsync` to accept `object? body` and set JSON content only
when non-null, so it can exercise DELETE without inventing a body. Add focused
helpers for assignment and unassignment that always set a new idempotency key
and optionally set `AuthEndpoints.StepUpHeaderName`.

Add runtime-created Worker/flock fixtures using `SeedUserAsync`,
`SeedFlockAsync`, `FindUserAsync`, and `TestHarness.Password`. Do not add a
literal credential or a cross-test shared grant.

Add these named behaviors before changing production code:

1. `AssignFlock_MissingProof_KnownAndUnknownTargetsAreUniformAndWriteNothing`
   creates one baseline assignment with setup grant #1, records its audit/row
   counts, then sends missing-proof requests for (a) that known duplicate pair,
   (b) a known Worker and unassigned flock, and (c) unknown target IDs. All
   responses are the existing uniform 403. Assert only the baseline row/audit
   remain. This pins proof ordering before user/flock/duplicate reads and pins
   403 ahead of the endpoint's 404/409/422 mapping chain.
2. `UnassignFlock_MissingProof_KnownAndUnknownTargetsAreUniformAndPreserveLastAssignment`
   begins with one valid fixture assignment made using a dedicated setup grant
   #1, then sends missing-proof requests for the real and mismatched or unknown
   route pair. The setup grant is never reused. Both denial responses are 403.
   Assert the last assignment and its scope meaning remain, and no
   `User.FlockUnassign` audit row was written.
3. `FlockScope_FreshProofPerMutation_AssignsThenRemovesLastAssignment` obtains
   separate grants for POST and DELETE, expects 201 then 204, and proves the
   original narrow/restore transitions still work.
4. `FlockScope_OneGrantCannotAuthorizeAssignmentThenUnassignment` assigns with
   one grant and attempts to remove that row with the same grant. Expect 403 and
   assert the row remains.
5. `AssignFlock_ValidProofConflictConsumesGrant` uses a fresh proof on a
   duplicate assignment and expects 409, then reuses that proof on a different
   otherwise-valid assignment and expects 403. Assert neither later row nor
   audit event exists.

Run only the new tests and capture the expected pre-fix result: missing-proof
mutations currently succeed or disclose target state, and token-bearing calls
cannot yet meet the new contract.

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~AssignFlock_MissingProof_KnownAndUnknownTargetsAreUniformAndWriteNothing|FullyQualifiedName~UnassignFlock_MissingProof_KnownAndUnknownTargetsAreUniformAndPreserveLastAssignment|FullyQualifiedName~FlockScope_FreshProofPerMutation_AssignsThenRemovesLastAssignment|FullyQualifiedName~FlockScope_OneGrantCannotAuthorizeAssignmentThenUnassignment|FullyQualifiedName~AssignFlock_ValidProofConflictConsumesGrant' \
  --logger 'console;verbosity=normal'
```

### Task 1.2: Put proof validation first in both application handlers

**Create:**

- `src/Cluckwork.Application/Features/Users/AssignFlock/AssignFlockCommand.cs`
- `src/Cluckwork.Application/Features/Users/AssignFlock/UnassignFlockCommand.cs`

**Modify:**

- `src/Cluckwork.Application/Features/Users/AssignFlock/AssignFlockHandler.cs`

Add the two records with the fixed signatures above. Inject
`IStepUpGrantService` into both handlers. Their first awaited operation must be:

```csharp
var proof = await stepUp.ValidateAsync(
    accountId, actingUserId, command.StepUpToken, ct);
```

Return `Result.Failure<Guid>(proof.Error)` for assignment and
`Result.Failure(proof.Error)` for unassignment. Only after success may either
handler read identity, flock, or assignment repositories. Replace raw argument
reads with command properties. Preserve the unique-pair conflict, route-user
mismatch check, audit actions and detail shapes, and the existing save order.
Do not add a bypass flag or a second public handler method.

### Task 1.3: Adapt the endpoint and every raw integration caller

**Modify:**

- `src/Cluckwork.Api/Endpoints/Users/UserEndpoints.cs`
- `tests/Cluckwork.Api.IntegrationTests/RoleMatrixTests.cs`

For both endpoint methods, bind:

```csharp
[FromHeader(Name = Cluckwork.Api.Endpoints.Auth.AuthEndpoints.StepUpHeaderName)] string? stepUpToken,
ICurrentUser currentUser
```

Keep the tenant unresolved check. Require the current user to be resolved using
the same established endpoint pattern as the other step-up user operations.
Construct the relevant command and pass `tenant.AccountId` plus
`currentUser.UserId` to the handler. Map `Identity.StepUpRequired` to the same
uniform 403 used by sibling routes before NotFound/conflict/domain mapping.
Keep the empty `FlockId` validation response because it performs no target
lookup. Update OpenAPI summaries to say recent password confirmation is
required.

In `RoleMatrixTests`, add a local `StepUpAsync(HttpClient)` helper and POST/DELETE
helpers that mint a separate fresh grant for each handler-reaching write. Replace
the seven raw writes: the two adjacent duplicate-flow POSTs and last-row DELETE
in `Worker_FlockScoping_FirstAssignmentNarrows_RemovalRestores`, the draft-scope
POST, the mismatched-pair DELETE, and both tenant-isolation POSTs. Reconcile the
inventory with:

```bash
rg -n 'flock-assignments' tests/Cluckwork.Api.IntegrationTests/RoleMatrixTests.cs
```

Do not change its GET calls or the scope-state assertions. The dedicated denial
tests in `StepUpAuthTests` remain the only intentional proof omissions.

### Task 1.4: Make the backend increment green

Run the new step-up tests, then the complete role matrix and step-up suites:

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~StepUpAuthTests|FullyQualifiedName~RoleMatrixTests' \
  --logger 'console;verbosity=normal'
```

Before accepting green, perform and revert these mutations one at a time:

- move assignment validation below the target-user lookup;
- move unassignment validation below the assignment lookup;
- remove assignment validation entirely;
- remove unassignment validation entirely.
- place the assignment 403 mapping after its 422 fallthrough.

Each mutation must make at least one named denial/replay test red. Record the
test name and failure. If moving validation below a lookup does not go red, the
known/unknown assertion is insufficient and must be strengthened before work
continues.

## Increment 2 — explicit trusted simulation provisioning

### Task 2.1: Add the audit-detail regression test first

**Modify:**

- `tests/Cluckwork.Api.IntegrationTests/SimulationSeederTests.cs`

Add a real-Postgres test named
`SimulationSeed_RestrictedWorkerAssignment_PreservesActorAndAuditDetails`.
After the simulation profile runs, find the `User.FlockAssign` event for the
restricted Worker and assert:

- the entity is that Worker's user ID;
- the actor is the seeded Owner/Admin persona already used by the fixture;
- JSON details contain the Worker's known runtime fixture email;
- JSON details equal the actual restricted flock entity's name, and the fixture
  still maps that assignment to its established `Sim House A` topology;
- rerunning the seeder remains convergent and does not duplicate the assignment
  or its audit event.

The existing `SimulationSeed_AttributesEachAuditedActionToItsPersona` and
`SimulationSeed_EachActionsActorHoldsTheExpectedRoles` continue to pin actor
identity/roles; do not replace or weaken them.

Run the focused test. It may be green on shipped code because the old handler
currently supplies the details; that is an acceptable characterization test.
Its required red proof is the mutation in Task 2.3.

### Task 2.2: Remove the trusted caller from the interactive handler

**Modify:**

- `src/Cluckwork.Infrastructure/Persistence/SimulationDataSeeder.cs`

Remove `AssignFlockHandler` from the seeder constructor. Inject the existing
`IFlockRepository` and `IAuditWriter` instead; retain
`IUserRoleAssignmentRepository` and `AppDbContext`.

In `RestrictOneWorkerAsync`, preserve the existing duplicate check and known
`cast.Workers[0]` / `flockIds[0]` topology. Bind
`var worker = cast.Workers[0]`, then resolve the tenant-filtered flock
through `IFlockRepository` so its real name supplies the audit details. Create
`UserRoleAssignment` directly with the same account/user/flock IDs, add through
the repository, and write exactly:

```csharp
await audit.WriteAsync(
    AuditActions.UserFlockAssign,
    "User",
    worker.UserId,
    details: new { worker.Email, Flock = flock.Name },
    ct: ct);
```

Add the assignment and audit event to the same scoped `AppDbContext` and commit
them atomically with exactly one `SaveChangesAsync`; do not introduce an
intermediate save. Preserve the
`ActAs(cast.Owner)` scope and the fact that `cast.Owner` was constructed from
`RolesOfAsync`; do not manufacture roles, call the step-up service, or add a
request-selectable trusted mode. There is no trusted unassignment path.

### Task 2.3: Verify simulation parity and kill the audit mutations

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~SimulationSeederTests' \
  --logger 'console;verbosity=normal'
```

Perform and revert each mutation:

- route the seeder back through the interactive handler without proof;
- remove the Owner actor scope;
- replace `worker.Email` in audit details with a different value;
- replace `flock.Name` with the ID or a hardcoded name;
- omit the audit write.

The email and flock-name mutations must specifically make
`SimulationSeed_RestrictedWorkerAssignment_PreservesActorAndAuditDetails` red
through deserialized key/value assertions. Actor-scope removal may be killed by
that test or the existing actor/role tests; omission must make the focused test
red. The single-Save atomicity is verified by the implementation diff: an
ordinary successful end-state test cannot prove crash atomicity after a split
without a synthetic failpoint, so it must not be presented as such a guard. Also
run the simulation caller inventory and confirm k6/Playwright only consume the
fixture and do not call either mutation route:

```bash
rg -n 'AssignFlockHandler|UnassignFlockHandler|flock-assignments' \
  src tests web tools --glob '!**/bin/**' --glob '!**/obj/**'
```

## Increment 3 — SPA transport, password lifetime, and stale continuations

### Task 3.1: Add red client and dialog tests

**Modify:**

- `web/src/api/client.test.ts`
- `web/src/routes/UsersPage.test.tsx`

In `client.test.ts`, add a DELETE transport test that supplies both an explicit
idempotency key and `X-Cluckwork-Step-Up`, then asserts both headers arrive on
the request and no body is introduced.

In `UsersPage.test.tsx`, update existing successful assignment/unassignment
tests to fill a runtime-generated `PROOF_PASSWORD`, expect one
`stepUp(PROOF_PASSWORD)`, and expect the returned token as the wrapper's fourth
argument. Add separate deferred-issuance tests for assignment and unassignment:
immediately after clicking, the password field is empty before the deferred
step-up promise resolves; resolving it produces exactly one wrapper call.

Add close/reopen-same-Worker tests for both writes. Start issuance, close the
dialog, reopen that same Worker (new generation), then resolve the old promise.
The stale continuation must not call `assignFlock`/`unassignFlock`, refresh the
new dialog, or surface its error there. A target-ID-only guard must fail these
tests.

Add a catalog marker assertion showing `users.stepUpFlockHint` is rendered from
the active locale. Do not hardcode a real or fixed password.

Run the focused files and capture red:

```bash
cd web
npm test -- src/api/client.test.ts src/routes/UsersPage.test.tsx
```

### Task 3.2: Carry the grant through the typed client

**Modify:**

- `web/src/api/client.ts`
- `web/src/api/cluckwork.ts`

Change the delete signature without breaking existing callers:

```ts
export function apiDelete<T>(
  path: string,
  idempotencyKey?: string,
  extraHeaders?: Record<string, string>,
): Promise<T>
```

Merge `extraHeaders` with the existing generated/explicit idempotency header.
Do not let extra headers replace the key. Extend `assignFlock` and
`unassignFlock` with an optional fourth `stepUpToken` argument and attach
`STEP_UP_HEADER` exactly as sibling user wrappers do. Keep optionality at the
transport edge so denial tests and server fail-closed behavior remain possible.
Mechanically inspect all existing positional `apiDelete` callers (order-item,
logo, banner, and flock-assignment removal) and retain their two-argument
behavior; copy the existing `apiPost`/`apiPut` extra-header test pattern.

### Task 3.3: Make the dialog erase-first and generation-safe

**Modify:**

- `web/src/routes/UsersPage.tsx`
- `web/src/i18n/en.ts`
- `web/src/i18n/es.ts`
- `web/src/i18n/tl.ts`

Add one controlled password value local to the flock-access dialog. Render a
label using the existing shared current-password label and a new translated
`users.stepUpFlockHint`; use `type="password"` and
`autoComplete="current-password"`.

Replace the current target-only lifetime check with an active dialog identity
containing `{ targetId, generation }`. Increment generation for every open,
including closing and reopening the same Worker. `openAssignments` must capture
that identity and check it after its list await, before every success-state
write, and in its catch before `errors.setPage`; a displaced load failure must
not surface in the new dialog or page. `closeAssignments` must clear
the active identity, password, dialog state, and scoped errors.

For both `onAssign` and `onUnassign`:

1. capture the active dialog identity, selected action, idempotency scope, and
   password into locals;
2. clear the password state synchronously;
3. await `stepUp(password)`;
4. return unless target and generation still match;
5. call exactly one mutation wrapper with the returned token;
6. return unless target and generation still match before every list/error
   state write after subsequent awaits;
7. rotate the idempotency key only after the write and refresh complete under
   the existing contract.

Disable assignment and remove buttons when no password is present or the
relevant action is busy. Require fresh re-entry for the next action. Do not
retain the password in a ref, include it in a mutation payload, retry grant
issuance automatically, or reuse one token across assignment/unassignment.

Translate the new hint idiomatically in English, Spanish, and Tagalog. Keep the
meaning: re-enter the current password before each assignment or removal.

### Task 3.4: Make the SPA increment green and mutate it

```bash
cd web
npm test -- src/api/client.test.ts src/routes/UsersPage.test.tsx
npm run typecheck
```

Perform and revert each mutation:

- clear the password after, rather than before, awaiting issuance;
- remove the post-issuance generation check;
- compare only the Worker ID, not generation;
- omit the step-up token from `assignFlock`;
- omit the step-up token or extra headers from `apiDelete`/`unassignFlock`;
- reuse a previously returned token for a second action.

Each mutation must make a named test red. If stale completion can update a
reopened same-Worker dialog, strengthen the test before proceeding.

## Increment 4 — user-visible boundary and canonical documentation

### Task 4.1: Update semantic documentation tests first

**Modify:**

- `web/src/routes/HelpPage.test.tsx`

In the existing test
`enumerates every step-up-gated category and the explicit ungated boundary`,
change the security-boundary assertion from six to eight protected Users-screen
actions. Assert assignment and unassignment are named as requiring recent
password confirmation, and display-name editing remains the ungated contrast.
Retain the catalog-source tests for both Help prose and glossary definition.

Run the Help and catalog tests and observe the old copy fail:

```bash
cd web
npm test -- src/routes/HelpPage.test.tsx src/i18n/catalogParity.test.ts
```

### Task 4.2: Update every copy and comment reader

**Modify:**

- `web/src/i18n/en.ts`
- `web/src/i18n/es.ts`
- `web/src/i18n/tl.ts`
- `specs/product/GLOSSARY.md`
- `src/Cluckwork.Application/Common/IStepUpGrantService.cs`
- `src/Cluckwork.Infrastructure/Identity/StepUpGrantService.cs`
- `web/src/api/client.ts`
- `web/src/api/cluckwork.ts`
- `src/Cluckwork.Api/Endpoints/Users/UserEndpoints.cs`

Update Users dialog copy, Help `signingInStepUp`, and
`glossaryStepUpAuthDef` in all locales. State eight protected actions and name
flock assignment and unassignment. Remove every claim that flock-assignment
changes are ungated. Update the product glossary's Step-up authentication row
to the same boundary while preserving #320's future-factor wording.

Update code comments and endpoint summaries whose enumerated caller list would
otherwise omit these two operations. Do not copy implementation jargon such as
“grant” or “token” into user-facing prose.

### Task 4.3: Prove copy parity mechanically

```bash
cd web
npm test -- src/routes/HelpPage.test.tsx src/i18n/catalogParity.test.ts src/routes/UsersPage.test.tsx
cd ..
rg -n -i 'six actions|six categories|flock-assignment changes do not|flock assignment changes do not|assigning or unassigning flocks remain|remain \*\*ungated\*\*' \
  web specs src tests
```

The stale-phrase search must return no obsolete boundary. Inspect every hit for
`step-up`, `current password`, `flock assignment`, and `flock unassignment` in
all three catalogs and the glossary; a mechanical key-parity green alone is not
semantic translation evidence.

## Final verification and handoff

Run from the repository root with Docker available:

```bash
dotnet restore Cluckwork.sln --locked-mode
dotnet build Cluckwork.sln --configuration Release --no-restore
dotnet test Cluckwork.sln --configuration Release --no-build --verbosity normal
tools/schema-docs/generate.sh --check

cd web
npm ci
npm run test:coverage
npm run build
npm run verify:sw
cd ..
```

Repeat the original real-Postgres role-matrix feedback test explicitly. Then
run its post-fix denial variants and the valid fresh-proof transition test. The
pre-fix command must no longer demonstrate a bare mutation because its raw
callers now mint grants; the dedicated missing-proof tests are the canonical
post-fix exploit guards.

Repeat the caller inventory and reconcile every hit deliberately:

```bash
rg -n 'AssignFlockHandler|UnassignFlockHandler|AssignFlockCommand|UnassignFlockCommand|flock-assignments|assignFlock|unassignFlock' \
  src tests web tools --glob '!**/bin/**' --glob '!**/obj/**'
```

Required final evidence:

- clean Release build with warnings as errors;
- full .NET suite against real Postgres where applicable;
- full frontend coverage/build/service-worker verification;
- schema-doc check unchanged and green;
- all adversarial mutations recorded red, then reverted;
- no migration or generated schema-doc diff;
- no hardcoded credential, bypass selector, provider-specific configuration, or
  assertion about production exploitation;
- Help, glossary, endpoint summaries, and en/es/tl catalogs agree;
- diff limited to #606 plus its plan/review evidence.

The implementer must commit only green increments with conventional subjects.
The final PR title is the release note and should be:

```text
fix: require step-up for flock scope changes
```

Do not merge. Return the branch, commit SHAs, PR URL, exact test output summary,
mutation ledger, and any residual risk to the driver for independent review and
owner merge approval.
