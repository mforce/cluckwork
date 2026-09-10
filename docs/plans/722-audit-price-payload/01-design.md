# #722 — Price change in the sales-line audit payload · design

**Slice:** #722, epic #719 (discount visibility). **Mode:** feature.
**Front half:** `feature-workflow`, design drafted 2026-09-10.
**Provisional risk class:** **medium** — no schema change, no auth surface, two
call sites; but the payload carries money values and its whole purpose is
after-the-fact forensics, so a wrong or unreadable value is a defect nobody
detects until the incident it was written for.

> **Superseded in two places — read this before trusting the payload shapes below.**
> This document is the design as it stood when #722 was dispatched, kept as the
> point-in-time record. Two things changed after it:
>
> - **The payload gained `productName` and `unit` (#747, PR #748).** Every payload
>   snippet below carries `productId` and no name. That was the defect: the audit
>   screen's artboard renders product *names*, and resolving an id at read time
>   yields **today's** name, so a renamed product would re-render history under a
>   name the seller never saw. The name and unit are now snapshotted at write time,
>   the same rule #720 applies to the list price. `productId` stays, for the reason
>   given in §"`productId` is on this payload deliberately".
> - **Nothing rendered any of it until #745 (PR #749).** This slice is backend-only
>   by design and closed as complete while the audit screen still showed an em dash
>   on every sales-line row. See the amendment on #722 for the full map.

---

## 1. What actually shipped, versus what the issue says

The issue body predates #720 and its comment predates the final shape of #720.
Read against `cffed5e`, three of its statements are stale:

| Issue says | Shipped code says |
|---|---|
| "`UpdateOrderItemHandler` holds only the order, not the product, so the list price there arrives with #720" | `SalesOrderRepository.GetByIdAsync` does `.Include(o => o.Items)`, and `SalesOrderItem` now carries `ListUnitPriceMinorUnits` **and** `ListPriceBasis`. The list price is already in hand on the Update path. |
| "`AuditVocabularyCoverageTests` … the obvious tidy-up goes red" (body) / "a `details:` argument is invisible to it" (comment) | The comment is right and the body is wrong, **and the reason is stronger than the comment states**: `SplitTopLevelArguments` increments depth on `(`, `{` and `[`, so an anonymous object is one top-level argument. Only `args[0]` and `args[1]` are inspected, and both sit before any `details:`. Verified by reading the guard, not by trusting either note. |
| Acceptance: "orderable across a same-second burst (#508's `Sequence` tiebreak)" | Already met by shipped code. `Sequence` is a `bigint GENERATED ALWAYS AS IDENTITY` shadow property (`AuditEventConfiguration.cs:37`), used as the tiebreak in `AuditEventRepository`'s order clauses. **This slice does no work for that criterion**, and must not: `Sequence` reaches no response, export or screen by decision (#508), and it numbers audit events rather than order lines. |

## 2. Simplicity ceiling

**Smallest viable implementation, in one sentence:** add a `details:` argument to
the two existing `IAuditWriter.WriteAsync` calls, built from values the two
handlers already hold, and assert the serialised JSON in the integration suite.

**Complexity budget.** 2 source files, 1–2 test files, 0 new types, 0 new
interfaces, 0 new registrations, 0 migrations, 0 new `AuditActions` constants,
0 i18n keys. **Revised 2026-09-10 by the owner's line-id decision:** the Add
handler's tail is restructured onto `IUnitOfWork.ExecuteInTransactionAsync`, an
existing repo pattern used by `CreateProductHandler` — no new abstraction. The
cost differs by path and the larger one is not the HTTP one. **Joined (HTTP):**
one extra round trip per line added; `CommitAsync` is a no-op because
`IdempotencyMiddleware` owns the transaction. **Owned (both seeders, the CLI, a
direct-call test):** the work also gains an explicit `BEGIN`
(`AmbientTransaction.cs:66`) and `COMMIT` (`UnitOfWork.cs:36`) around what
previously ran under one implicit transaction — 1 round trip becomes 4, per
line, inside the seeders' loops (`SimulationDataSeeder.cs:1407,1429`;
`DemoDataSeeder.cs:51`). Acceptable: the seeders are development fixtures, not a
hot path, and correctness on that path is exactly what the transaction buys.
**Test budget, priced:** 1-2 test files, and one of them is the first test in
the repo to construct `AddOrderItemHandler` directly — `grep -rln
"AddOrderItemHandler" tests/` returns nothing today. The established idiom
exists (`scope.ResolveTenantAndActor(accountId)`, `TestHarness.cs:200`, used by
`CurrencyLockRaceTests.cs:103`), so this is a new test, not new scaffolding. **Non-goals:** no change to `IAuditWriter`'s signature; no change
to `AuditWriter`'s `JsonSerializerOptions`; no per-key renderer in the SPA; no
payload on `SalesOrder.RemoveItem`; no reordering of any audit write relative to
`SaveChangesAsync`.

