# Implementer runbook — #606 step-up for durable flock scope

> **Owner-approved:** 2026-08-26. Execute this runbook on branch
> `feat/606-step-up-flock-scope` from GitHub `main` at the dispatch SHA. Do not
> merge the pull request. The driver owns review and the final merge gate.

## 0. Read, preflight, and stop conditions

Read completely before editing:

1. repository `AGENTS.md`;
2. `docs/plans/606-step-up-flock-scope/01-threat-model.md`;
3. `docs/plans/606-step-up-flock-scope/04-implementation-plan.md`;
4. `docs/plans/606-step-up-flock-scope/05-plan-review.md`;
5. `docs/decisions/269-transient-db-retry-boundary.md` for the idempotency and
   unreplayable-work boundary;
6. the current files named by each increment below before authoring tests.

Hard stops:

- Do not change INV-1 through INV-10 or their meaning.
- Trace every increment back to the approved set: INV-1 assignment proof,
  INV-2 unassignment proof, INV-3 uniform denial, INV-4 one-proof admission,
  INV-5 trusted provisioning, INV-6 preserved authorization data, INV-7
  password lifetime/transport, INV-8 stale-dialog isolation, INV-9 caller
  preservation, and INV-10 documentation parity.
- Do not add an HTTP/configuration/request bypass, trusted boolean, magic actor,
  migration, schema change, new step-up mechanism, or new audit action.
- Do not alter Worker read visibility, zero-row account-wide semantics, tenant
  filters, the role policy, #338 claim-once behavior, or #320 future factors.
- Do not weaken an existing test to obtain green. If a named red test cannot be
  made meaningful, message the driver before changing the contract.
- Do not claim crash atomicity from a success-only test. Preserve one scoped EF
  context and exactly one save for simulation assignment plus audit.
- Do not use a literal credential. Use `TestHarness.Password` and the existing
  runtime-generated frontend proof password.
- Do not merge, force-push, rewrite another commit, or touch unrelated files.

At start, report through agmsg:

```bash
bash /home/mforce/.agents/skills/agmsg/scripts/send.sh \
  cluckwork-606 cw606-implementer cw606-driver \
  "Starting #606 at $(git rev-parse HEAD); read runbook and invariants."
```

Confirm the tree contains only the approved plan artifacts before editing:

```bash
git status --short
git rev-parse HEAD
git branch --show-current
docker version --format '{{.Server.Version}}'
```

## 1. Backend denial guards, commands, handlers, and endpoints

### 1.1 Write the integration guards first

Modify `tests/Cluckwork.Api.IntegrationTests/StepUpAuthTests.cs` exactly as Task
1.1 of the implementation plan specifies:

- allow `SendWithStepUpAsync` to omit JSON content for DELETE;
- add assignment/unassignment helpers with a new idempotency key per request;
- create runtime Worker/flock fixtures;
- add the five named behaviors from Task 1.1;
- ensure the assignment denial test includes known duplicate, known unassigned,
  and unknown targets and expects 403 for all three;
- ensure setup uses a dedicated grant and baseline row/audit counts;
- ensure the last-unassignment denial test preserves the only row;
- ensure the valid-proof duplicate 409 spends its proof.

Run the exact focused filter before production edits and save the failing output
for the PR ledger:

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~AssignFlock_MissingProof_KnownAndUnknownTargetsAreUniformAndWriteNothing|FullyQualifiedName~UnassignFlock_MissingProof_KnownAndUnknownTargetsAreUniformAndPreserveLastAssignment|FullyQualifiedName~FlockScope_FreshProofPerMutation_AssignsThenRemovesLastAssignment|FullyQualifiedName~FlockScope_OneGrantCannotAuthorizeAssignmentThenUnassignment|FullyQualifiedName~AssignFlock_ValidProofConflictConsumesGrant' \
  --logger 'console;verbosity=normal'
