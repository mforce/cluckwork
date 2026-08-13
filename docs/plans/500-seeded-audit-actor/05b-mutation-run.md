# The recorded mutation run (2026-08-12)

Every row of `03-program-design.md`'s mutation table, run by the method that
document mandates — because the previous method ("write the test, then claim it
is the one that dies") produced a wrong claim four times on this plan, and a
fifth time during slice 3.

**Method.** Apply the mutation → **rebuild** (never `--no-build`, which re-runs
the previous binary and yields a false green) → run the **full** solution suite →
record **every** test that goes red → require the named one to be **among** them →
restore → rebuild → confirm the baseline is green.

**Baseline after restore: 1653 passed, 0 failed** (145 application, 325 domain,
1183 integration).

Two rules the run itself taught:

- **A mutation must still compile.** The first attempt at "every entry is
  self-submitted" used `if (false)`, which is an unreachable-code *warning* — and
  warnings are errors here. The build failed, the test run silently used the
  **previous mutant's** binary, and the result looked plausible. Only checking
  the build output caught it. Replaced with `if (… == -1)`.
- **The named test is a minimum, not the whole set.** Several rows kill five or
  ten tests. Two rows kill exactly one, and that is itself the finding.

---

## Results

| # | Mutation | Red tests | Named test among them |
|---|---|---|---|
| 1 | `AuditWriter` reverted to the `(unresolved)` fallback | `AuditActorTests.WriteAsync_WithUnresolvedActor_Throws`, `…_AddsNothingToTheChangeTracker` | ✅ **exactly ×2, not ×3** — `WriteAsync_WithSystemActor_…` uses a *resolved* actor, so the old ternaries preserve both its label and `Guid.Empty` and it stays green, as the design predicted |
| 2 | remove only the actor guard, keep the tenant guard | same 2 | ✅ `…_Throws` |
| 3 | move the actor guard to **after** `AddAsync` | `…_AddsNothingToTheChangeTracker` **only** | ✅ — and the isolation is the point: querying the table instead of the change tracker would have passed, because `AuditWriter` never saves |
| 4 | stamp a valid but **wrong** `ActorUserId` beside a correct email | 10 tests, incl. `SimulationSeed_ActorIdentityIsInternallyConsistent`, `AdminRecoveryServiceTests`, `BootstrapAdminCommandTests`, `DemoSeedAttributionTests`, `SimulationPartialRerunTests` | ✅ |
| 5 | drop every `ActAs` in `SeedFlockHistoryAsync` | 6, incl. `…AttributesEachAuditedActionToItsPersona`, `…EachActionsActorHoldsTheExpectedRoles`, `…WorkerRotationIsExactAndDeterministic` | ✅ |
| 6 | `WorkerFor` ignores the flock restriction | **38 tests** — nearly the whole simulation suite | ✅ `…RestrictedWorkerAuthorsOnlyItsAssignedFlock` is among them, **and the suite-wide red is the predicted shape**: the restricted worker writing to a foreign flock returns `FlockScope.NotAssigned`, `Require` throws, and `SeedAsync` fails the entire fixture. Loud, not silent |
| 7 | `Pick` returns a constant element | `…WorkerRotationIsExactAndDeterministic`, `…WritingPersonasAuthorSomething_AndReadOnlyAuthorsNothing` | ✅ |
| 8 | `Pick` chooses randomly | `…WorkerRotationIsExactAndDeterministic` **only** | ✅ — a probabilistic kill over the full seeded history, not a certain one; stated rather than claimed away |
| 9 | drop demo's Owner preflight | `DemoSeed_WithNoOwner_FailsClosed`, `OneShotVerbMinimalConfigTests` (the `seed --profile demo` row), `SeedCommand_Demo_AgainstAnUntouchedDatabase_…` | ✅ |
| 10 | demo preflight accepts **any** user, not an Owner | `DemoSeed_WithNoOwner_FailsClosed` **only** | ✅ — which is exactly why that fixture seeds a lone **Manager** rather than no user at all |
| 11 | drop `ActAs(Owner)` before `SeedPrimaryTimeZoneAsync` | `…AttributesEachAuditedActionToItsPersona`, `…EachActionsActorHoldsTheExpectedRoles` | ✅ **valid only because of the slice-2 reorder.** While that phase followed `RestrictOneWorkerAsync`, which leaves the Owner resolved, this mutation was a **no-op** and the row pinned nothing |
| 12 | `RestrictOneWorkerAsync`'s idempotent branch returns `(Guid.Empty, Guid.Empty)` | `SimulationPartialRerunTests.…PartialRerunReconstructsWithAnEligibleWorker` **only** | ✅ **after the test was fixed** — see below |
| 13 | manager-submit branch → plain `ActAs(Owner)` | `…SubmitIsEitherTheRecordingWorkerOrAManager`, `…AttributesEachAuditedActionToItsPersona` | ✅ |
| 14 | every entry self-submitted | `…SubmitIsEitherTheRecordingWorkerOrAManager`, `…CarriesBothProvenanceShapes` | ✅ |
| 15 | every entry manager-submitted | same 2 | ✅ |
| 16 | drop `ActAs(actor)` in `EnsureConfirmedOrderAsync` | **NONE — the mutant SURVIVES** | ❌ see below |

---

## Row 12: the mutation the test was written for, and survived

`03-program-design.md` specified the partial-rerun test as *"(a) the seed
succeeds and (b) the replacement entry's actor is an eligible worker"*. Both
clauses hold under the mutation:

- The victim entry is on the **non**-restricted flock, so `WorkerFor` takes the
  filtered branch either way.
- With the bug, `RestrictedWorkerId` is `Guid.Empty`, which matches no worker —
  so the filter excludes nobody and the pool is **3** instead of 2.
- `dayIndex % 3` and `dayIndex % 2` both land on a worker that is not the
  restricted one. Just a *different* one.

Mutant applied → **survived**. The test now also asserts **which** eligible
worker the rotation selects, re-deriving the index from the durable anchor.
Mutant re-applied → red on that `Assert.Equal`, with two differing GUIDs.
Restored, rebuilt, green.

Worth noting because a reviewer reached the opposite conclusion from the same
code and called the fixed test unable to observe the bug: it missed that the
filter compares against `Guid.Empty`. The experiment settles it; the argument did
not.

## Row 16: a load-bearing line no test covers, stated rather than assumed

Deleting `ActAs(actor)` from `EnsureConfirmedOrderAsync` leaves the **entire
suite green**. The line is correct and necessary — on a re-run where a draft
order already exists, `EnsureDraftOrderAsync` returns early without acting, so
the confirmation would be authorized and audited as the inventory phase's
manager.

Covering it needs a fixture where a Draft order survives into a re-run, and the
only way to build one is to force a confirmed order back to Draft, which replays
FIFO allocation against the same lots. The line is correct and cheap; the fixture
that would prove it is neither. **The gap is recorded at the line itself**, so
the next reader does not mistake an uncovered line for a dead one.

---

## Rows added by the final review (2026-08-12)

| # | Mutation | Red tests | Verdict |
|---|---|---|---|
| 17 | disable `EnsureUserAsync`'s role-mismatch check | `SimulationReconfiguredCastTests.…FailsNamingTheRole` **only** | ✅ and the failure is instructive: without the check the seed **still fails**, but on the exact-count validation, with a message about counts and no mention of the demoted persona. The test asserts the message names the user AND the role, which is what separates "it failed" from "it said why" |
| 18 | `RolesOfAsync` returns `[]` for every persona | **39 tests** | ✅ — see below |

**Row 18 needs its provenance stated.** The final review raised it as a gap:
`SimulationSeed_EachActionsActorHoldsTheExpectedRoles` reads each actor's roles
**from the database**, not the roles the seeder passed into `CurrentUserContext`,
so a `RolesOfAsync` that returned `[]` would leave the stored roles untouched and
the test green. That reasoning was correct **about that test**.

The mutation is nonetheless killed — by row 17's new guard, not by the roles
test: an empty role list makes every non-worker persona fail its own
role-mismatch check, and the seed dies before writing anything. Recorded this way
rather than as "row 18 is covered", because *which* assertion kills a mutant is
the whole content of a mutation table. The roles test's own claim is unchanged
and still true: it pins the stored roles per action.
