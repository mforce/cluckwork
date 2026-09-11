import { describe, expect, it } from "vitest";
import { discountCeiling, exceedsCeiling, lineExceedsCeiling } from "./discountCeiling";

// #727 — the shared vector table, literal for literal from the C# DiscountCeiling
// unit tests. Two implementations of one comparison drift the moment each
// carries its own cases, so these are copied, never re-derived from the formula.
//
// Money is in MINOR UNITS throughout (1000 = $10.00 at a two-decimal currency),
// and as bigint, because the last two rows are past what a float64 separates.
const VECTORS: {
  name: string;
  basisPoints: number;
  list: bigint;
  unit: bigint;
  exceeds: boolean;
}[] = [
  // 1000 bp = 10%. Strict `>`, so exactly on the boundary is allowed: "maximum
  // 10%" means at most 10%.
  { name: "exactly 10.00% off", basisPoints: 1000, list: 1000n, unit: 900n, exceeds: false },
  { name: "10.10% off", basisPoints: 1000, list: 1000n, unit: 899n, exceeds: true },
  { name: "exactly 10.00% at a finer scale", basisPoints: 1000, list: 10000n, unit: 9000n, exceeds: false },
  { name: "10.01%, one minor unit past the boundary", basisPoints: 1000, list: 10000n, unit: 8999n, exceeds: true },
  { name: "at list", basisPoints: 1000, list: 1000n, unit: 1000n, exceeds: false },
  { name: "above list", basisPoints: 1000, list: 1000n, unit: 1200n, exceeds: false },

  // A zero list price never breaches, at any ceiling, and it falls out of the
  // arithmetic — there is deliberately no divide-by-zero guard to test.
  { name: "zero list price, zero unit price, 10% ceiling", basisPoints: 1000, list: 0n, unit: 0n, exceeds: false },
  { name: "zero list price, sold for money, 10% ceiling", basisPoints: 1000, list: 0n, unit: 500n, exceeds: false },
  { name: "zero list price, zero unit price, zero ceiling", basisPoints: 0, list: 0n, unit: 0n, exceeds: false },
  { name: "zero list price, sold for money, zero ceiling", basisPoints: 0, list: 0n, unit: 500n, exceeds: false },

  // 0 bp is a legal ceiling meaning "give nothing away", and is NOT the absence
  // of a ceiling — null is. Collapsing the two is #719's own trap.
  { name: "zero ceiling, at list", basisPoints: 0, list: 1000n, unit: 1000n, exceeds: false },
  { name: "zero ceiling, above list", basisPoints: 0, list: 1000n, unit: 1001n, exceeds: false },
  { name: "zero ceiling, one minor unit below list", basisPoints: 0, list: 1000n, unit: 999n, exceeds: true },

  // 10000 bp = 100%: the whole line may be given away, and nothing more is
  // expressible, because the validator refuses a negative unit price.
  { name: "full ceiling, whole line given away", basisPoints: 10000, list: 1000n, unit: 0n, exceeds: false },
  { name: "full ceiling, one minor unit charged", basisPoints: 10000, list: 1000n, unit: 1n, exceeds: false },
  // The other half of that pair, and the reason the row above is not enough: at
  // 9999 bp the same whole giveaway DOES breach. Without it, an implementation
  // that waved every free line through would pass the row above and look right.
  // C#: DiscountCeilingTests MaxBasisPoints / MaxBasisPoints - 1.
  { name: "one basis point below full ceiling, whole line given away", basisPoints: 9999, list: 1000n, unit: 0n, exceeds: true },

  // Headroom. Both cross-products are about 9e21 and differ by 10 000 — roughly
  // a thousand times long.MaxValue, and far past what a float64 separates. In
  // `number` these two rows return the same answer; in BigInt they do not.
  { name: "headroom: exactly 10.00% at the top of the range", basisPoints: 1000, list: 9000000000000000000n, unit: 8100000000000000000n, exceeds: false },
  { name: "headroom: one minor unit past it", basisPoints: 1000, list: 9000000000000000000n, unit: 8099999999999999999n, exceeds: true },

  // The SUBTRACTION, not the products. C#'s
  // IsExceededBy_DoesNotWrapWhenTheDiscountItselfExceedsALong pins this and
  // the table above had copied everything EXCEPT it, so narrowing the
  // subtraction here to 64 bits left all 31 rows green — the one defect this
  // module's own comment says BigInt exists to make unrepresentable went
  // unguarded. In 64 bits MaxValue − MinValue wraps to −1, reporting the
  // largest expressible discount as no discount at all.
  { name: "the discount itself exceeds a long", basisPoints: 0, list: 9223372036854775807n, unit: -9223372036854775808n, exceeds: true },
];

