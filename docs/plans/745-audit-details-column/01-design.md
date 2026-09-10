# #745 — the audit Details column · design

**Slice:** #745, epic #719. **Mode:** feature. Base `0481c06` (main, with #722 and #747 merged).
**Read from the artboard**, `docs/images/discount-mockups/AuditRow.png`, not from prose about it — that
substitution is what cost #722 a follow-up slice.

## What the artboard actually draws

Columns: `When | Actor | Action | Entity | Details`. The **Details** column replaces today's **Reason**.

| Action | Details cell, exactly as drawn |
|---|---|
| `SalesOrder.Confirm` | `[Discount 9.0%]` badge, then `Volume order` — the reason |
| `SalesOrder.UpdateItem` | `Medium Eggs` ~~`$5.40`~~ `→` **`$4.32`** `(list $5.40)` |
| `SalesOrder.UpdateItem` | `Large Eggs` ~~`$0.45`~~ `→` **`$0.40`** `(list $0.45)` |
| `SalesOrder.AddItem` | `Large Eggs ×240 at list $0.45` |

Three things the picture settles that prose did not:

- **The old price is struck through and the new price is bold** on Update rows. Add rows carry neither.
- **`(list $X)` is parenthetical**, in the muted weight, after the arrow pair.
- **The Entity column is unchanged.** `SO-10D2C04D` is today's rendering — `entityTypeLabel` plus
  `entityId.slice(0, 8)` — uppercased in the artboard's type styling. It is not a reference-number
  lookup, and this slice does not touch it.

## Scope

**In.** Rename the Reason column to Details. Render the two sales-line payloads as the summaries above.
Fall back to the row's `reason`, then to `—`.

**Out, and why.**

- **The `Discount 9.0%` badge on the Confirm row is not buildable today.** It needs the confirm-time
  discount and reason from **#721**, which has not shipped. Confirm rows keep rendering their reason,
  which is what the artboard shows beside the badge anyway. Noted on the issue rather than approximated.
- The Entity column, per above.
- Any other action's payload. ~25 `details:` call sites exist; this slice renders two of them and leaves
  the rest falling through to reason, exactly as today.

**Simplicity ceiling.** One component change in `AuditPage.tsx`, one small pure formatter, and the i18n
keys. No new dependency, no API change, no backend change. If the implementation needs a request to
`/api/v1/products` it has gone wrong — see below.

## The one rule this slice must not break

**Never resolve anything live.** The payload carries `productName`, `unit`, `currencyCode` and
`currencyMinorUnit` because #747 put them there, specifically so the renderer never has to look anything
up. Resolving `productId` against today's catalogue would render a renamed product under a name the
seller never saw — the defect #747 exists to close, reintroduced in the reader. **A fetch in this
component is a design failure, not an optimisation.**

## Data available, verified on `0481c06`

`SalesOrder.AddItem`: `salesOrderItemId`, `productId`, `productName`, `unit`, `quantity`,
`unitPriceMinorUnits`, `listUnitPriceMinorUnits`, `listPriceBasis`, `currencyCode`, `currencyMinorUnit`.

`SalesOrder.UpdateItem`: the same, plus `before { quantity, unitPriceMinorUnits }` and
`after { quantity, unitPriceMinorUnits }`, and no top-level `quantity`.

`listUnitPriceMinorUnits` is **nullable**, and `listPriceBasis` says why: `Recorded`, `ProductUnpriced`,
`NotComparable`, `PreDating`. #720's artboard is explicit that a null must render as `—` or
`No list price`, **never as a zero discount**. So the no-list-price case gets its own string rather than
formatting `null` as money.

## Invariants

| ID | Invariant | Enforcement |
|---|---|---|
| INV-1 | The renderer performs no network request and reads only the row's own payload. | `AuditPage.tsx`; no new hook, no new API import |
| INV-2 | Money is formatted with the payload's own `currencyCode` and `currencyMinorUnit`, never a hardcoded locale default. | `formatMoney(minorUnits, currencyCode, minorUnit, locale)`, `web/src/lib/format.ts:35` |
| INV-3 | A null `listUnitPriceMinorUnits` renders as its own phrase, never as a money value and never as zero. | the formatter's branch, pinned by a test |
| INV-4 | A payload that does not parse, or lacks an expected key, falls back to `reason` then `—`, and never throws. | the formatter returns null on any failure |
| INV-5 | Every user-visible string is a catalog key present in en, es and tl. | `catalogParity.test.ts` |

## Existing tests this changes

- `AuditPage.test.tsx:649` pins `["reasonHeader", "REASON-MARKER", "Reason"]` in a header-marker table.
  Renaming the key to `detailsHeader` changes that row. **The spec changes; the component does not lie
  about it** — the row is updated, not deleted.
- `AuditPage.test.tsx:194` — "maps each audit event's actor, action, entity and reason into its row" —
  and `:212`, which asserts a null reason renders `—`. Both must still hold: a row with no payload and
  no reason still shows `—`.

## Prediction to test

The formatter is the whole risk, so every branch gets a case: Add with a list price, Add without,
Update with a list price, Update without, a malformed payload, an absent payload. Each is a mutation
row — the fallback branches especially, because a formatter that throws on bad JSON would take the whole
table down, and a `try` that swallows silently would render `—` for rows that have data.
