# #722 — implementation plan

Companion to `01-design.md` (signed off 2026-09-10, sha256 `ed99f1ada709c97d…`).
The design carries the exact code; this carries the **order**, the **gates** and
the **mutation ledger**. The Phase 9 runbook turns these into fenced blocks.

**Base commit:** `cffed5ee5d0c6dd7731aa4fcb102307ff2dc3bbb`
**Branch:** `feat/722-audit-price-payload`
**Worktree:** `/home/mforce/.cluckwork-slices/722/worktree`

---

## Files

**Allow-list — nothing else may be touched.**

| File | Action | Increment |
|---|---|---|
| `tests/Cluckwork.Api.IntegrationTests/SalesOrderAuditPayloadTests.cs` | **create** (verified absent: `git ls-files` returns nothing) | 1a, 2a |
| `src/Cluckwork.Application/Features/Sales/UpdateOrderItem/UpdateOrderItemHandler.cs` | edit | 1b |
| `src/Cluckwork.Application/Features/Sales/AddOrderItem/AddOrderItemHandler.cs` | edit | 2b |

**Do-not-touch.** `src/Cluckwork.Domain/Sales/SalesOrder.cs`; `AuditWriter.cs`;
`IAuditWriter.cs`; `AuditActions.cs`; `web/src/i18n/enums.ts`; every migration;
`docs/schema/`; `tests/Cluckwork.Application.Tests/Common/AuditVocabularyCoverageTests.cs`;
`tests/Cluckwork.Api.IntegrationTests/CustomerAndOrderTests.cs`; anything under
`tools/`. **And the runbook itself** — it is delivered as an untracked path in
the checkout and must stay untracked.

## Gate commands, and what clean looks like

Derived from `.github/workflows/ci.yml`, not from memory. **Every Docker-touching
command is wrapped in `sg docker -c "…"`** — this machine's login sessions do not
carry the `docker` supplementary group, so a bare `dotnet test` fails
Testcontainers with `permission denied … /var/run/docker.sock`.

**G5 needs the wrapper too, and this is not obvious.** `tools/schema-docs/generate.sh`
calls `docker network create` / `docker run` / `docker exec` directly (lines
33-44), so it is Docker-touching by the same definition. Measured on the base
commit, 2026-09-10: **bare → exit 1, `permission denied while trying to connect
to the docker API`; wrapped → exit 0, `docs/schema/ is up to date.`** An earlier
draft of this table listed G5 bare, which would have produced a false "schema
docs are stale" and halted the slice on this plan's own stop rule.

