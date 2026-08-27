# Threat model and design — #606: step-up for durable flock scope

**Status:** owner-approved design. Assessed on shipped `main` at
`f767dce0073aea17a7e4e8cd644224023d325c89` on 2026-08-26. The owner approved
a public remediation branch/PR, the next supported release, maintainer
notification through the existing issue/PR, and no credential rotation without
deployment evidence. After the adversarial design-review corrections were
folded in, the owner approved this design on 2026-08-26.

## Intent, scope, and non-goals

An interactive Owner must present one fresh, single-use step-up grant for every
flock assignment and every flock unassignment. Proof is checked in the
application handler before any target user, flock, or assignment lookup. The
SPA obtains that proof by re-confirming the signed-in Owner's current password,
clears the password from state before awaiting issuance, and spends the grant
on exactly one write.

This slice does not change role authorization, grant issuance or validation,
Worker read visibility, the meaning of zero assignments, assignment schema,
tenant filtering, audit vocabulary, or the simulation fixture. In particular,
#388 owns read-side Worker flock filtering and #320 owns future factors that
mint the same grant. The API's existing ability for an Owner to target their
own user ID is also unchanged: elevated roles are not flock-scoped, so this is
neither an elevation path nor a new self-lockout rule for #606.

## Evidence and exposure

The pre-fix feedback loop is:

```bash
dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj \
  --filter 'FullyQualifiedName=Cluckwork.Api.IntegrationTests.RoleMatrixTests.Worker_FlockScoping_FirstAssignmentNarrows_RemovalRestores' \
  --logger 'console;verbosity=normal'
```

Against real Postgres/Testcontainers, the test passed `1/1` in about 5.6
seconds on `f767dce0`: both assignment and last-unassignment completed through
the HTTP endpoints without `X-Cluckwork-Step-Up`, and the existing scope-state
assertions observed their durable effects. This is executable pre-fix evidence,
not a theoretical finding.

Reachability is limited by the authenticated, active, current-epoch Owner-only
`/api/v1/users` group and tenant resolution. Within that farm, however, a bare
Owner bearer can persistently narrow a Worker's allowed write set or widen it
by removing the last assignment. Tenant filters and the tenant-stamp
interceptor continue to prevent cross-account access. `User.FlockAssign` and
`User.FlockUnassign` audit events record the actor, but this repository contains
no production audit data, so exploitation can be neither established nor ruled
out here.

The mutation surface was introduced by `e35f22bd` before all published releases
(`v0.0.1` through `v0.0.4`), so every published version is affected.
`SECURITY.md` supports only the latest pre-1.0 release and has no backport
branches; the fix ships in the next release from `main`.

## Assets and trust boundaries

- **Assets:** a Worker's durable flock-scope rows, the authorization meaning of
  zero rows, farm production-write permissions, tenant isolation, and audit
  attribution.
- **Ordinary session proof:** an Owner access token. It proves current
  authentication and authorization, but not recent possession of the Owner's
  password.
- **Interactive elevated proof:** a short-lived, account-bound, actor-bound,
  security-stamp-bound, logout-revocable, single-use grant issued by
  `POST /api/v1/auth/step-up` and carried only in `X-Cluckwork-Step-Up`.
- **Trusted non-HTTP caller:** the explicit `seed --profile simulation`
  one-shot flow. It acts as a real Owner for audit purposes but has no
  interactive password. Its trust must be expressed by a lower-level internal
  provisioning path, never a request field, environment switch, magic actor,
  or `bypassStepUp` boolean.

## Mechanically enumerated entry points and callers

`rg` over `src/`, `tests/`, `web/`, and `tools/` found the following current
surface; generated `graphify-out/` and historical plan prose are not callers.

