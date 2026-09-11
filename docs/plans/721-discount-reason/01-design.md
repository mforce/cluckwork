# #721 — Require a discount reason when confirming a below-list order

Epic #719, slice 5. Cut from `origin/main` at `ea84801`, after #720 (`cffed5e`),
#722 (`97c866f`) and #723/#724 (`1a07441`) shipped.

## 0. What already shipped, read from the code at `ea84801`

| Fact | Where |
|---|---|
| `SalesOrderItem.ListUnitPriceMinorUnits: long?` — snapshot, never re-resolved | `src/Cluckwork.Domain/Sales/SalesOrder.cs:230` |
| `ListPriceBasis { Recorded, ProductUnpriced, NotComparable, PreDating }`, paired with the value by a throw in `Create` | `SalesOrder.cs:188-198`, `264-284` |
| `SalesOrder.Confirm()` takes no arguments, guards via `CheckCanConfirm()`, bumps `Version`, raises `SalesOrderConfirmedEvent` | `SalesOrder.cs:141-150` |
| `SalesOrder.Void(reason)` is the in-repo precedent for a required free-text reason: blank check, length cap `MaxVoidReasonLength = 500`, `reason.Trim()` stored | `SalesOrder.cs:5`, `155-175` |
| `VoidSaleValidator` **mirrors** both domain rules (blank, trimmed length) at the boundary | `src/Cluckwork.Application/Features/Sales/VoidSale/VoidSaleValidator.cs` |
| `ConfirmSaleCommand(Guid SalesOrderId)` — no validator exists today | `.../ConfirmSale/ConfirmSaleCommand.cs:3` |
| `ConfirmSaleHandler` runs one transaction: Account `FOR SHARE` → SalesOrder `FOR UPDATE` → `CheckCanConfirm` → fresh role → FIFO lock → plan → apply → `order.Confirm()` → audit | `.../ConfirmSale/ConfirmSaleHandler.cs:58-270` |
| The confirm endpoint takes **no body** and every caller POSTs with no `Content` at all | `SaleEndpoints.cs:51`, `.../ConfirmSale`, `TestHarness.cs:566-574` |
| An **optional** request body already has a precedent in this repo: `DisableUserRequest? request` bound with `request?.Reason` | `src/Cluckwork.Api/Endpoints/Users/UserEndpoints.cs:357`, `379` |
| SPA `lineDiscount(item)` → `none \| atList \| below \| above`; `below` iff `list !== null && unitPrice < list` | `web/src/routes/SalesPage.tsx:131-142` |
| SPA `orderDiscount(items)` → `unknown \| atList{partial} \| below{...,partial}` | `SalesPage.tsx:175-205` |
| `onConfirm` uses `useConfirm().confirm({title, body, confirmLabel})` — plain yes/no | `SalesPage.tsx:763-781` |
| `useConfirm` is the app's single dialog primitive, two shapes (`confirm`, `askReason`), settling through one `resolveRef` | `web/src/components/useConfirm.tsx:24-96` |
| `askReason` returns `string \| null` and enforces non-blank **inline**, keeping the dialog open | `useConfirm.tsx:92-96`, `114-125` |
| The order-level discount paragraph sits directly above the order total | `SalesPage.tsx:1143-1175` |
| The void reason renders as `<p className="muted">` under the order | `SalesPage.tsx:1391-1393` |
| `discount`, `listPrice`, `aboveList`, `noListPrice`, `discountTotal*`, `discountBadge*`, `discountUnknown` exist in en/es/tl | `web/src/i18n/en.ts:405-432`, `es.ts:317-328`, `tl.ts:333-344` |
| `web/src/i18n/enums.ts` is the ONLY sanctioned enum renderer; each family is a `_VALUES` tuple + `_KEYS` map `as const satisfies Record<Union, EnumsKey>` | `web/src/i18n/enums.ts:1-45` |

**The decisive rows.** `Confirm()` is argument-free and `ConfirmSaleCommand` is a
one-field record, so the reason rides in on both without disturbing the locking
sequence. And the confirm POST carries no body today, so the new body **must**
bind as optional or every existing caller 400s — `DisableUser` proves that shape
works here.

## 1. Simplicity ceiling

**Smallest viable implementation, in one sentence:** two nullable columns on
`SalesOrders`, one predicate on the aggregate (`HasBelowListLine`), the rule
inside `Confirm(code, note)`, an optional confirm body carrying them, and one
new `useConfirm` shape that asks for a code plus a note.