An implementation that exceeds this stops and the driver takes the re-scope to
the owner.

## 3. The design

Two call sites change. Nothing else in `src/` does.

### 3.1 `AddOrderItemHandler` — the Add path carries the real line id

**Owner decision, 2026-09-10.** The first draft identified an added line by
`productId` + `quantity`, because `SalesOrderItem.Id` is unset until
`SaveChangesAsync`. The owner then supplied a fact the design did not have:
**duplicate products on one order are the common case, not an edge case.** With
`AddItem` carrying no duplicate-product guard (`SalesOrder.cs:43-69`, and
`AddOrderItemValidator` has none either), that identity does not identify, and
`UpdateItem` rewrites the `quantity` half of it (`SalesOrder.cs:104`). So the
line id is bought.

**PROTECTED — driver-authored, transcribed verbatim, never repaired locally.**
This is a transaction boundary; it is correctness-critical by failure class.

```csharp
// #722 — the audit row names the LINE, not just the order, so a discount is
// attributable on an order carrying the same product on several lines (which
// is the normal case). SalesOrderItem.Id is assigned by EF during
// SaveChanges (SalesOrderItem.Create leaves it unset — SalesOrder.cs:250), so
// the row can only be written after a save.
//
// ExecuteInTransactionAsync is what keeps #93's guarantee across that save:
// the audit row commits or rolls back with the sale. It JOINS
// IdempotencyMiddleware's request-wide transaction on the HTTP path and OWNS
// one for a direct caller — the seeders and unit tests, which have no ambient
// transaction and would otherwise get two independent commits
// (AmbientTransaction.cs:7-22). Its owned branch runs through
// SingleAttemptExecution and is never replayed (#269), so the pair cannot
// double-write.
//
// Every validation above this point runs OUTSIDE the transaction on purpose.
// The only failure left inside is AddItem's own, and all three of its failure
// paths return before `_items.Add` (SalesOrder.cs:48-58) — so the `return
// false` below leaves nothing tracked, which matters because a joined
// scope's RollbackAsync is a no-op (AmbientTransaction.cs:95).
//
// Nullable, not a placeholder failure: a future branch that forgets to set it
// then NREs loudly at `return outcome!`, where a synthetic Error.Validation
// would instead return a silent 400 carrying an error code no validator, no
// locale and no coverage test knows. This is CreateProductHandler.cs:40's
// shape and the reason for it.
Result<Guid>? outcome = null;

await unitOfWork.ExecuteInTransactionAsync(async token =>
{
    var result = order.AddItem(
        product.Id, product.ProductType, grade.Id,
        unit, conversion.EggsPerUnit, command.Quantity, unitPrice,
        listUnitPriceMinorUnits, listPriceBasis);
    if (result.IsFailure)
    {
        outcome = Result.Failure<Guid>(result.Error);
        return false;
    }

    // Assigns result.Value.Id. Pinned by
    // AddItem_RecordsTheLineIdItCreated.
    await unitOfWork.SaveChangesAsync(token);

    // #494 — see RemoveOrderItemHandler: draft-only, recorded for attribution.
    await audit.WriteAsync(
        AuditActions.SalesOrderAddItem, nameof(SalesOrder), order.Id,
        details: new
        {
            salesOrderItemId = result.Value.Id,
            productId = product.Id,
            quantity = command.Quantity,
            unitPriceMinorUnits = unitPrice.MinorUnits,
            listUnitPriceMinorUnits,
            listPriceBasis = listPriceBasis.ToString(),
            currencyCode = order.TotalAmount.CurrencyCode,
            currencyMinorUnit = order.TotalAmount.CurrencyMinorUnit,
        },
        ct: token);

    outcome = Result.Success(result.Value.Id);
    return true;
}, ct);