| Surface | Callers / readers / writers | Required change |
|---|---|---|
| `AssignFlockHandler.HandleAsync` | `UserEndpoints.AssignFlock`; `SimulationDataSeeder.RestrictOneWorkerAsync` | Make the ordinary handler fail closed on step-up. Move the seeder to an explicit lower-level provisioning path. |
| `UnassignFlockHandler.HandleAsync` | `UserEndpoints.UnassignFlock` only | Make the handler fail closed on step-up. There is no trusted unassign caller. |
| `POST /users/{id}/flock-assignments` | `web/src/api/cluckwork.ts`; seven raw mutation calls in `RoleMatrixTests` cover assignment/unassignment behavior | Bind the header and current actor; adapt every handler-reaching test caller with a fresh grant except new denial tests. |
| `DELETE /users/{id}/flock-assignments/{assignmentId}` | `web/src/api/cluckwork.ts`; `RoleMatrixTests` | Add `accountId`, acting user id, and proof to the handler interface; bind `ICurrentUser` plus the header in the endpoint; extend `apiDelete` with optional extra headers and pin that transport contract. |
| SPA assignment module | `UsersPage.tsx`; `UsersPage.test.tsx` mocks `assignFlock`, `unassignFlock`, `stepUp` | Add one dialog-local password field, generation guard, and per-action issuance/use tests. |
| Assignment reads | Owner-only list endpoint, SPA dialog, `RoleMatrixTests`, and simulation assertions | Preserve. Reads do not require step-up; #388 owns Worker read scoping. |
| Non-CI callers | `SimulationDataSeeder`; k6 and Playwright consume the seeded restricted Worker but make no assignment HTTP calls | Preserve fixture shape, actor, audit row, and worker persona contracts. |
| Product copy | `specs/product/GLOSSARY.md`; Help and Users strings in `en.ts`, `es.ts`, `tl.ts`; comments in `IStepUpGrantService`, `StepUpGrantService`, `client.ts`, and `cluckwork.ts` | Remove the stale “flock assignment is ungated” claim and describe eight gated Users actions consistently. |

The mirrored-path set difference is intentional: only assignment has a trusted
non-HTTP writer; unassignment does not. Every other discovered write reaches an
application handler and therefore has no enforcement-site gap after this
design.

## State-transition conflict table

| User action / transition | Issue requirement | Canonical repository rule | Winner |
|---|---|---|---|
| Assign first flock: `0 -> 1` | Fresh step-up; this narrows account-wide scope. | Zero rows means account-wide Worker access. | Both: preserve meaning, gate mutation. |
| Assign another flock: `N -> N+1` | Fresh step-up for every assignment, not only the first. | Avoid privilege classifiers and TOCTOU reads; #360 gates request types unconditionally. | Gate every request before lookup. |
| Remove while rows remain: `N>1 -> N-1` | Fresh step-up for every unassignment. | Audit and route-user ownership check must remain. | Gate first, then run unchanged checks. |
| Remove last flock: `1 -> 0` | Fresh step-up; this restores farm-wide scope. | Zero-row grandfathering remains the product contract. | Preserve widening semantics, gate mutation. |
| Duplicate assignment / unknown target / mismatched route pair | Invalid proof must not disclose which target exists. | Expected failures use `Result`; target mismatches are 404 only after authorization. | Step-up failure maps to the identical 403 before lookup; valid proof may then receive existing 404/409/422. |
| List assignments / open dialog | No mutation; no step-up requirement. | Reads remain Owner-only and #388 owns read scoping. | Leave ungated. |
| Simulation fixture assignment | Trusted one-shot caller has no interactive password. | Audit writers require a real actor; no request-selectable bypass. | Explicit lower-level provisioning while acting as the seeded Owner. |
| Display-name edit | Outside #606. | #360 deliberately leaves it ungated because it changes no authorization. | Leave unchanged. |

## Design options

