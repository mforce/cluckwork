# Gate 3 review round 2 — findings and dispositions

Round 2 reviewed **revision 2**, with every reviewer handed
[`03a-review-round-1.md`](03a-review-round-1.md) and told those 15 defects were
closed, so they hunted only new ground and fix-induced breakage. Two of the four
delivered (a `pi` contrarian pre-mortem and a Claude Sonnet test-plan agent); the
codex pass and a second Sonnet implementability agent were killed by a process
exit mid-run and are **re-run against revision 3**, not against revision 2.

## The method defect — this one matters more than any single finding

Round 1 finding #15 caught one mutation-table row that undercounted which tests
would die. Round 2 found **three more** (rows for "remove only the actor guard",
"`Pick` returns a constant", and "drop every `ActAs` in `SeedFlockHistoryAsync`").

Four misses of one shape means the method is wrong, not the list — AGENTS.md's
own rule, and the repo has been bitten by exactly this twice before. The method
was: *write the test, then claim it is the one that dies.* Revision 3 replaces
it: **run the mutation, record every test that goes red, and require the named
assertion to be among them.** The table's named test is now explicitly a
*minimum*, not the complete set, and no row may be written before its mutation
has been run.

## Confirmed and fixed in revision 3

| # | Finding | Found by | Disposition |
|---|---|---|---|
| 1 | **The delta-snapshot fix is unimplementable for the simulation suite — and unnecessary there.** `SimulationSeedFactory` seeds inside `InitializeAsync`, before any `[Fact]` runs, so no test body can observe a "before" state. It is also moot: that fixture owns its own Postgres container, and `TestHarness.SeedUserAsync` creates the Owner via raw `UserManager.CreateAsync`, bypassing `IdentityProvider` — so it writes **no** audit row, and every `AuditEvent` in the account came from the seeder. | test agent | Delta **dropped for simulation**, with that reasoning recorded so it is not re-added. **Kept for demo**, where `DemoSeedTests` calls `SeedAsync` inside the `[Fact]` body (a snapshot is possible) and shares a container with other tests that touch `SeedDefaults.AccountId`. |
| 2 | `WorkerRotationIsExactAndDeterministic` kills a **random** `Pick` only probabilistically. Over "a full cycle plus one" — 4 days against a 3-worker pool — a random pick matches by luck with p = (1/3)⁴ ≈ 1.2%. The mutation table claimed a certain red. | contrarian | Assert across the **full seeded history**, not one cycle. The residual is stated as a number rather than claimed away. |
| 3 | The rotation test's "Workers pool (3 by default)" premise is **flock-dependent**: eligibility is `Workers` (3) on the restricted flock and `Workers minus the restricted one` (2) elsewhere. The plan never said which flock the test uses or how it finds it — and a list-index approach depends on unordered Postgres return. | test agent | The test pins the restricted flock by joining `UserRoleAssignments.FlockId` (the pattern the existing `SimulationSeed_RestrictsExactlyOneWorkerToOneOfTwoFlocks` already uses) and states the expected eligible count per flock. |
| 4 | **The manager-submitted branch is pinned by nothing.** Replacing `ActAs(Pick(Managers, d, Owner))` with plain `ActAs(Owner)` still satisfies `CarriesBothProvenanceShapes`' "the actors differ" clause, and no other assertion looks at the submitter's identity. | test agent | New clause: a `DailyEntry.Submit` actor is either the recording worker or a `sim-manager-` address, **never** the Owner; and at least one manager-submitted entry exists. |
| 5 | `AttributesEachAuditedActionToItsPersona` fails a **correct** implementation at `Simulation:Workers=0` — a valid config, since nothing validates positive counts — because `WorkerFor` then correctly falls back to a Manager. | contrarian | Assertions are explicitly scoped to the fixture's configured cast, and the fixture's counts are themselves asserted, so a config change fails loudly instead of silently changing what the test means. |
| 6 | The `Pick(..., Owner)` fallback **degrades silently**, and the Owner is the least faithful attribution possible for a fixture whose stated purpose is fidelity. | contrarian | `Pick` stays total (a crash is worse), but the seeder logs a warning naming the degradation when a pool is empty — AGENTS.md's "no silent caps". |
| 7 | `SalesOrder.* → sim-sales-` does not say whether it is per-action `Assert.All` or one existence check across the family. If the latter, a bug stamping `Create` correctly and `AddItem`/`Confirm` from a stale actor passes. Same vacuity class as the `Payment.*` assertion round 1 deleted. | test agent | Every persona assertion is per-action `Assert.All` over every row of that action. |
| 8 | `RestrictedWorkerAuthorsOnlyItsAssignedFlock` has no positive clause, so an implementation where the restricted worker authors **nothing at all** satisfies it vacuously — which is exactly what happens at `Workers=1`. | test agent, contrarian | Positive clause added: the restricted worker authors at least one entry **on** its assigned flock. |
| 9 | The guard's exception message tells the caller to use `CurrentUserContext.ResolveSystemActor`, but `AuditWriter` holds only `ICurrentUser`, which does not expose it — a half-loud failure in the one place this design exists to make loud. | contrarian | Message rewritten to name what the caller can act on. |
| 10 | The plan names `ActorId`; the domain property is **`ActorUserId`** (`Domain/Auditing/AuditEvent.cs:15`). | codex (from its partial transcript) | Corrected throughout. |
| 11 | "Every seed assertion runs against a delta" overstates its own scope — the surrounding untouched count assertions in the same file use no delta and need none. | test agent | Scoped to the new audit-actor assertions. |
| 12 | Nothing states that `FindOwnerAsync` and `MissingBaseDataAsync` must agree on `IgnoreQueryFilters`; if they disagree the preflight and the lookup can disagree about whether an Owner exists. | contrarian (speculative) | Stated explicitly. |

## Recorded as accepted limitations, not defects

- **"Every writing persona authors something" is near-vacuous at defaults** for
  Managers and Sales (pool size 1, so the single persona trivially authors
  everything of its kind). The clause carrying real weight is "ReadOnly authors
  nothing". Named here so a later reviewer does not re-derive it as new.
- **The submitter ratio** (`SubmittedByManagerEvery = 3`) is pinned only in the
  sense that both shapes must occur; no test fails if it becomes 1:1.
- **`RestrictedWorkerAuthorsOnlyItsAssignedFlock` is meaningful only at
  `Workers > 1`**, and the suite runs at the default 3.
- Round 1's fixes for its findings #3, #4, #5, #6, #9, #10, #12 and #13 were
  re-checked against the real source and hold.