return outcome!;
```

The wrapper's own `db.SaveChangesAsync` then persists the audit row and
`CommitAsync` commits — a no-op when joined, where the middleware owns the
outcome (`UnitOfWork.cs:26-38`).

**The shape is `CreateProductHandler.cs:40-71`'s**, which already runs an
`AddAsync` + `audit.WriteAsync` pair inside `ExecuteInTransactionAsync`. Diffed
against it line by line, not asserted: same `Func<CancellationToken, Task<bool>>`
delegate; the same **nullable** `Result<Guid>? outcome = null` at `:40` returned
as `outcome!` at `:71`; the same forwarding of the *delegate's own* token
(`ct: transactionCt` there, `ct: token` here) rather than the outer `ct`. One
difference, and it is this slice's whole point: an interleaved
`SaveChangesAsync` between the mutation and the audit write.

**This replaces the handler's existing tail**, which today is `order.AddItem`,
`audit.WriteAsync`, one `unitOfWork.SaveChangesAsync(ct)`, then
`return Result.Success(result.Value.Id)`. There is no second save added to the
request: the count goes from one to two, and the second is the wrapper's.

### 3.2 `UpdateOrderItemHandler.cs:30`

**Every payload value is materialised into a local BEFORE `order.UpdateItem(...)`.**
`order.Items.FirstOrDefault(...)` returns a *reference*, and `SalesOrderItem.Update`
(`SalesOrder.cs:243-248`) mutates that same instance in place — `Quantity`,
`QuantityBase`, `UnitPrice`. An anonymous object built after the call therefore
reads the NEW values through that reference, and `before` would equal `after` on
every row. Holding the reference is not enough; the *values* must be read early.

This mirrors the mechanism, not just the shape, of
`UpdateFarmSettingsHandler.cs:54`, which materialises `var before = Snapshot(account)`
before its mutation and calls `Snapshot` again for `after` at line 148.

The list price and basis are not mutated by `Update` today, but they are captured
early with everything else deliberately: a reader then needs no knowledge of which
fields `Update` happens to touch, and a future change to `Update` cannot silently
turn `before` into `after` for one key while the others stay honest.

```csharp
// Materialised BEFORE UpdateItem: SalesOrderItem.Update (SalesOrder.cs:243)
// mutates the tracked instance in place, so values read through `existing`
// after the call are the NEW ones. Pinned by
// UpdateItem_RecordsThePriceItChangedFromAndTo.
var existing = order.Items.FirstOrDefault(i => i.Id == command.ItemId);
var beforeQuantity = existing?.Quantity;
var beforeUnitPriceMinorUnits = existing?.UnitPrice.MinorUnits;
var beforeProductId = existing?.ProductId;
var beforeListUnitPriceMinorUnits = existing?.ListUnitPriceMinorUnits;
var beforeListPriceBasis = existing?.ListPriceBasis.ToString();

var result = order.UpdateItem(command.ItemId, command.Quantity, unitPrice);
if (result.IsFailure)
    return result;

// Reaching here proves the lookup found the item: UpdateItem returns
// Error.NotFound for the same id (SalesOrder.cs:94-95), so the captures above
// are non-null. Ordering these two statements the other way makes that false.
await audit.WriteAsync(
    AuditActions.SalesOrderUpdateItem, nameof(SalesOrder), order.Id,
    details: new
    {
        salesOrderItemId = command.ItemId,
        productId = beforeProductId!.Value,
        before = new
        {
            quantity = beforeQuantity!.Value,
            unitPriceMinorUnits = beforeUnitPriceMinorUnits!.Value,
        },
        after = new
        {
            quantity = command.Quantity,
            unitPriceMinorUnits = unitPrice.MinorUnits,
        },
        listUnitPriceMinorUnits = beforeListUnitPriceMinorUnits,
        listPriceBasis = beforeListPriceBasis,
        currencyCode = order.TotalAmount.CurrencyCode,
        currencyMinorUnit = order.TotalAmount.CurrencyMinorUnit,
    },
    ct: ct);