| Gate | Command | Clean result |
|---|---|---|
| G1 build | `dotnet build Cluckwork.sln` | ends `0 Warning(s)` / `0 Error(s)` — warnings are errors here |
| G2 targeted | `sg docker -c "dotnet test tests/Cluckwork.Api.IntegrationTests/Cluckwork.Api.IntegrationTests.csproj --filter FullyQualifiedName~SalesOrderAuditPayloadTests"` | the increment's named tests, all passed |
| G3 release build | `dotnet restore Cluckwork.sln --locked-mode && dotnet build Cluckwork.sln --configuration Release --no-restore` | `0 Warning(s)` / `0 Error(s)` |
| G4 full suite | `sg docker -c "dotnet test Cluckwork.sln --configuration Release --no-build --verbosity normal"` | see counts below |
| G4b vuln gate | `dotnet list package --vulnerable --include-transitive --format json --output-version 1 \| node .github/scripts/vuln-gate.mjs --ecosystem nuget --level high` | exits 0. CI runs this **between restore and build** as a blocking gate (#146, `ci.yml`). This slice adds no package, so it cannot misfire — it is listed because a plan weaker than the gate is the failure mode, not because this diff is expected to trip it. |
| G5 schema docs | `sg docker -c "tools/schema-docs/generate.sh --check"` | prints `docs/schema/ is up to date.` and exits 0 — this slice adds no migration, so a failure here is a defect, never something to regenerate |

**Baseline, run on the base commit 2026-09-10 (`baseline-test.log`, exit 0):**
four project totals `10 / 388 / 260 / 1722`, **2380 passed, 0 failed, 0 skipped**.
**G5 verified green on the base commit** (wrapped), and **G1 verified green**. **Already failing at baseline: none, verified.** Block on anything *new or
changed* against this — never on "everything must be green".

**Expected after this slice: +4 tests.** All four land in
`Cluckwork.Api.IntegrationTests`, so that project goes `1722 → 1726` and the
grand total goes `2380 → 2384`. A different number is a STOP carrying the
number actually observed.

**No CI wiring is needed.** The new file goes in a project
`dotnet test Cluckwork.sln` already runs; adding wiring on top of inherited
coverage is duplicate work that future-breaks twice.

**Pre-commit hook:** `core.hooksPath` is **unset** in this worktree, so
`.githooks/pre-commit` does not run. CI is the authority. No increment may
commit a non-compiling tree regardless.

## Increment order

Increment 1 first because it is the smaller, transaction-free half: it proves
the payload mechanism, the test file and the assertion idiom before increment 2
adds a transaction boundary on top. Both increments are independent — one
handler each — and each ends at a green, committable tree.

### Increment 1 — the Update path

- **1a. RED.** Create `SalesOrderAuditPayloadTests.cs` with
  `UpdateItem_RecordsThePriceItChangedFromAndTo`. Drive it over HTTP:
  `POST /api/v1/customers` → `POST /api/v1/sales` → `POST /api/v1/sales/{id}/items`
  → `PUT /api/v1/sales/{id}/items/{itemId}` at a **different** unit price, then
  read the `SalesOrder.UpdateItem` row. Assert the payload carries the old price
  **and** the new one and that they differ, plus `salesOrderItemId`,
  `productId`, and `"listPriceBasis":"Recorded"` (row M5). **Seed the product
  with a `defaultPriceMinorUnits`** — `SeedProductAsync`'s last parameter — or
  the basis is `ProductUnpriced` and the M5 assertion pins the wrong member. Run G2. **Must be RED**, and the reason must be the payload being
  `null` — every call site passes `details: null` today. A red for any other
  reason is a STOP.
- **1b. GREEN.** Apply design §3.2 to `UpdateOrderItemHandler.cs`. Run G2, then
  G1. Commit.

### Increment 2 — the Add path (PROTECTED)

- **2a. RED.** Add three tests to the same file:
  - `AddItem_RecordsTheLineIdItCreated` — the `salesOrderItemId` in the payload
    equals the `ItemId` the endpoint returned (`SaleEndpoints.cs:121` returns
    `new { OrderId = id, ItemId = result.Value }`) and is not `Guid.Empty`.
  - `AddItem_RecordsListPriceBasisByName` — seed the product **with** a
    `defaultPriceMinorUnits` so the basis is `Recorded`, then assert the stored
    JSON contains `"listPriceBasis":"Recorded"`. Asserting the *name* is the
    point; `"listPriceBasis":0` must not satisfy it.
  Run G2. **Both must be RED**, each for its own stated reason — on base both
  call sites pass `details: null`, so `DetailsJson` is `null`.

  Then add a **third** test, which is deliberately **not** red-first:
  - `AddItem_WhenTheAuditWriteFails_RollsBackTheLine` — the owned path. Resolve
    the tenant **only**, never the actor, then call `AddOrderItemHandler`
    directly; assert it throws `InvalidOperationException` and that **no**
    `SalesOrderItem` row exists, read from a fresh scope.

  **This test PASSES on the base commit, and that is correct — do not treat it
  as a defect and do not try to make it red.** On base the audit write sits at
  `AddOrderItemHandler.cs:161`, *before* the save at `:164`, so with the actor
  unresolved `AuditWriter` throws at `AuditWriter.cs:44-48` before anything is
  saved and both assertions already hold. The rollback property is one this
  slice must **preserve**, not one it introduces. Its evidence is mutation row
  M4, not a red-first run. **So step 2a's red-first count is two, not three.**
- **2b. GREEN.** Apply design §3.1 to `AddOrderItemHandler.cs`. **The whole
  block is PROTECTED** — transcribe it exactly, comments and whitespace
  included, and do not repair it locally. A block that will not compile is a
  STOP reported to the driver, never an edit. Run G2, then G1. Commit.

### Close

Run G3, G4b, G4, G5 in the foreground and report G4's final summary lines. **Do not
background the suite** — a report whose last sentence is "waiting for the
background run" is a result nobody has seen.

## Mutation ledger

Every row is executed **as written**, not as intended. Apply → run the named
test → observe → restore → rebuild → confirm green. A row whose observation
differs from its prediction is information about the design; report it, do not
adjust the row to match.

| # | Mutate | Named test that must go RED | Why this is the observable mechanism |
|---|---|---|---|
| M1 | `AddOrderItemHandler`: drop `.ToString()` from `listPriceBasis` | `AddItem_RecordsListPriceBasisByName` | The stored JSON is what a reader sees; `"listPriceBasis":0` is the failure being guarded, and `AuditWriter.JsonOptions` registers no enum converter. |
| M2 | `UpdateOrderItemHandler`: inline the two locals back as `existing!.Quantity` / `existing.UnitPrice.MinorUnits` | `UpdateItem_RecordsThePriceItChangedFromAndTo` | Reads through the reference at serialisation time, after `SalesOrderItem.Update` mutated it — `before` becomes `after`, which is what the assertion pins. **Not** "move the lookup", which is a no-op. |
| M3 | `AddOrderItemHandler`: move `audit.WriteAsync` **above** `unitOfWork.SaveChangesAsync` inside the delegate | `AddItem_RecordsTheLineIdItCreated` | EF assigns the id during that save, so the payload carries `Guid.Empty`. This row is what proves the transaction restructure earned its cost — a green here means it bought nothing. |
| M4 | `AddOrderItemHandler`: unwind the transaction wrapper to straight-line code — **delete the lambda and the `Result<Guid>? outcome` local entirely**, then write, in order: `var result = order.AddItem(...);` → `if (result.IsFailure) return Result.Failure<Guid>(result.Error);` → `await unitOfWork.SaveChangesAsync(ct);` → `await audit.WriteAsync(..., ct: ct);` → `return Result.Success(result.Value.Id);`. **Spelled out because a literal "replace the wrapper with two bare saves" does not compile** — the lambda's `return false` / `return true` land in a method returning `Task<Result<Guid>>` (CS0029/CS1997). | `AddItem_WhenTheAuditWriteFails_RollsBackTheLine` | Save #1 autocommits the line before the audit write throws, so the row survives the fault. Only a test on the **owned** path can see this — over HTTP `IdempotencyMiddleware`'s transaction rolls both designs back identically. This is the sole evidence for that test, which is green on base by design. |
| M5 | `UpdateOrderItemHandler`: drop `.ToString()` from `beforeListPriceBasis` | `UpdateItem_RecordsThePriceItChangedFromAndTo` | INV-3 says *both* call sites write the basis by name, and M1 only covers the Add path — without this row the Update path's `.ToString()` is deletable with the whole suite green. Requires that test's product to be seeded **with** a `defaultPriceMinorUnits` so the basis is `Recorded`, and the test to assert `"listPriceBasis":"Recorded"` on the Update row alongside the two prices. |

Five rows, under the ⛔ owner-call threshold of 6. None is a closed-set
guard, so no member table applies.

## Caller ledger, per commit boundary

| After | Every production caller still behaviourally usable? |
|---|---|
| 1b | Yes. `UpdateOrderItemHandler` has one caller (`SaleEndpoints.UpdateOrderItem`); its request and response contracts are unchanged. Neither seeder calls it. |
| 2b | Yes. `AddOrderItemHandler` has three callers — `SaleEndpoints.AddOrderItem`, `SimulationDataSeeder.cs:112`, `DemoDataSeeder.cs:51`. The method signature, the `Result<Guid>` contract and every returned error code are unchanged; both seeders keep working and simply begin writing a payload. The seeders are the **owned** transaction path, which is why increment 2's third test drives that path rather than HTTP. |

## Stop-and-report limits

Stop and report to the driver, rather than improvising, on any of:

- a PROTECTED block that will not compile as transcribed;
- a G2 red at step 1a or 2a whose reason is not the one this plan states;
- a G4 total other than `2384`;
- any need to touch a file outside the allow-list;
- three consecutive failed attempts at one step;
- G5 failing (this slice adds no migration — never regenerate to make it pass);
- any instruction that would run something under `tools/simulation/` — a sibling
  agent owns that path right now and has asked that nothing there be run. No gate
  in this plan goes near it; if one appears to, that is a defect in the plan.
