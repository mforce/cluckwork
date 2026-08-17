# Gate 3 review round 1 — findings and dispositions

> **Planning record — seeded audit events carry a real actor ([#500](https://github.com/mforce/cluckwork/issues/500)), August 2026.** What was *intended* at the time, not what shipped. The issue is closed; where this disagrees with the code, the code is right. See [`docs/plans/README.md`](../README.md).

Four independent reviews of `03-program-design.md` (2026-08-11): a contrarian
pre-mortem and a technical pass (both `pi`, deepseek-v4-flash on local vllm), a
codebase-verified technical pass (`codex`), and two Claude Sonnet agents (one on
guard/test quality, one on architecture-vs-codebase). Kept because several
findings are facts about the codebase that the next session should not have to
rediscover.

## Confirmed and fixed in the revised design

| # | Finding | Found by | Disposition |
|---|---|---|---|
| 1 | **Seven seeder handlers write no audit row at all** — `CreateCustomer`, `RecordPayment`, `CreateInventoryItem`, `RecordPurchase`, `RecordFeedUsage`, `RecordWaterUsage`, `CreateExpenseCategory`. There is no `Payment.Record` / `Customer.Create` / `InventoryItem.Create` action; only `Payment.Void`, `InventoryItem.Adjust`, `WaterUsage.Correct` — all corrections the seeder never performs. | guard agent, codex | Persona table rebuilt from the **audited** action set. The `Payment.* → sim-sales-` assertion was **vacuous** (queries zero rows, passes for any implementation) and is deleted. |
| 2 | `ResolveSystemActor` pins `ActorId = Guid.Empty`, but `SimulationSeed_LeavesNoUnattributedAuditEvent` asserted zero empty actor ids — and a real setup runs `bootstrap-admin` first, so such a row exists. Self-contradictory. | codex | Both seed assertions now run against a **delta**: snapshot audit-event ids before seeding, assert only on rows the seeder wrote. |
| 3 | `SimCast` cannot carry the restricted pair. `SeedCastAsync` returns before flocks exist; `RestrictOneWorkerAsync` returns `Task` (`SimulationDataSeeder.cs:452`). Positional-record properties can't be assigned later — won't compile; left empty, `WorkerFor` treats the restricted worker as unrestricted, which is the bug the chain exists to prevent. | codex | `RestrictOneWorkerAsync` returns the pair; the caller rebuilds the record with `with`. |
| 4 | `Rotate` is not total. `Simulation:Managers=0` / `Sales=0` are valid config (`SimulationOptions.cs` has no positive-count validation, and the seeder already tolerates `Workers=0`), so `pool[i % 0]` divides by zero. The Owner fallback covered only `WorkerFor`. | codex | Replaced by `Pick(pool, index, fallback)` — total for every pool, Owner as the universal fallback. |
| 5 | None of the four services takes `CurrentUserContext` today; every `currentUser.*` call as written is CS0103. The plan called two of them "one line" changes. | codex, architecture agent | Constructor parameters listed explicitly for all four. |
| 6 | `MissingBaseDataAsync` returns `bool` and discards the owners it queried, but the call stack said "ownerActor ← the Owner it already found". | codex | `FindOwnerAsync` added to both seeders; `SeedAsync` calls it once and passes the result into the preflight. No second query. |
| 7 | `Where(...)` returns `IEnumerable<SimActor>`; `Rotate` takes `IReadOnlyList<SimActor>` — CS1503. | codex | Materialized. |
| 8 | `ReadOnly` personas (4 by default) are never an actor in any phase, so `EveryPersonaAuthorsSomething` is unsatisfiable by a **correct** implementation. | guard agent, codex | Split: writing personas must author something; ReadOnly must author **nothing** — the deliberate exclusion is now itself asserted, per #407's "walk everything, exclude deliberately". |
| 9 | **`Rotate` distribution is untestable at the fixture's own config.** `SimulationOptions` defaults are `Managers=1, Sales=1, Workers=3, ReadOnly=4`, and `SimulationSeedFactory` overrides none of the counts. With `Count==1`, `pool[i % 1]` is `pool[0]` for every `i` — identical to the proposed `pool[0]`-always mutant. That mutation-table row proved nothing. | guard agent | Rotation is now asserted **exactly** over the Workers pool (size 3) and the limitation on Manager/Sales rotation is stated, not hidden. |
| 10 | Nothing pinned rotation **determinism**. A future `random` pick still produces both provenance shapes and still converges on re-run (existence guards mean a re-run writes nothing), so neither `CarriesBothProvenanceShapes` nor a convergence test can catch it. | contrarian | The exact-mapping assertion in #9 fails for any non-deterministic pick. |
| 11 | Mutation table missing the mutation that can actually ship: **remove only the actor guard, keep the tenant guard**. The listed "revert to the `(unresolved)` fallback" mutation is not the same edit. | contrarian | Added. |
| 12 | `WriteAsync_WithUnresolvedActor_WritesNothing` cannot distinguish guard placement — `AuditWriter` never calls `SaveChangesAsync`, so a guard moved to *after* `AddAsync` leaves the table empty too. | guard agent | Now asserts the **change tracker** holds no added `AuditEvent`. |
| 13 | `DemoSeed_WithNoOwner_FailsClosed` doesn't prove role-specificity — a `FindOwnerAsync` that checks "any user exists" passes it. | guard agent | Fixture seeds a lone **Manager**, no Owner. |
| 14 | `docs/decisions/283-first-run-admin-provisioning.md` missing from the docs-to-update list, though it is the canonical statement of the `bootstrap-admin` flow whose audit row changes. | contrarian | Added. |
| 15 | The "drop `ActAs` from `SeedFlockHistoryAsync`" mutation row undercounts — it also kills `CarriesBothProvenanceShapes`. | guard agent | Corrected. |

## Confirmed by review, and NOT a defect

- **The four-caller list is complete.** Both codex and the architecture agent
  independently walked every `IAuditWriter` injection, every file in
  `Infrastructure/Jobs/` (`DailyEntryLockSweep`, `RefreshTokenPurgeSweep`,
  `IdempotencyRecordPurgeSweep`, `DurableJobWorker` — none reference
  `IAuditWriter`) and every `CliDispatcher.Commands` verb. Nothing missed.
- **Actor resolution precedes every audit write in all four callers.** The
  contrarian's top-severity pre-mortem was that `AdminRecoveryService` might
  write an audit row before line 102's resolve, killing break-glass. Codex read
  the file: it does not. Refuted.
- **DI/process-role is sound, and this does not repeat #331/#347.**
  `AddScoped<CurrentUserContext>()` is unconditional, registered in `Program.cs`
  before CLI dispatch, and is a plain DI registration — not a
  `.ValidateOnStart()` or an eager boot guard, so it cannot abort a one-shot verb.
- **No partial state on the new throw.** It fires before any `AddAsync`, and
  every audit-writing path already runs inside `AmbientTransaction.RunAsync`,
  which commits only on success.
- **#269 retry boundary is untouched.** `AuditWriter` never calls
  `SaveChangesAsync`; the resolved actor is plain in-memory scoped state.
- **#370 holds.** `tools/simulation/bootstrap.sh` and `reset.sh` never invoke
  `seed --profile demo`.
- `SeedSecondAccountAsync` writes `db.Accounts` directly and audits nothing.

## Escalated to the owner — Gate 1 backtrack

**Finding 16 (architecture agent):** the Gate 1 decision-A prerequisite breaks a
contract that is explicitly pinned by a test, not merely an undocumented habit.
`tests/Cluckwork.Api.IntegrationTests/SeedCommandTests.cs` spawns the real
`seed --profile demo` CLI against databases where `bootstrap-admin` never ran:

- `SeedCommand_Demo_SeedsDataAndExitsWithoutStartingKestrel` (lines 71-90)
- `SeedCommand_Demo_AgainstAnUntouchedDatabase_MigratesAndSeedsInOneStep`
  (lines 135-145), whose own comment states that a green result *"proves
  `seed --profile demo` needs nothing but a connection string"* — the exact
  claim the new preflight invalidates.

`SeedResult.PrerequisitesMissing` is not `IsSuccess`, so `SeedCliCommand` exits
non-zero and both tests fail. Plus `Boot_NeverAutoSeedsDemo_OnlyExplicitSeedAsyncDoes`
(`DemoSeedTests.cs:99`), which the plan wrongly claimed needed no change.

This also exposes a structural gap in `02-architecture.md`: it measured the
blast radius of the **actor throw** and treated that as the blast radius of the
whole change. The **demo prerequisite** is a second, independent breaking change
with its own callers, and it was never measured. AGENTS.md #394 covers
request/status contract changes; a CLI *prerequisite* change is the same class
of miss with a different trigger.

Decision needed — see the question put to the owner at the Gate 3 re-approval.