// The percent the wire carries, and the basis points the server stores.
// The REFUSALS in the server's TryParsePercent (12.345, -1, 100.01, 101) are
// its input validation for Farm settings, not this module's job: here the
// screen's own min/max/step and the server's validator hold the range, and a
// value that arrived over the wire has already passed both.
const PERCENTS: [number, number][] = [
  [0, 0],
  [0.01, 1],
  [10, 1000],
  [12.5, 1250],
  [12.34, 1234],
  [100, 10000],
];

describe("discountCeiling (#727)", () => {
  it("is absent for a caller the server sends no ceiling for", () => {
    expect(discountCeiling(null)).toBeNull();
  });

  it("keeps a ceiling of zero, which is a setting and not an absence", () => {
    expect(discountCeiling(0)).toEqual({ basisPoints: 0, percent: 0 });
  });

  it.each(PERCENTS)("reads %s%% as %s basis points", (percent, basisPoints) => {
    expect(discountCeiling(percent)).toEqual({ basisPoints, percent });
  });

  it("recovers the stored integer from a fractional percent's binary representation", () => {
    // 2.3 * 100 is 229.99999999999997 and 8.29 * 100 is 828.9999999999999.
    expect(discountCeiling(2.3)?.basisPoints).toBe(230);
    expect(discountCeiling(8.29)?.basisPoints).toBe(829);
  });
});

describe("exceedsCeiling vectors (#727)", () => {
  it("covers both answers, so a helper stuck on one constant cannot pass the table", () => {
    expect(VECTORS.some((v) => v.exceeds)).toBe(true);
    expect(VECTORS.some((v) => !v.exceeds)).toBe(true);
  });

  it.each(VECTORS)("$name", ({ basisPoints, list, unit, exceeds }) => {
    expect(exceedsCeiling(list, unit, { basisPoints, percent: basisPoints / 100 })).toBe(exceeds);
  });
});

// The wire-facing wrapper. Its own rows stay inside 2^53 on purpose: past that
// JSON.parse has already rounded the number, so a headroom row stated as a
// `number` literal would silently become its neighbour and pass for the wrong
// reason. The table above is where that magnitude is pinned.
describe("lineExceedsCeiling, the wire-facing form (#727)", () => {
  const tenPercent = discountCeiling(10)!;

  it("never breaches on a line with no comparable list price", () => {
    expect(lineExceedsCeiling(null, 0, tenPercent)).toBe(false);
    expect(lineExceedsCeiling(null, 500, discountCeiling(0)!)).toBe(false);
  });

  it("carries the boundary through from numbers", () => {
    expect(lineExceedsCeiling(1000, 900, tenPercent)).toBe(false);
    expect(lineExceedsCeiling(1000, 899, tenPercent)).toBe(true);
  });

  it("treats a zero ceiling as a ceiling", () => {
    expect(lineExceedsCeiling(1000, 1000, discountCeiling(0)!)).toBe(false);
    expect(lineExceedsCeiling(1000, 999, discountCeiling(0)!)).toBe(true);
  });
});