```

`before`/`after` mirrors
`src/Cluckwork.Application/Features/Accounts/UpdateFarmSettings/UpdateFarmSettingsHandler.cs:148`,
the repo's existing shape for a recorded transition.

**`productId` is on this payload deliberately.** `SalesOrder.RemoveItem` does
`_items.Remove(item)` (`SalesOrder.cs:81`) against a required FK with
`OnDelete(DeleteBehavior.Cascade)` (`SalesOrderConfiguration.cs:42-45`), so the
row is deleted. Without `productId`, an add → discount → remove sequence leaves
an `UpdateItem` row keyed by a Guid that resolves to no row in any table.
`existing.ProductId` is public (`SalesOrder.cs:203`) and already in hand.

### 3.3 The enum is written as a NAME, not a number

`AuditWriter`'s options are `new JsonSerializerOptions(JsonSerializerDefaults.Web)`
(`AuditWriter.cs:17`). Web defaults give camelCase naming and case-insensitive
reads; they do **not** register `JsonStringEnumConverter`. So a bare
`ListPriceBasis` would serialise as `0`/`1`/`2`/`3`.

That is unacceptable here specifically: the whole point of #720's basis is that
`ProductUnpriced` and `NotComparable` are recorded facts while `PreDating` means
"we do not know", and #727 gates an approval on that difference. A stored `2`
also silently re-reads as a different basis if anyone ever reorders the enum.

**Decision: `.ToString()` at the call site**, not a converter on
`AuditWriter.JsonOptions`. A converter would rewrite the shape of the 25 existing
`details: new` payloads (`grep -rc "details: new" src/`, 2026-09-10) — including
`CreateProductHandler.cs:65`, which stores `ProductType` numerically, and
`UpdateProductHandler.cs:78`, which does the same for `DefaultUnit`. That is a
change to shipped behaviour outside this slice's scope, for no benefit this slice
needs. **Those two numerically-stored enums are a real latent defect of the same
class** — reorder either enum and history re-reads as a different member — and
they are recorded in §5's ownership map as unowned rather than fixed here.

**`.ToString()` is the established house idiom here, not a novelty.**
`src/Cluckwork.Application/Features/Accounts/UpdateFarmSettings/UpdateFarmSettingsHandler.cs`
— the same handler whose `before`/`after` shape §3.2 mirrors — already calls
`.ToString()` on four enums inside its `details:` payload: `UnitSystem` (:158),
`FirstDayOfWeek` (:159), `DefaultStepperUnit` (:163) and
`WorkerSaleAllocationPolicy` (:164). Verified by reading, 2026-09-10.

## 4. Invariants

| ID | Invariant | Enforcement sites (symbols) | Discovered | Source |
|---|---|---|---|---|
| INV-1 | The `SalesOrder.AddItem` payload carries the line's real `SalesOrderItem.Id` — never `Guid.Empty`. That requires the audit write to follow a `SaveChangesAsync`, inside one transaction. | `AddOrderItemHandler.HandleAsync` (`ExecuteInTransactionAsync` → `SaveChangesAsync` → `WriteAsync`); `SalesOrderItem.Create` leaves `Id` unset (`SalesOrder.cs:250`) | owner decision 2026-09-10, replacing the draft's `productId`+`quantity` identity | front-half signoff |
| INV-2 | The `SalesOrder.UpdateItem` payload's `before` values are read from the tracked item **before** `SalesOrder.UpdateItem` runs. | `UpdateOrderItemHandler.HandleAsync`; `SalesOrderItem.Update` (mutates in place) | design | front-half signoff |
| INV-3 | `listPriceBasis` is serialised as its NAME. | both call sites (`.ToString()`); `AuditWriter.JsonOptions` registers no enum converter | design | front-half signoff |
| INV-4 | Both call sites keep a literal `AuditActions.*` reference as argument 0 and `nameof(SalesOrder)` as argument 1; no new `AuditActions` constant is minted. | `AuditActions.SalesOrderAddItem`, `AuditActions.SalesOrderUpdateItem`; guarded by `AuditVocabularyCoverageTests` (args[0], args[1]) and its `web/src/i18n/enums.ts` mirror | design | front-half signoff |
| INV-5 | The audit row commits or rolls back with the change it records (#93). On the **Update** path that is the single `SaveChangesAsync` it is appended before. On the **Add** path the save is split, so the guarantee is carried by `ExecuteInTransactionAsync` instead — which joins the ambient request transaction on HTTP and owns one for a direct caller, so a seeder or a direct-call test never gets two independent commits. | `IAuditWriter` contract comment (#93); `AuditWriter.WriteAsync` (no SaveChanges of its own); `UnitOfWork.ExecuteInTransactionAsync`; `AmbientTransaction.RunAsync`; both handlers | restated 2026-09-10 for the owner's line-id decision | front-half signoff |
| INV-6 | Every money value in the payload travels with the order's `currencyCode` and `currencyMinorUnit`. | both call sites; `Money(MinorUnits, CurrencyCode, CurrencyMinorUnit)` | design | front-half signoff |
| INV-7 | Nothing inside the Add path's transaction can fail by returning `false` after the aggregate has been mutated. Every validation runs before the transaction opens, and `AddItem`'s own three failure paths all return before `_items.Add`. | `AddOrderItemHandler.HandleAsync` (validation order); `SalesOrder.AddItem` (`SalesOrder.cs:48-58`); `JoinedTransactionScope.RollbackAsync` — a joined rollback is a no-op (`AmbientTransaction.cs:95`) | owner decision 2026-09-10 | front-half signoff |

**Mechanically enumerated readers of the payload** (`grep -rn "DetailsJson" src/`
and `"detailsJson" web/src`, 2026-09-10) — none needs a change, and each is a
site a reviewer can check against:

- `src/Cluckwork.Api/Endpoints/Audit/AuditEndpoints.cs:45,51` — API response, passthrough string.
- `src/Cluckwork.Infrastructure/Repositories/ExportQueries.cs:267` — `audit-events` CSV column, passthrough.
- `src/Cluckwork.Domain/Auditing/AuditEvent.cs:23,52` — the property itself.
- `src/Cluckwork.Infrastructure/Persistence/Configurations/AuditEventConfiguration.cs:21` — mapping.
- `web/src/api/cluckwork.ts:922` — `detailsJson: string | null`.
- `web/src/routes/AuditPage.tsx:409` — rendered as the row's `title` tooltip, raw.

Set difference between readers and enforcement sites: empty — no reader parses
the JSON, so no reader can break on a key this slice adds.

**Non-CI callers of the changed write contract (#394).** Both seeders drive the
real handler rather than the aggregate, so both fixtures start carrying the new
payload the moment this ships:

- `src/Cluckwork.Infrastructure/Persistence/SimulationDataSeeder.cs:112` — injects `AddOrderItemHandler`.
- `src/Cluckwork.Infrastructure/Persistence/DemoDataSeeder.cs:51` — injects `AddOrderItemHandler`.

Neither needs a change, and nothing pins what they produce here: the only
`DetailsJson` assertion in `SimulationSeederTests.cs` is at line 1574 and it
parses a `UserFlockAssign` row, not a sales one. `ExportTests.cs:272-273` asserts
on export-dataset payloads only. `tools/simulation/k6/` and the Playwright specs
under `tools/simulation/ui/` drive the HTTP surface, whose request and response
contracts this slice does not touch — no new field, no new status, no new
required input. Verified by reading, 2026-09-10.

**One more reader of these two action names**, found by the same sweep and
unaffected: `AuditEventRepository.cs:213-215` lists `SalesOrderAddItem`,
`SalesOrderUpdateItem` and `SalesOrderRemoveItem` in its `draftingActions` set
for the provenance query. It selects on `Action`, never on `DetailsJson`.

## 5. Ownership map (epic #719)

| Surface | Owning slice | Lands first | Forward-compat carried by |
|---|---|---|---|
| `SalesOrderItem.ListUnitPriceMinorUnits`, `ListPriceBasis`, the backfill | #720 | shipped (`cffed5e`) | — |
| `SalesOrder.AddItem` / `SalesOrder.UpdateItem` audit payload | **#722 (this slice)** | this slice | — |
| Order-screen discount marking and order total | #723 | after #722, independent of it | #723 |
| Discount badge in the Orders list | #724 | after #723 | #724 |
| Discount reason at confirm (new column + `SalesOrder.Confirm` payload) | #721 | after #722 | #721 |
| Per-farm ceiling + approval | #727 | after #721 | #727 |
| Report / export discount totals | #725 | after #721 | #725 |
| Customer discount history | #726 | after #725 | #726 |
| Alert-centre entry | #728 | after #727 + alert centre (#15) | #728 |
| SPA per-key renderer for `detailsJson` | **unowned** — follow-up named in #722's mockup comment | not this slice | file at Phase 13b |
| `SalesOrder.RemoveItem` audit payload | **unowned** — out of this slice's scope | — | — |
| Numerically-stored enums in `CreateProductHandler.cs:65` and `UpdateProductHandler.cs:78` `details:` payloads | **unowned** — same defect class as §3.3, found while designing this slice | — | file at Phase 13b |
| `detailsJson` as a parsed contract (key names, versioning, documentation) | **unowned** — becomes load-bearing when #721/#727 read the payload back | — | #721 / #727 |

#723 and #724 are in flight in a sibling session. They touch `web/` and the
order read surfaces; this slice touches two Application handlers and the
integration suite. **No shared file.** Both run in separate git worktrees.

## 6. Conflict table

| Plan requires | Repo's canonical rule | Wins |
|---|---|---|
| A `details:` argument on two `WriteAsync` calls | `AuditVocabularyCoverageTests` fails closed on argument 0 and inspects argument 1 | **Plan** — the splitter is brace-aware and neither argument moves. Verified by reading the guard. |
| No new audit action | "reuse the existing `AuditActions` constants; minting a new one drags `web/src/i18n/enums.ts` and three locales" (#722 comment) | **Rule** — plan already complies. |
| No GLOSSARY / Help update | "every PR that adds or changes user-visible behavior updates GLOSSARY.md and the SPA Help page" (AGENTS.md, owner directive 2026-07-17) | **Owner decision, 2026-09-10: reasoned no-op.** No new concept and no new *translatable* string. Stated precisely, because "not user-visible" would be false: `AuditPage.tsx:409` sets `title={e.detailsJson ?? undefined}`, so these two row types gain a tooltip they do not have today, carrying English key names and raw minor-unit integers identically in all three locales. What the waiver rests on is that nothing is labelled, nothing is translated and no concept is introduced — not on the change being invisible. The reasoning goes in the PR body verbatim so the repo-rules reviewer judges a stated decision rather than a silent omission. |
| Enum serialised as a name | The repo is inconsistent and the dominant convention for THIS payload shape already uses names: `UpdateFarmSettingsHandler.cs:158,159,163,164` and `UpdateEggUnitConversionHandler.cs:30` call `.ToString()` inside `details:`, while `CreateProductHandler.cs:65` and `UpdateProductHandler.cs:78` store enums numerically | **Rule** — the plan follows the convention that already governs a `before`/`after` details payload. Not a slice-local deviation. |
| Every aggregate mutation bumps `Version` | AGENTS.md, "Data and correctness" | **Rule** — not engaged: this slice adds no mutation. `SalesOrder.UpdateItem` already bumps `Version` at `SalesOrder.cs:106`; `AddItem` is unchanged. |

## 7. What this design hedges about, and the predictions to test

Five claims here are predictions, and one of them (4) is a property this slice preserves rather than introduces — its evidence is a mutation row, never a red-first run. The runbook orders each as a red-first check
with a mutation row, rather than recording it as a caveat.

1. **`listPriceBasis` would serialise as a number without `.ToString()`.**
   Test asserts `"listPriceBasis":"Recorded"` in the stored `DetailsJson`.
   Mutation: drop the `.ToString()` on the Add path; the named test must go RED
   (the payload then carries `"listPriceBasis":0`).

2. **The Update `before` values are lost unless materialised before `UpdateItem`.**
   Test asserts one `SalesOrder.UpdateItem` row carries BOTH the old and the new
   unit price, and that they differ. Mutation: **inline `beforeQuantity` and
   `beforeUnitPriceMinorUnits` back into the payload as `existing!.Quantity` /
   `existing.UnitPrice.MinorUnits`** — read through the reference at
   serialisation time; the named test must go RED with `before == after`.

   *An earlier draft of this row said "move the `existing` read below
   `order.UpdateItem(...)`". That is a no-op against the code as now written and
   could never have gone red — the lookup's position is not what makes the
   values correct, the materialisation is. Corrected before dispatch; the design
   defect it was hiding is fixed in §3.2.*

3. **The Add payload's `salesOrderItemId` would be `Guid.Empty` without the
   interleaved save.** Test asserts the id in the `SalesOrder.AddItem` payload
   equals the id the API returned, and is not `Guid.Empty`. Mutation: move the
   `audit.WriteAsync` above the `unitOfWork.SaveChangesAsync` inside the
   delegate; the named test must go RED on `Guid.Empty`. **This is the row that
   proves the whole transaction restructure earned its cost** — if it stays
   green, the restructure bought nothing.

4. **The audit row still commits or rolls back with the sale (#93, INV-5) —
   and the fault has to be injected, or the row proves nothing.**

   *A first draft of this row said: drive the handler directly and assert that
   the sale and its audit row are both present, and neither present when the
   unit fails, with the mutation "replace `ExecuteInTransactionAsync` with two
   bare `SaveChangesAsync` calls". That row is decoration. Under the mutation
   the success half still writes both rows, and the failure half is unreachable
   by INV-7 — the delegate's only `return false` is `AddItem` failing, which
   mutates nothing. Both halves stay GREEN and the mutant survives. The only
   schedule that separates the two designs is a fault BETWEEN the two saves.*

   Test: drive `AddOrderItemHandler` **directly, outside HTTP** — the owned
   path, where no ambient transaction exists — in a scope with the tenant
   resolved and **the actor deliberately not resolved**
   (`scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(accountId)`
   alone, *not* `ResolveTenantAndActor`). `AuditWriter` then throws at its own
   actor guard (`AuditWriter.cs:44-48`) — a natural fault between save #1 and
   the commit, needing no test double and no DI surgery. Assert the call throws
   `InvalidOperationException` **and that no `SalesOrderItem` row exists**,
   read from a fresh scope.

   Mutation M4: unwind the wrapper to straight-line code (the ledger in the
   implementation plan spells it out, because the one-line phrasing does not
   compile). The named test must go RED — save #1 has already committed the
   line, so the row is there after the throw. Driving this over HTTP would prove
   nothing: `IdempotencyMiddleware`'s own transaction rolls both designs back
   identically, which is the "layered defenses mask mutants" case. This row
   names the layer it must break.

   **This test is GREEN on the base commit, and that is correct.** On base the
   audit write precedes the save (`AddOrderItemHandler.cs:161` vs `:164`), so an
   unresolved actor throws before anything is saved and both assertions already
   hold. The rollback property is one this slice **preserves**, not one it
   introduces — so it is excluded from prediction 5's red-first rule, and M4 is
   its only evidence. Point 5 below applies to the other assertions.

5. **No test currently pins that these two events carry a payload at all.**
   Both call sites pass `details: null` today and the suite is green, so each
   new assertion must be shown RED on the base commit before the code change.
   A green-on-base assertion is a defect in the test, not evidence about the
   code.

## 8. What remains unowned, and what it costs later

The owner bought the line id, so the Add↔line attribution gap is closed. Three
things stay outside this slice, recorded so a later slice does not assume
otherwise:

- **`SalesOrder.RemoveItem` still writes no payload.** An add → discount →
  remove sequence leaves the Add and Update rows (both now carrying the real
  line id and the product), and a Remove row saying only that a line went away.
  The trail is joinable and the prices are recorded; what is missing is the
  removed line's final state. Unowned in §5.
- **`detailsJson` becomes a parsed contract the moment #721 or #727 reads it
  back.** Today no reader parses it (§4). At that point it needs key-name
  stability and documentation, and nothing guards either: `docs/schema/` documents
  the *column*, and `AuditVocabularyCoverageTests` inspects only arguments 0 and 1.
  Unowned in §5; the decision belongs to whichever of those slices reads first.
- **Two catalog payloads store enums numerically** —
  `CreateProductHandler.cs:65` and `UpdateProductHandler.cs:78`. Same defect
  class as §3.3: reorder either enum and stored history re-reads as a different
  member. Unowned in §5, filed at Phase 13b.
