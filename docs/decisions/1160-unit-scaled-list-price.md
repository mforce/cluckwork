# Scale a sales line's list price to its unit, rounded up (#1160)

> **Rule** — the short version lives in
> [`specs/product/GLOSSARY.md`](../../specs/product/GLOSSARY.md) under
> **Sales line (#99)** and **List price (#720)**; this file is the rationale.

**Status:** accepted
**Date:** 2026-10-10

## What happened

A product's default price is per its own selling unit (**Sold per** on
Products). A sales line can be sold in another unit through **Per**, and since
#100 both the line's default price and, since #734, its list-price snapshot
used the product's price unscaled. Large Eggs at $0.45 per egg, sold Per: Tray,
prefilled $0.45 a tray and recorded a $0.45 list price, so 8 trays (240 eggs)
came to $3.60 instead of $108.00. The reverse, a tray-priced product sold per
egg, overcharged 30 times. No test sold a line in a unit other than the
product's own at the product's default price; `PackedUnit_FactorSnapshots_RedefineOnlyAffectsFutureLines`
asserted the unscaled total as correct.

The first version of the fix rounded half up. Review found that it could turn
a positive price into a free line: a product at $1.30 a case of 360, sold per
egg, scaled to 0.36 of a cent and rounded to zero. The line then recorded a
list price of zero, sold at zero was "at list", and a Sales user under a 0%
discount ceiling could confirm the whole case for nothing. That case moved the
rule to rounding up.

## The rule

A line's default price and list price are the product's default price ×
(line unit eggs-per-unit ÷ product unit eggs-per-unit), **rounded up to a
whole minor unit**. `AddOrderItemHandler` computes it in exact `Int128`
integers, `(price × lineFactor + productFactor − 1) ÷ productFactor`, and
refuses with `SalesOrder.LineTotalTooLarge` above `long.MaxValue`. The SPA
mirrors it in `BigInt` for the prefill, the below/above-list hint and the
stale-list-price expectation, and treats a result beyond
`Number.MAX_SAFE_INTEGER` as unknown: no prefill and no expectation, so the
server applies its own default. A line in the product's own unit, or for an
unpriced product, takes the unscaled path with no extra conversion lookup. A
line in another unit needs an active definition for the product's unit too,
or the add refuses with `SalesOrder.NoUnitConversion`. Recorded lines are
never rewritten; their list prices are #720 history.

Rounding up is chosen for three reasons. A positive price can never scale to
zero. The default never undercuts the bulk price, so no hidden discount slips
under #727's ceiling. And dividing a bulk price into single units this way is
common practice. The cost is that a customer pays at most one minor unit more
per selling unit than the exact share: a $13.00 tray sold per egg is $0.44,
not $0.4333.

## Why not the obvious alternative

When the factors do not divide evenly, the natural move is to refuse a list
price: record the line as `NotComparable`. That opens a hole in #727's
**discount ceiling**, which exempts a line whose basis says the discount is
not computable. A ceiling-bound seller could pick a unit that does not divide
evenly and sell at any price. A product at $13.00 a tray, sold Per: Egg, would
have no list price at all. Rounded up, it lists at $0.44 an egg, and the
ceiling measures the discount against that.

Half-up rounding, the first version, fails the same ceiling from the other
side: a small bulk price rounds to a zero list price, and a free line is then
"at list".

## What this does NOT cover

- `UpdateOrderItem` changes quantity and price only. A line's unit cannot
  change after creation, so there is nothing to rescale.
- Lines recorded before this fix keep their unscaled list price. No migration
  or backfill corrects them.
- A scaled price that is a valid server `long` but beyond
  `Number.MAX_SAFE_INTEGER` gets no SPA prefill or list-price expectation. The
  money parser refuses such a typed price anyway.

## How it is enforced

`tests/Cluckwork.Application.Tests/Sales/AddOrderItemHandlerTests.cs` pins both
directions, packed to packed, rounding up (1300 ÷ 30 → 44, 130 ÷ 360 → 1), a
free product staying free, the same-unit path, the scaled snapshot under an
explicit price, the scaled stale check, the inactive product-unit definition
and overflow. `SalesProductTests.AddLine_InAnotherUnit_ScalesTheDefaultAndListPrice`
pins it through the endpoint, and
`SalesDiscountCeilingTests.AFreePerEggLineFromACasePrice_IsRefusedUnderAZeroCeiling`
pins the free-line case at confirm. `SalesPage.test.tsx` pins the SPA's
prefill, expectation and hint, the exact result at the safe-integer edge, and
the unknown result beyond it.
