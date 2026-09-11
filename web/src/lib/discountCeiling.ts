// #727 — the client mirror of the server's `DiscountCeiling`.
//
// The server stores BASIS POINTS and compares cross-multiplied, so nothing is
// ever divided and the boundary has no rounding step that could disagree with
// itself. This file reproduces that arithmetic literally rather than
// re-deriving it from a percentage, and discountCeiling.test.ts pins it to the
// same vector table the C# unit tests use. Two implementations of one rule stay
// in step only while one table holds both.
//
// The wire carries a PERCENT (`Account.yourMaxDiscountPercent`) because a
// percent is what the screen shows and what a farm types into Farm settings;
// basis points are a storage choice.

export interface DiscountCeiling {
  // 0–10 000. The comparison's unit.
  readonly basisPoints: number;
  // The same ceiling as the screen renders it. Carried beside the basis points
  // rather than re-derived at each render site, so the number in the warning
  // and the number the comparison used can never be two different things.
  readonly percent: number;
}

// Null in, null out. Null covers BOTH "the farm sets no ceiling" and "this
// caller may exceed it" — the server collapses them deliberately, because both
// mean the same thing to the screen. A ceiling of 0 is neither: it is a real
// setting meaning "give nothing away", and it must not fall through here.
export function discountCeiling(percent: number | null): DiscountCeiling | null {
  if (percent === null) return null;
  // A percent that is not a whole number arrives as a double, and bp/100 is not
  // exactly representable: 12.34 * 100 is 1233.9999999999998, which loses the
  // boundary case by one ten-thousandth. Rounding recovers the integer the
  // server actually stored.
  return { basisPoints: Math.round(percent * 100), percent };
}

// `(list − unit) * 10 000 > bp * list`, strict, so a line sitting EXACTLY on the
// ceiling is allowed: "maximum 10%" means at most 10%.
//
// A zero list price falls out without a guard — the right side is 0 and the
// left side is `−unit * 10 000`, which is never positive for a unit price the
// validator accepts.
//
// A null list price is not "no discount", it is "not measurable here": the wire
// does not carry which of the three non-recorded bases produced it, so the
// server is the only place that can tell a recorded absence from an unknown.
// The screen declines to mark the row and lets the confirm answer.
export function lineExceedsCeiling(
  listUnitPriceMinorUnits: number | null,
  unitPriceMinorUnits: number,
  ceiling: DiscountCeiling,
): boolean {
  if (listUnitPriceMinorUnits === null) return false;
  return (listUnitPriceMinorUnits - unitPriceMinorUnits) * 10_000
    > ceiling.basisPoints * listUnitPriceMinorUnits;
}