```

The expected red is behavior mismatch against the ungated shipped routes, not a
compile error caused by incomplete test helpers. Fix test compilation first if
needed, then capture behavioral red.

### 1.2 Implement the application gate

Create:

- `src/Cluckwork.Application/Features/Users/AssignFlock/AssignFlockCommand.cs`
- `src/Cluckwork.Application/Features/Users/AssignFlock/UnassignFlockCommand.cs`

Use the exact records and handler signatures from `04-implementation-plan.md`.
Modify `AssignFlockHandler.cs`, which contains both handler classes. Inject
`IStepUpGrantService` into each. `ValidateAsync(accountId, actingUserId,
command.StepUpToken, ct)` must be the first awaited operation and must return
its failure before identity, flock, assignment, audit, or save access.

Preserve the existing duplicate conflict, mismatched route-user refusal, audit
actions/details, and save ordering. Do not add a second handler overload for the
seeder.

### 1.3 Implement the HTTP adapter and adapt all seven raw writes

Modify `src/Cluckwork.Api/Endpoints/Users/UserEndpoints.cs`:

- bind the fully qualified `AuthEndpoints.StepUpHeaderName` header;
- require both tenant and current user resolved;
- pass command, account ID, and acting user ID;
- map `StepUpErrorCodes.Required` to 403 before NotFound, 409, and 422;
- preserve empty-`FlockId` 400 before the handler;
- update endpoint summaries.

Modify `RoleMatrixTests.cs` by adding fresh-grant POST/DELETE helpers and adapt
the seven writes only: two duplicate-flow POSTs, the last-row DELETE, the draft-
scope POST, mismatched-pair DELETE, and two tenant-isolation POSTs. Leave all
GETs and scope assertions intact.

Run green:

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~StepUpAuthTests|FullyQualifiedName~RoleMatrixTests' \
  --logger 'console;verbosity=normal'
```

### 1.4 Run and record backend mutations

Apply one mutation at a time, run the narrowest named killer, record the red
test/failure, and revert only that mutation:

1. move assignment validation below target-user lookup;
2. move assignment validation below the duplicate check;
3. move unassignment validation below assignment lookup;
4. remove assignment validation;
5. remove unassignment validation;
6. route proof failure into the assignment 422 fallthrough;
7. allow a grant spent by duplicate 409 to succeed on a second assignment.

After reverting all mutations, rerun the green filter above and inspect
`git diff --check`.

Commit this green increment:

```text
fix: gate flock scope handlers with step-up
```

Send the commit SHA and test/mutation counts to `cw606-driver` through agmsg.

## 2. Trusted simulation provisioning and audit parity

### 2.1 Add the focused real-Postgres characterization guard

Modify `SimulationSeederTests.cs`. Add
`SimulationSeed_RestrictedWorkerAssignment_PreservesActorAndAuditDetails`.
Deserialize `AuditEvent.Details` and assert exact `Email` and `Flock` keys and
values. Resolve the restricted assignment and its actual flock entity, compare
the audit value to that name, and separately pin the established `Sim House A`
fixture mapping. Assert Owner actor, target entity, one row/event, and convergent
rerun. Keep the existing actor and actor-role tests unchanged.

Run the new test against the current handler-backed seeder. It may be green as
a characterization test; do not manufacture a pre-change red.

### 2.2 Replace only the trusted caller

Modify `SimulationDataSeeder.cs`:

- remove `AssignFlockHandler` injection;
- inject `IFlockRepository` and `IAuditWriter`;
- retain assignment repository and `AppDbContext`;
- bind `var worker = cast.Workers[0]` and `var flockId = flockIds[0]`;
- preserve duplicate early return and `ActAs(cast.Owner)`;
- resolve the tenant-filtered flock entity;
- create/add the same `UserRoleAssignment` directly;
- write `User.FlockAssign` for entity `worker.UserId` with exact details
  `{ worker.Email, Flock = flock.Name }`;
- commit the row and event through the same scoped context with exactly one
  `SaveChangesAsync`.

No trusted unassignment path is allowed.

Run:

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~SimulationSeederTests' \
  --logger 'console;verbosity=normal'
```

### 2.3 Run and record simulation mutations

One at a time, require red, then revert:

1. route seeder assignment back through the interactive handler without proof;
2. remove `ActAs(cast.Owner)`;
3. change audit `Email` to a different value;
4. change audit `Flock` to an ID or hardcoded wrong value;
5. omit the audit write.

Mutations 3–5 must be killed by the focused details test; mutation 2 may also be
killed by existing actor/role tests. Do not claim an ordinary success test
proves split-save crash behavior. Confirm the final diff has one save.

Run the caller inventory and explicitly report every hit:

```bash
rg -n 'AssignFlockHandler|UnassignFlockHandler|flock-assignments' \
  src tests web tools --glob '!**/bin/**' --glob '!**/obj/**'
