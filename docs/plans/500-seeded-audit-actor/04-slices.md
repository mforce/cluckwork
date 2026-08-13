# Vertical slices: seeded records name a real person (#500)

> **Revision 2** (2026-08-11), after the Gate 4 review found 8 defects —
> including two that made the suite red at a commit boundary. Ledger:
> [`04a-review-gates-1-2-4.md`](04a-review-gates-1-2-4.md).

Six slices, in build order. Each ends in a working, testable state and is proven
before the next starts. One PR (`fix/500-seeded-audit-actor`); the slices are
commits within it, so the AGENTS.md "docs in the same PR" rule is satisfied by
slice 6.

**Ordering constraint that drives everything:** the moment `AuditWriter` fails
closed, *every* non-HTTP audit caller must already declare an actor or the seed
verbs crash. So slice 1 makes all four callers declare one — the simplest actor
that works — and later slices refine *who* that actor is.

**The claim "no point in the sequence is knowingly red" is only worth as much as
the walk behind it.** Revision 1 asserted it on the strength of a hand-written
casualty list that turned out to be half the real one. See slice 1, task 1.

---

## Slice 1 — tracer bullet: nothing is unattributed

**Task 1, before any code: walk the test suite and produce the casualty list.**
Not from memory, not from this document. Two independent enumerations are needed
because the same hand-listing method has now failed three times on this plan:

- every scope that resolves `TenantContext` without an actor and then reaches an
  auditing handler, and
- every caller of `seed --profile demo` / `DemoDataSeeder.SeedAsync` against a
  database with no Owner.

The Gate 4 review's own walk found roughly **eight** sites where revision 1
listed four — including a sibling scope *inside* a test it had already cited,
and a `[Theory]` with 4 of 7 rows affected. Treat that as a floor, not the
answer: `CurrencyLockRaceTests:99`, `:167`, `:174`, `:243` (4 of 7 rows),
`ChangeUserRoleRaceTests:81`, `DisableUserRaceTests:82`, `:92`, `:577`, plus the
demo-prerequisite three below. Confirm each, and find any the review missed.

Then the change itself:

- `SystemActors`; `CurrentUserContext.ResolveSystemActor`.
- `AuditWriter` fails closed on an unresolved actor.
- All four non-HTTP callers declare one: `bootstrap-admin` and `recover-admin`
  declare their system actors; **both seeders resolve the account's Owner for
  everything** — no personas, no `SimCast`, no rotation, no worker logic.
- Demo gains `FindOwnerAsync` (with the `AccountId` filter and the deterministic
  choice) and its preflight.
- `TestHarness.WithTenantAndActorScopeAsync`, applied to every site task 1 found.
- The three demo-prerequisite casualties: `SeedCommandTests:71-90`, `:135-145`
  (including the comment stating the old claim), `DemoSeedTests:99`. Verified
  complete by the review; `OneShotVerbMinimalConfigTests:99` already tolerates
  exit 0 *or* 1, so `PrerequisitesMissing` does not break it.