**Complexity budget:** ~18 files. Domain (1), migration (2 + snapshot), EF
configuration (1), command/handler/validator (3), endpoint + DTOs (1),
SPA client (1), `SalesPage.tsx` (1), `useConfirm.tsx` (1), `enums.ts` (1),
i18n en/es/tl (3), `helpGlossary.ts` (1), `GLOSSARY.md` (1), export (1),
`docs/schema/` (generated).

**Non-goals:**

- **No per-line reason.** Open question 2 is answered *per order* — see §3.
- **No approval flow, no ceiling.** That is #727, which lands on the same
  confirm path deliberately after this one.
- **No report or customer-history column.** #725 and #726 read the field this
  slice creates; they do not land here.
- **No backfill.** Orders confirmed before this migration have `NULL` reason and
  must read as *not recorded*, never as *no discount* — the same rule #720 wrote
  for `PreDating`.
- **No change to `lineDiscount` / `orderDiscount`**, to the #720 wire contract,
  or to `ListPriceBasis`.
- ~~No audit payload on `SalesOrderConfirm`.~~ **Reversed by the owner
  (#756):** the audit row now carries `discountReasonCode` and
  `discountReasonNote` when the order is discounted, because the columns
  alone are invisible to the Audit page's History view, which reads events,
  not the aggregate. This is not a second source of the same truth: the
  reason is write-once (set only at `Confirm`, never edited afterward) and
  the audit row commits in the SAME transaction as the columns, so the two
  cannot disagree — the duplication risk this bullet originally guarded
  against does not exist. Omitted entirely, not written as nulls, when the
  order carries no reason.

## 2. Design decisions

### 2.1 The data shape: two nullable columns, paired by the domain

```csharp
public enum DiscountReasonCode { Volume, DamagedStock, LongStandingCustomer, ManagerApproved, Other }

// on SalesOrder
public DiscountReasonCode? DiscountReasonCode { get; private set; }
public string? DiscountReasonNote { get; private set; }
public const int MaxDiscountReasonNoteLength = 500;
```

Stored **by name**, not by ordinal (#751's lesson, and the same reason
`ListPriceBasis` is readable): a reordered enum must not silently relabel every
historical row. `DiscountReasonNote` is `varchar(500)`, matching `VoidReason`.

Why not a value object. `DiscountReason(code, note)` as an owned type buys one
invariant (*Other* implies a note) that the aggregate already has to enforce at
`Confirm` time anyway, and costs an owned-entity mapping plus a nullable-owned
EF quirk. Two columns beside `VoidReason`, which is the same shape and the same
lifecycle, is the smaller change (**principle-laziness-protocol**).

### 2.2 The rule lives on the aggregate, because the data does

```csharp
// A line is discounted iff a comparable list price exists and the sale price is
// under it. NULL list price is not a discount (acceptance 3); above list is not
// a discount (open question 4). Same two branches as the SPA's lineDiscount.
public bool HasBelowListLine => _items.Any(
    i => i.ListUnitPriceMinorUnits is { } list && i.UnitPrice.MinorUnits < list);
```

`Confirm(DiscountReasonCode? code, string? note)` then, in order:

1. `CheckCanConfirm()` — unchanged, still first.
2. `!Enum.IsDefined(code)` → throw. #720 R10: a cast can produce a value no
   member names, and it must not persist.
3. `HasBelowListLine && code is null` → `SalesOrder.DiscountReasonRequired`.
4. `code == Other && note is blank` → `SalesOrder.DiscountReasonNoteRequired`.
5. `code is not null || note is not null`, and `!HasBelowListLine` →
   `SalesOrder.DiscountReasonNotApplicable`. **Amended while implementing:** the
   rule as first written was `code is not null` alone, which let a note with no
   code beside it persist on an undiscounted order — the pair out of step, and
   the same row `#725` would miscount that rule 5 exists to prevent. Pinned by
   `Confirm_WithANoteAndNoCode_OnAnOrderThatIsNotDiscounted_IsRefused`.
6. `note` trimmed; blank becomes `null`; over the cap →
   `SalesOrder.DiscountReasonNoteTooLong`.
7. Store, set `Confirmed`, `Version++`, raise the event.

**Rule 5 is a deliberate refusal, not a convenience.** Silently storing a reason
on an order that gave nothing away writes a row that #725's "discounts by
reason" report would count as a discount. Dropping it silently is worse: it is
data loss disguised as tolerance. The cost is a real race — a concurrent line
edit between the dialog and the confirm can make the order undiscounted, and the
seller then sees an error for a reason they were correctly asked for. That race
already exists on this path for stock (`EggLot.InsufficientStock`) and the
order is under `FOR UPDATE`, so the refusal is at least consistent and the SPA
recovers by refetching. Recorded here so #727 does not rediscover it.

**Amended while implementing:** all six checks live in `CheckCanConfirm`, not
in `Confirm`, and `Confirm` delegates to it. The design put them in `Confirm`,
which meant a missing reason was refused only *after* `ConfirmSaleHandler` had
planned and applied a whole FIFO allocation, and the handler's
"contradicted its own CheckCanConfirm" throw turned that refusal into a 500.
`CheckCanConfirm` is the method #612 added precisely so the handler can refuse
before touching stock, so the discount rules belong beside `NotDraft` and
`NoItems` in it. The handler passes the reason to both calls.

**Where each rule is enforced.** The domain owns all of them, because the
seeders call `ConfirmSaleHandler` directly and never see a validator (#394 —
a validator-only rule is invisible to every seeder test). `ConfirmSaleValidator`
mirrors only the syntactic half (code parses to a defined member, trimmed note
within the cap), exactly as `VoidSaleValidator` mirrors `Void`'s.

### 2.3 The wire: an optional body on the existing endpoint

```csharp
public sealed record ConfirmSaleRequest(string? DiscountReasonCode = null, string? DiscountReasonNote = null);
```

`ConfirmSale(Guid id, ConfirmSaleRequest? request, ...)`, read as
`request?.DiscountReasonCode`. **Amended while implementing:** the nullable
parameter is necessary but not sufficient. A typed body parameter attaches
`application/json` Accepts metadata, which the consumes matcher turns into a
route CONSTRAINT, so a POST with no `Content-Type` at all — which is what every
existing caller sends — stopped matching the route and fell through to
Program.cs's `/api/{**rest}` catch-all as a 404. The route therefore also
declares `*/*`. `DisableUser` does not disprove this; it has the same 404, pinned
by `Disable_WithNoContentTypeAtAll_Is404_NotUnsupportedMediaType`, and its
callers all send a content type. Pinned here by
`Confirm_WithNoContentTypeAtAll_StillConfirms`. The code crosses the wire as a **string** and is
parsed with `Enum.TryParse(..., ignoreCase: false)`, the same shape
`AddOrderItemRequest.Unit` already uses — an unparseable value is a 400 from the
validator, never a silent default.

Rejected: a separate `PATCH /discount-reason` on the draft. It adds an endpoint,
a second write, and a window where a draft carries a reason it may never use.

Rejected: a required body. Every existing caller — ~20 integration test files,
both seeders, k6, the Playwright specs — POSTs with no `Content`, so a required
body is a 400 for all of them. **That existing suite is the proof the optional
binding works**: if it binds wrong, those tests go red, which is exactly the
signal wanted.

New error codes map to **422**, which the endpoint's existing `else` branch
already does — only `SalesOrder.NotDraft` is a 409. No endpoint status change.

`SalesOrderResponse` gains `string? DiscountReasonCode` and
`string? DiscountReasonNote`, appended with defaults after `CustomerName`, so no
positional break.

### 2.4 The dialog: a third shape on `useConfirm`, not a second primitive

`onConfirm` branches on the order it already holds:

- no below-list line → today's `confirm()`, unchanged. (Acceptance 2.)
- at least one → `askChoice({...})`, resolving `{ code, note } | null`.

Why extend the shared hook rather than add a `<DiscountReasonDialog>` to
`SalesPage`: `useConfirm`'s own doc comment states it is one hook rather than
two components precisely so a screen needing several shapes still renders a
single element, and its inline "required" handling (error under the field,
focus returned to it, dialog stays open) is the behaviour this picklist needs
for the *Other* note. A second dialog idiom on the same screen is the drift the
#688 rule exists to stop. The cost is widening the settle union to
`boolean | string | ChoiceResult | null`; the existing `useConfirm.test.tsx`
guards the two shapes that must not change.

## 3. The epic's open questions, answered

| # | Question | Answer | Why |
|---|---|---|---|
| 2 | Reason per line or per order? | **Per order** | The issue's own scope says `SalesOrder`. A per-line reason multiplies clicks on the app's most routine write and buys precision no consumer (#725, #726, #728) asks for. |
| 4 | Flag a markup above list too? | **No** | `HasBelowListLine` uses a strict `<`. An above-list line is not a discount, and the SPA already renders it as its own `above` state. |
| — | Is #730 close enough to cut this to free text? | **No** | #730 is unscheduled on the epic and needs its own scoping decision, so the picklist is not about to be superseded. Recorded because the epic makes this an explicit precondition. |

## 4. Guards this change must satisfy

- **`Version++` with a parallel-race integration test** (AGENTS.md). `Confirm`
  already bumps it; the race test is two concurrent confirms of the same
  below-list order, one winning and one refused.
- **#407** — exactly one new migration; `InitialCreate` untouched.
- **#417** — `docs/schema/` regenerated in the same PR (`generate.sh`, needs Docker).
- **#394** — the four non-CI callers are audited in §5 and updated in this PR.
- **#688 / i18n** — every new string in en, es and tl; help prose naming this
  control uses that control's label **per locale**, checked by reading each.
- **Owner directive** — *discount reason* enters `specs/product/GLOSSARY.md` and
  the in-app glossary in this PR.
- **enums registry** — a new family in `web/src/i18n/enums.ts`; find its guards
  by grepping the registry's readers, never by recall.
- **#662** — this slice changes the confirm dialog, so the PR attaches a 1:1
  before/after capture from a stack rebuilt at the head under review.

## 5. #394 — the callers CI does not cover

Filled from the inventory pass; each row states whether the caller confirms an
order that is **below list** (and therefore breaks without a reason) or at/above
list (and therefore does not).

| Caller | Confirms a below-list order? | Action |
|---|---|---|
| `DemoDataSeeder` | **No.** Both lines are added with a `null` unit price (`AddOrderItemCommand(confirmed, largeEggs, 360, null, null)`, `DemoDataSeeder.cs:359-362`), so the handler prices them at `Product.DefaultPriceMinorUnits` — at list by construction. | None. |
| `SimulationDataSeeder` | **No.** `EnsureDraftOrderAsync` passes `AddOrderItemCommand(orderId, productId, quantityEggs, null, null)` (`SimulationDataSeeder.cs:1408`), and `EnsureConfirmedOrderAsync` confirms that order (`:1465`). At list. | None. |
| `tools/simulation/k6/` | **No — it never confirms a sale.** Sales is a read-only persona: `bundles.js:391-393` records that "confirming sales/recording payments stays in the deferred hazard passes (#243 load-model rule 2)", and `personas/sales.js:5` says the same. | None. |
| `tools/simulation/ui/specs/sales.spec.ts` | **No, by 5 minor units.** It fills `UNIT_PRICE = "0.50"` (`:40`, `:99`) against `Sim Large Eggs` at `45` (`SimulationDataSeeder.cs:1175`), so the line is **above** list. | Add a comment at `:40` pinning why the price must stay at or above the seeded list price. |
| `tools/simulation/ui/specs/worker-sale-allocation.spec.ts` | **YES — it breaks.** It fills `"0.01"` (`:83`) against the same `45` list price, so the order is deeply below list. After this change the first click opens the reason dialog, `getByRole("dialog", { name: tEn("sales:confirmOrderTitle") })` (`:105`) never appears, no `/confirm` POST is ever sent, and the spec times out on a `waitForResponse` that can never settle. | Drive the new dialog: pick a reason, then confirm. Keep `0.01` and keep the #612 assertions intact — the spec then covers both paths. |

**This row is the whole point of rule #394.** Nothing in CI runs that spec, the
price is incidental to what it was written to test, and every gate stays green
while it hangs. It was found by reading the two files against the seeded
catalog, not by running anything.

## 5b. Export

The two columns join the **`sales-orders`** dataset in
`src/Cluckwork.Infrastructure/Repositories/ExportQueries.cs:164-170`, beside
`voidReason` and `version`, as `discountReasonCode` and `discountReasonNote`.

That is the same call #720 made one dataset down: its comment at `:172-175` says
`sales-order-items` carries `listUnitPriceMinorUnits` and `listPriceBasis`
because this export is "the full-fidelity export of the table", explicitly "not
#725's scope — that owns discount TOTALS in reports, not raw column fidelity
here". A column on `SalesOrders` follows the same rule, so the reason ships here
and the report totals stay with #725.

The dataset header is pinned by a guard; find it by grepping the readers of
`ExportQueries` under `tests/`, not by recall.

## 6. Risks

1. **Optional body binding.** If `ConfirmSaleRequest?` does not bind from an
   empty body, every confirm caller 400s. Mitigated by the `DisableUser`
   precedent and detected immediately by the existing suite.
2. **`useConfirm` union widening** touches Void, Cancel and the daily-entry
   dialogs. Mitigated by `useConfirm.test.tsx` and by leaving `confirm` and
   `askReason` signatures byte-identical.
3. **A seeder that discounts** turns a green baseline red only at runtime, in a
   verb nothing in CI runs. §5 is read from the source, not from a test run.
