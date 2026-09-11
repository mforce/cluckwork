import { describe, expect, it } from "vitest";
import { discountCeiling, lineExceedsCeiling } from "./discountCeiling";

// #727 — the shared vector table. The same rows are asserted against the
// server's DiscountCeiling in C#; two implementations of one comparison drift
// the moment each carries its own cases, so these are literals, not a
// re-derivation of the formula from the formula.
//
// Money is in MINOR UNITS throughout (1000 = $10.00 at a two-decimal currency).
const VECTORS: {
  name: string;
  percent: number;
  listMinorUnits: number | null;
  unitMinorUnits: number;
  exceeds: boolean;
}[] = [
  // The boundary. "Maximum 10%" means at most 10%, so exactly 10% off passes
  // and the next minor unit does not. This pair is what the strict `>` is for.
  { name: "exactly at the ceiling", percent: 10, listMinorUnits: 1000, unitMinorUnits: 900, exceeds: false },
  { name: "one minor unit past the ceiling", percent: 10, listMinorUnits: 1000, unitMinorUnits: 899, exceeds: true },
  { name: "well inside the ceiling", percent: 10, listMinorUnits: 1000, unitMinorUnits: 990, exceeds: false },
  { name: "at list", percent: 10, listMinorUnits: 1000, unitMinorUnits: 1000, exceeds: false },
  { name: "above list", percent: 10, listMinorUnits: 1000, unitMinorUnits: 1100, exceeds: false },

  // A ceiling of 0 is a real setting — sales staff may give nothing away — and
  // is NOT the absence of a ceiling. Collapsing the two is #719's null-means-
  // two-things trap.
  { name: "zero ceiling, sold at list", percent: 0, listMinorUnits: 1000, unitMinorUnits: 1000, exceeds: false },
  { name: "zero ceiling, one minor unit below list", percent: 0, listMinorUnits: 1000, unitMinorUnits: 999, exceeds: true },
  { name: "zero ceiling, sold above list", percent: 0, listMinorUnits: 1000, unitMinorUnits: 1001, exceeds: false },

  // A 100% ceiling permits giving the whole line away, and still permits
  // nothing more, because a negative unit price is not a thing the API stores.
  { name: "full ceiling, whole line given away", percent: 100, listMinorUnits: 1000, unitMinorUnits: 0, exceeds: false },

  // A zero list price cannot breach at any ceiling: the right side is 0 and the
  // left side is never positive.
  { name: "zero list price, zero ceiling", percent: 0, listMinorUnits: 0, unitMinorUnits: 0, exceeds: false },
  { name: "zero list price, sold for money", percent: 0, listMinorUnits: 0, unitMinorUnits: 500, exceeds: false },

  // Not measurable on the client: the wire does not say which of the three
  // non-recorded bases produced the null, so the screen never marks the row.
  { name: "no list price", percent: 0, listMinorUnits: null, unitMinorUnits: 500, exceeds: false },

  // A fractional ceiling is storable today (basis points) even though Farm
  // settings only offers whole percents, so the boundary is pinned there too.
  { name: "12.5% ceiling, exactly at it", percent: 12.5, listMinorUnits: 1000, unitMinorUnits: 875, exceeds: false },
  { name: "12.5% ceiling, one minor unit past it", percent: 12.5, listMinorUnits: 1000, unitMinorUnits: 874, exceeds: true },

  // 2.3 * 100 is 229.99999999999997 in binary floating point. Skip the rounding
  // back to the integer the server stored and the right-hand side lands a
  // fraction UNDER the left, so a line sitting exactly on the ceiling reports a
  // breach that is not one. Only the under-shoot direction is detectable, and
  // only at exact equality: one minor unit either way swamps the error, which
  // is why this pair is the boundary and not an arbitrary discount.
  { name: "2.3% ceiling, exactly at it", percent: 2.3, listMinorUnits: 10000, unitMinorUnits: 9770, exceeds: false },
  { name: "2.3% ceiling, one minor unit past it", percent: 2.3, listMinorUnits: 10000, unitMinorUnits: 9769, exceeds: true },
];

describe("discountCeiling (#727)", () => {
  it("is absent for a caller the server sends no ceiling for", () => {
    expect(discountCeiling(null)).toBeNull();
  });

  it("keeps a ceiling of zero, which is a setting and not an absence", () => {
    expect(discountCeiling(0)).toEqual({ basisPoints: 0, percent: 0 });
  });

  it("converts whole percents to the basis points the server stores", () => {
    expect(discountCeiling(10)).toEqual({ basisPoints: 1000, percent: 10 });
    expect(discountCeiling(100)).toEqual({ basisPoints: 10000, percent: 100 });
  });

  it("recovers the stored integer from a fractional percent's binary representation", () => {
    // 2.3 * 100 is 229.99999999999997 and 8.29 * 100 is 828.9999999999999.
    // 12.5 is exact and is here to show the conversion is not merely rounding
    // everything into the same bucket.
    expect(discountCeiling(2.3)?.basisPoints).toBe(230);
    expect(discountCeiling(8.29)?.basisPoints).toBe(829);
    expect(discountCeiling(12.5)?.basisPoints).toBe(1250);
  });
});

describe("lineExceedsCeiling vectors (#727)", () => {
  it("covers both answers, so a helper stuck on one constant cannot pass the table", () => {
    expect(VECTORS.some((v) => v.exceeds)).toBe(true);
    expect(VECTORS.some((v) => !v.exceeds)).toBe(true);
  });

  it.each(VECTORS)("$name", ({ percent, listMinorUnits, unitMinorUnits, exceeds }) => {
    const ceiling = discountCeiling(percent);
    expect(ceiling).not.toBeNull();
    expect(lineExceedsCeiling(listMinorUnits, unitMinorUnits, ceiling!)).toBe(exceeds);
  });
});
