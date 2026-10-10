# Scale a sales line's list price to its unit, rounded half up (#1160)

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

## The rule

A line's default price and list price are the product's default price ×
(line unit eggs-per-unit ÷ product unit eggs-per-unit), **rounded half up to a
whole minor unit**. `AddOrderItemHandler` computes it with exact `Int128`
math; the SPA mirrors it for the prefill, the below/above-list hint and the
stale-list-price expectation. A line in the product's own unit, or for an
unpriced product, takes the unscaled path with no extra conversion lookup. A
line in another unit needs an active definition for the product's unit too,
or the add refuses with `SalesOrder.NoUnitConversion`. Recorded lines are
never rewritten; their list prices are #720 history.

## Why not the obvious alternative

When the factors do not divide evenly, the natural move is to refuse a list
price: record the line as `NotComparable`. That opens a hole in #727's
**discount ceiling**, which exempts a line whose basis says the discount is
not computable. A ceiling-bound seller could pick a unit that does not divide
evenly and sell at any price. A product at $13.00 a tray, sold Per: Egg, would
have no list price at all. Rounded, it lists at $0.43 an egg (1300 ÷ 30 =
43.33), and the ceiling measures the discount against that. The rounding
error is at most half a minor unit per selling unit.

## What this does NOT cover

- `UpdateOrderItem` changes quantity and price only. A line's unit cannot
  change after creation, so there is nothing to rescale.
- Lines recorded before this fix keep their unscaled list price. No migration
  or backfill corrects them.
- The SPA's `Math.round(price × lineFactor ÷ productFactor)` equals the
  server's half-up result only while `price × factor` is a safe integer.
  Beyond that, the server's number is the one recorded.

## How it is enforced

`tests/Cluckwork.Application.Tests/Sales/AddOrderItemHandlerTests.cs` pins both
directions, packed to packed, half-up rounding, the same-unit path, the scaled
snapshot under an explicit price, the scaled stale check, the inactive
product-unit definition and overflow.
`SalesProductTests.AddLine_InAnotherUnit_ScalesTheDefaultAndListPrice` pins
it through the endpoint, and `SalesPage.test.tsx` pins the SPA's prefill,
expectation and hint.