1. **Application-handler gate plus explicit lower-level simulation
   provisioning (recommended).** Add the grant dependency to both handlers and
   validate at their first executable line. The HTTP adapters bind the header
   and actor. The simulation seeder stops calling the interactive handler and
   performs its already-known, idempotent fixture assignment through the
   repository/audit/unit-of-work layer while acting as the Owner. This matches
   #360's trusted-provisioning precedent and exposes no bypass on an interactive
   interface. The cost is a few fixture-specific mutation lines in the seeder.
2. **Endpoint filter or middleware.** Small diff, but direct handler callers
   bypass it and the handler no longer owns its security contract. Rejected by
   the acceptance criteria and the repository's handler-per-feature shape.
3. **Shared writer module beneath interactive and trusted adapters.** Centralizes
   mutation code, but adds a new public seam and registration for one trusted
   assignment caller and no trusted unassignment caller. It also makes a
   security-sensitive lower-level writer broadly injectable. Rejected as
   overbuilt unless review finds the direct provisioning path cannot preserve
   the audit contract without meaningful duplication.

## Selected module and data flow

The existing assignment handlers remain the ordinary application modules. Add
`IStepUpGrantService` and carry the nullable proof in small command records so
the handler interface is consistent with the other gated user features:

```text
AssignFlockCommand(UserId, FlockId, StepUpToken)
UnassignFlockCommand(UserId, AssignmentId, StepUpToken)

HandleAsync(command, accountId, actingUserId, ct)
  -> stepUp.ValidateAsync(accountId, actingUserId, token)
  -> on failure: return Identity.StepUpRequired immediately
  -> existing lookup / ownership / duplicate logic
  -> existing audit write and SaveChanges
```

`UserEndpoints` binds `X-Cluckwork-Step-Up`, requires resolved tenant and
current user, builds the command, and maps `Identity.StepUpRequired` to the
same 403 problem before its existing NotFound/conflict/domain mappings. Empty
`flockId` transport validation may still return 400 before the handler because
it looks up no target and discloses no target state.

Idempotency remains outside the endpoint and uses `SingleAttemptExecution`.
A request with a valid grant consumes it at handler entry, even if a later
target/domain check returns 404/409/422 without a mutation. This ordering is
load-bearing: deferring consumption would reopen replay races across stateful
work. An exact same-key replay of a completed success returns the cached response
without entering the handler or consuming another grant. A proof-validation 403
is not cached, so the same logical key can be retried with a newly issued grant.
No non-2xx response is cached: a post-validation 404/409/422 has already spent
its proof and a same-key retry re-enters the handler with a fresh proof.
The step-up header remains outside the request hash, matching every existing
gated write.

`SimulationDataSeeder.RestrictOneWorkerAsync` retains its pair existence check,
acts as `cast.Owner` (which `SeedAsync` already constructs with
`RolesOfAsync(owner)`, never a literal role set), creates the known row through
`IUserRoleAssignmentRepository`, and saves through its existing scoped
`AppDbContext`. Add the existing `IFlockRepository` and `IAuditWriter` ports to
the seeder: resolve the tenant-filtered flock name, use the known
`cast.Workers[0].Email`, and emit the exact existing `User.FlockAssign` details
shape `{ Email, Flock }`. A real-Postgres simulation test must deserialize the
audit `Details` and pin both values; actor-only coverage is insufficient. The
seeder never accepts a proof/bypass value from configuration or HTTP. No
trusted unassignment path is added.

## SPA interaction and stale-continuation rule

The flock-access dialog gets one controlled `type=password` field using the
shared “Your current password” label plus operation-specific help. Assignment
and every Remove button use the current field value for that one action. Each
action copies the value to a local, clears component state synchronously, then
awaits `stepUp`; on success it re-checks the dialog identity and sends the grant
once in the write header. A second action requires the Owner to type the
password again.

Target id alone is not a sufficient dialog identity: close and reopen the same
Worker while issuance is pending would make an old continuation look current.
The existing `activeUser` discipline therefore gains a monotonically increasing
generation. Loads, issuance completions, writes, refreshes, and error reports
may update dialog state only when both target id and generation still match.
The idempotency key is created after issuance, cleared immediately after a
confirmed write, and never reused for a different payload.