- Tests: `AuditActorTests` (3); `DemoSeed_WithNoOwner_FailsClosed` in its **own
  factory/container**; `DemoSeed_AttributesEverySeededAuditEventToTheOwner`;
  `SimulationSeed_LeavesNoUnattributedAuditEvent`; **and the two system-actor
  assertions** — `AdminRecoveryServiceTests` (break-glass row) and
  `BootstrapAdminCommandTests` (first Owner's `User.Create`). Those belong here,
  with the feature they cover; revision 1 left them in no slice at all, so the
  only thing keeping both CLI verbs from throwing shipped untested.

**Proof:** on a **freshly migrated** database, run `bootstrap-admin`, *then*
`seed --profile demo` and `seed --profile simulation` — both profiles now
require an Owner, so without that step the verbs exit `PrerequisitesMissing`
before writing a single audit row. Then query `AuditEvents` for
`ActorEmail = '(unresolved)'` → expect **0**. The "~256" figure in the issue is
from its own probe of a database seeded by the *old* code; it is not a before
count for this run, and the design never repairs such a database. Then open the
SPA and show a History line naming a real address.

## Slice 2 — the Owner/Manager/Sales phases become real people

Everything except worker attribution, which is now slice 3 (see below).

- `SimActor`, `SimCast`; `SeedCastAsync` returns it (three of four loops stop
  being fire-and-forget and accumulate instead); `EnsureUserAsync` returns a
  `SimActor` and rebuilds `Roles` via `GetRolesAsync` on its existing-user
  branch.
- `Pick(pool, index, fallback, poolName)` with the empty-pool warning.
- Per-phase `ActAs` for the flock, settings, catalog, customer, order, expense,
  inventory, feed and water phases, with the explicit actor parameters on every
  helper. **Daily entries stay Owner-attributed** until slice 3.
- **The phase reorder**: `SeedPrimaryTimeZoneAsync` before `RestrictOneWorkerAsync`.
- Tests: `…AttributesEachAuditedActionToItsPersona` **minus its
  `DailyEntry.Create` clause**, which arrives with slice 3;
  `…WritingPersonasAuthorSomething_AndReadOnlyAuthorsNothing` for the Manager and
  Sales pools; the fixture count precondition. State the
  `Account.UpdateSettings → Owner` clause explicitly here — its validity depends
  on the reorder this same slice delivers.

**Proof:** the fixture's History lines name sales staff on orders and managers on
flocks, expenses and the catalog. Show the rows.

*Why worker attribution is not here:* the flock assignment is written before
production history, so attributing daily entries to a rotating worker pool
without `WorkerFor`'s eligibility rule necessarily selects the restricted worker
for the foreign flock — `FlockScope.NotAssigned`, `SeedAsync` returns `Failed`,
the fixture's `InitializeAsync` throws, and the whole simulation suite is red.
There is no naive interim picker to lean on: `SeedFlockHistoryAsync` has no
actor selection today at all.

## Slice 3 — workers, and the authorization rule that governs them

- `WorkerFor` with the per-flock eligibility rule, wired into
  `SeedFlockHistoryAsync`.
- `RestrictOneWorkerAsync` returns the pair, **both branches** — the idempotent
  one returns the *existing* pair.
- Tests: the `DailyEntry.Create → sim-worker-` clause deferred from slice 2;
  `…WritingPersonasAuthorSomething…` extended to the Worker pool;
  `…RestrictedWorkerAuthorsOnlyItsAssignedFlock` (both clauses);
  `…PartialRerunReconstructsWithAnEligibleWorker`.

**Proof:** the partial-rerun test passes. Separately — and this is a **mutation
observation, not a test** — breaking the eligibility rule fails the *entire*
seed with `FlockScope.NotAssigned` rather than misattributing quietly. No named
test asserts that; it is what running the mutation shows, and slice 5 records it.

## Slice 4 — both provenance shapes

- `SubmittedByManagerEvery`; the mixed submitter branch.
- Tests: `…CarriesBothProvenanceShapes`,
  `…SubmitIsEitherTheRecordingWorkerOrAManager`.

**Proof:** show one record whose History reads "created and last changed by the
same person" and one reading "created by X, last changed by Y" — the #494 shape
no fixture exercises today.

## Slice 5 — the remaining guards, and the mutation run

- `…ActorIdentityIsInternallyConsistent` (with the per-action role mapping),
  `…WorkerRotationIsExactAndDeterministic` (restricted flock identified by
  joining `UserRoleAssignments.FlockId`, per-flock eligible counts, full history).
- **Run every mutation in the table**, by the method the design mandates: apply,
  rebuild (never `--no-build`), run the full suite, record *every* red test,
  require the named one among them, restore, rebuild, confirm green.

**Proof:** the recorded run — each mutation, the tests that died, the restored
green baseline. No claim is written before its mutation has been run.

## Slice 6 — docs, and the comments that carry the false model

- `deploy/README.md:30-32` and the AGENTS.md seed bullet (root `README.md` has no
  mention of `seed --profile demo`), `docs/decisions/280-seed-and-simulation.md`,
  `docs/decisions/283-first-run-admin-provisioning.md`,
  `docs/runbooks/break-glass-account-recovery.md`.
- The operational seam Gate 1's review surfaced: seeing the demo fixture needs
  the Owner's password, which `bootstrap-admin` prints **only on first
  provisioning** — on a re-used database the route back is `recover-admin`.
- The four stale comments: `ICurrentUser.cs:3`,
  `TenantResolutionMiddleware.cs:6`, `CurrentUserContext.cs:5`, and
  `FlockScopeGuard`'s `!IsResolved` justification.
- `specs/product/GLOSSARY.md` and the SPA Help page **only if** either names the
  placeholder — to be checked, not assumed.

**Proof:** `dotnet build` clean (warnings are errors), full suite green, and a
diff review showing no comment still claims `ICurrentUser` is audit-only.

---

## Not in any slice, deliberately

- **Backfilling existing databases.** Out of scope per the issue; the fix is
  fresh-or-reset only.
- **Non-default cast configurations.** Untested by choice; the fixture's counts
  are asserted so a config change fails loudly rather than silently changing what
  every assertion means.
- **Manager/Sales rotation coverage.** Pool size is 1 at the fixture's config;
  covering it needs a second fixture that would ripple into the manifest's
  exact-count validation.