```

Commit this green increment:

```text
fix: preserve trusted simulation flock setup
```

Send the commit SHA and evidence to the driver.

## 3. SPA grant transport and dialog lifetime

### 3.1 Add frontend red tests

Modify `web/src/api/client.test.ts` and `web/src/routes/UsersPage.test.tsx` as
Task 3.1 specifies. Add the DELETE header compatibility test, update the success
tests to use `PROOF_PASSWORD` and the returned token, add deferred erase-before-
await tests for both actions, and add same-Worker close/reopen generation tests
for both assignment and unassignment. Add the catalog marker assertion for
`users.stepUpFlockHint`.

Capture behavioral red:

```bash
cd web
npm test -- src/api/client.test.ts src/routes/UsersPage.test.tsx
cd ..
```

### 3.2 Implement transport and dialog behavior

Modify `client.ts` so `apiDelete` accepts optional third `extraHeaders` and
merges it without allowing replacement of `Idempotency-Key`. Verify existing
order-item, logo, banner, and flock-removal callers retain positional behavior.

Modify `cluckwork.ts` so assignment and unassignment accept optional fourth
`stepUpToken` and attach `STEP_UP_HEADER` through POST/DELETE respectively.

Modify `UsersPage.tsx`:

- add a controlled flock-dialog current-password field;
- clear it synchronously before awaiting `stepUp`;
- require fresh entry for each assign/remove action;
- use `{ targetId, generation }` for every dialog open;
- gate `openAssignments` success writes and catch error writes by that identity;
- gate post-issuance mutation, refresh, and error continuations likewise;
- send the returned token to exactly one mutation wrapper;
- preserve existing busy scopes and idempotency-key rotation behavior;
- clear active identity, password, state, and scoped errors on close.

Add `stepUpFlockHint` idiomatically to `en.ts`, `es.ts`, and `tl.ts`.

Run:

```bash
cd web
npm test -- src/api/client.test.ts src/routes/UsersPage.test.tsx
npm run typecheck
cd ..
```

### 3.3 Run and record frontend mutations

One at a time, require red, then revert:

1. clear the password after issuance await;
2. omit assignment token transport;
3. omit DELETE/unassignment token transport;
4. remove the post-issuance generation check;
5. compare Worker ID only, allowing same-target reopen;
6. allow a displaced `openAssignments` catch to write its error;
7. reuse a previously returned proof for a second action.

Rerun the focused tests and typecheck after all reverts.

Commit:

```text
fix: confirm password for flock scope changes
```

Send the commit SHA and evidence to the driver.

## 4. Help, glossary, locale semantics, and comments

### 4.1 Make the existing Help semantic test red

Modify the test named
`enumerates every step-up-gated category and the explicit ungated boundary` in
`HelpPage.test.tsx`: require eight actions; name flock assignment and
unassignment; retain display-name editing as the ungated contrast. Run it before
copy edits and capture red.

### 4.2 Update all readers

Modify exactly the documentation/comment files listed in Task 4.2 of the plan.
All three catalogs must say eight actions and name both new actions. Remove the
old claim that flock-scope changes are ungated. Update the product glossary,
endpoint summaries, `IStepUpGrantService`/`StepUpGrantService` comments, and
client wrapper comments. Preserve #320 wording and keep implementation jargon
out of user-facing prose.

Run:

```bash
cd web
npm test -- src/routes/HelpPage.test.tsx src/i18n/catalogParity.test.ts src/routes/UsersPage.test.tsx
cd ..
rg -n -i 'six actions|six categories|flock-assignment changes do not|flock assignment changes do not|assigning or unassigning flocks remain|remain \*\*ungated\*\*' \
  web specs src tests
```

The stale search must produce no obsolete statement. Inspect semantic content,
not only catalog key parity.

Commit:

```text
docs: document step-up for flock scope changes
```

Send the commit SHA and evidence to the driver.

## 5. Full verification, PR, and handoff

Confirm every mutation is reverted and the diff is scoped:

```bash
git status --short
git diff --check
git diff --stat origin/main...HEAD
git diff --name-only origin/main...HEAD
```

Run the full local CI-equivalent sequence:

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

Repeat the focused backend, simulation, and frontend suites after the full run
if any full-run tooling rewrites generated/cache state. Confirm there is no
migration or schema-doc diff.

Mechanically reconcile final callers:

```bash
rg -n 'AssignFlockHandler|UnassignFlockHandler|AssignFlockCommand|UnassignFlockCommand|flock-assignments|assignFlock|unassignFlock' \
  src tests web tools --glob '!**/bin/**' --glob '!**/obj/**'
```

Push the approved branch and open a PR with title:

```text
fix: require step-up for flock scope changes
```

The PR body must contain:

- `Closes #606`;
- concise security boundary and non-goals without claiming production exploit;
- red-first test names and before/after outcomes;
- exact suite commands/results;
- a mutation ledger with every mutation, killer test, red result, and reverted
  confirmation;
- the seven raw-write caller inventory and k6/Playwright consume-only result;
- statement that there is no migration/schema change, bypass selector, or
  credential rotation recommendation;
- residual risk: repo data cannot establish or rule out production use;
- reviewer note that merge remains owner-gated.

Finally use `agmsg/scripts/send.sh` to send `cw606-driver` one message containing
the actual PR URL, actual HEAD SHA, full-suite result/counts, mutation pass count,
and residual-risk summary. Do not send template markers in place of evidence.

Return the PR URL, branch, commit list, changed-file list, exact verification
summary, mutation ledger, and any deviation from this runbook. Do not merge.