`apiPost` already accepts optional extra headers. `apiDelete` gains the same
optional final parameter; its existing callers remain source-compatible. The
assignment wrappers attach `STEP_UP_HEADER` only when a token is present, while
the server remains authoritative for missing proof.

## Named invariants and enforcement sites

The IDs below are assigned once and must not be renumbered.

- **INV-1 — assignment proof:** every ordinary
  `AssignFlockHandler.HandleAsync` validates the actor/account grant before
  `IIdentityProvider.ListUsersAsync`, `IFlockRepository.GetByIdAsync`, any
  assignment repository read/write, audit write, or save. Establish/check:
  endpoint header + current actor, then handler first-line validation. Clear:
  the shared claim-once store consumes the grant. Callers: endpoint and, before
  this change, seeder; the seeder is the only set difference and moves to the
  trusted path.
- **INV-2 — unassignment proof:** every
  `UnassignFlockHandler.HandleAsync` validates before
  `GetByIdAsync`, route-user comparison, removal, audit, or save.
  Establish/check/clear are identical to INV-1. The endpoint is the only caller;
  there is no enforcement gap and no trusted bypass.
- **INV-3 — uniform denial:** for a syntactically valid route and body, missing,
  malformed, expired, replayed,
  wrong-actor/account, stamp-revoked, and logout-revoked proofs all return the
  existing `Identity.StepUpRequired` 403 and leave assignment rows/audit rows
  unchanged. Enforcement: `IStepUpGrantService.ValidateAsync`, handler
  short-circuit, endpoint mapping. Known and unknown targets must be
  indistinguishable without valid proof. An empty `FlockId` remains a transport
  400 before the handler because it performs no target lookup and exposes no
  target state.
- **INV-4 — one proof, one handler admission:** one grant can authorize at most
  one handler admission across routes and replicas. A valid grant is consumed
  before target/domain checks and therefore remains spent when those checks
  return 404/409/422; a proof-validation failure consumes nothing. Enforcement:
  `PersistentStepUpGrantRegistry` / `IClaimOnceStore` from #338 plus handler
  validation. Idempotent replay is a cached response, not another admission.
  Non-2xx responses are never cached, so retry after a post-validation domain
  failure requires another fresh proof even when the idempotency key is reused.
- **INV-5 — explicit trusted provisioning:** simulation assignment bypasses
  the interactive handler only through seeder-owned lower-level repository,
  audit, and save calls while `CurrentUserContext` is the real Owner. Readers
  and writers: `RestrictOneWorkerAsync`, `IFlockRepository` (tenant-filtered
  name), the known `SimActor.Email`, assignment repository, `AuditWriter`, and
  `AppDbContext`; no HTTP adapter reads a bypass selector. The simulation audit
  test pins actor, target email, and flock-name detail parity.
- **INV-6 — preserved authorization data:** account query filters,
  `TenantStampInterceptor`, the unique `(UserId,FlockId)` index, mismatched
  route-user refusal, zero-row account-wide meaning, existing audit actions and
  details, and current concurrency/idempotency behavior do not change.
  Enforcement remains at the existing repository/configuration/handler sites.
- **INV-7 — password lifetime and grant transport:** the SPA reads then clears
  the Owner password before awaiting issuance, never places it in the mutation
  body, and attaches the returned grant to one `apiPost` or `apiDelete` call.
  Writers/readers: flock dialog state, `stepUp`, assignment wrappers, client
  header transport; the password has no post-await reader.
- **INV-8 — stale dialog isolation:** a continuation from a closed, displaced,
  or same-target-reopened dialog cannot issue a mutation after step-up resolves
  or write list/error state after later awaits. Enforcement: target plus
  generation checks around every await and reset in `closeAssignments`.
- **INV-9 — caller-contract preservation:** every raw handler-reaching HTTP
  caller gets a fresh grant; denial tests deliberately omit/corrupt one;
  simulation still emits the restricted Worker; k6 and Playwright require no
  request change because they consume the fixture rather than mutate it.
- **INV-10 — documentation parity:** endpoint summaries, security comments,
  Users copy, Help, glossary, and en/es/tl catalogs all state that assignment
  and unassignment require recent proof; no surviving copy calls flock-scope
  changes ungated.

## Verification and adversarial mutations

The implementation plan must order each behavior red first and name the test
that kills its mutation:

| Mutation | Required red evidence |
|---|---|
| Delete or move assignment validation below the first target lookup. | Missing-proof known/unknown assignment tests cease returning identical 403, or a row is created; named test must assert both response and row/audit absence. |
| Delete or move unassignment validation below assignment lookup. | Missing-proof known/unknown last-unassignment test returns 204/404 instead of the identical 403, or the last row disappears; named test must assert row/audit preservation. |
| Reuse one grant for assignment then unassignment, or reuse a grant after a valid-proof duplicate assignment returns 409. | Second operation is 403; the first operation's resulting scope rows remain unchanged. The duplicate case pins that post-validation failures consume while proof-validation failures do not. |
| Route the simulation seeder back through the interactive handler, drop its Owner actor, or corrupt/drop the target email or flock name from audit details. | Focused real-Postgres simulation seed/audit test fails while the trusted fixture still converges on rerun. |
| Stop clearing the flock password before the issuance await. | Deferred-issuance UI test observes the password still present. |
| Delete the post-issuance target+generation check. | Close and reopen the same Worker before resolving issuance; stale continuation calls neither assignment wrapper. |
| Omit the grant from `apiDelete`. | Client transport test and Remove UI test observe a missing `X-Cluckwork-Step-Up` header/token argument. |
| Leave one locale or glossary/help sentence on the old boundary. | Locale key-parity/semantic assertions and a mechanical stale-phrase search fail. |

Post-fix Phase 11 must rerun the original real-Postgres flow as two variants:
assignment without proof and last-unassignment without proof must both be dead,
then valid fresh grants must preserve the original state transitions. The
mandatory sibling grep repeats the handler, route, raw-URL, SPA wrapper, seeder,
k6, Playwright, Help, glossary, and all-locale inventories above.

## Scope-ownership map

| Slice | Surface owned | Order / forward compatibility |
|---|---|---|
| #338 (closed) | Shared single-use replay and durable logout epoch for the generic step-up grant. | Landed first; #606 reuses it unchanged and tests cross-route replay. |
| #360 / PR #607 (closed) | Unconditional step-up for durable user creation/reset/role/email/disable/enable actions and the SPA dialog-race pattern. | Landed first; #606 extends the same boundary and preserves its grant semantics. |
| #606 (this slice) | Interactive flock assignment/unassignment handlers, endpoints, SPA transport/dialog, trusted simulation assignment, and matching docs. | Carries the compatibility changes for current assignment callers. |
| #388 (open, owner-decided) | Worker read-side flock visibility across lists, details, reports, and exports. | Independent follow-up; #606 must preserve current reads and zero-row meaning so #388 can filter without undoing mutation proof. |
| #320 (open) | TOTP/WebAuthn factors that mint the same generic grant. | Lands later; #606 calls `stepUp`/`IStepUpGrantService`, never assumes password is the only future factor server-side. |

## Design-review claims to attack

1. Handler-first validation really precedes every existence-sensitive read on
   both mirrored mutation paths.
2. Direct seeder provisioning is narrower and safer than exposing a trusted
   method on the interactive handler, while preserving audit details and
   idempotent convergence.
3. A target-plus-generation identity closes both pre-write and post-write stale
   continuations, including close/reopen of the same Worker.
4. Extending `apiDelete` with optional headers is sufficient and does not alter
   the rest of its callers.
5. The inventories contain every caller and every stale user-facing statement.
