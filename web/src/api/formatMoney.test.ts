import { describe, it, expect } from "vitest";
import { parseMoneyToMinorUnits } from "./cluckwork";

// formatMoney moved to lib/format.ts with #650 (it takes the farm locale now);
// its pins live in lib/format.test.ts. This file keeps the parser's.

// parseMoneyToMinorUnits is the inverse used when reading a user-typed amount
// back into the wire format. The Math.round is load-bearing: naive
// `parseFloat * 10**n` leaves binary-float dust that would truncate a cent.
describe("parseMoneyToMinorUnits", () => {
  it("parses a 2-decimal amount", () => {
    expect(parseMoneyToMinorUnits("10.50", 2)).toBe(1050);
  });

  // The load-bearing case: 0.29 * 100 = 28.999999999999996 in IEEE-754, which
  // Math.round rescues to 29 (a truncating parse would ship 28). NB 10.10 * 100
  // is exactly 1010 in JS — not a float-dust case, so 0.29/1.10 are the real pins.
  it("rounds away binary-float artifacts (0.29 → 29, not 28)", () => {
    expect(parseMoneyToMinorUnits("0.29", 2)).toBe(29);
    expect(parseMoneyToMinorUnits("1.10", 2)).toBe(110); // 110.00000000000001 → 110
    expect(parseMoneyToMinorUnits("10.10", 2)).toBe(1010);
  });

  it("scales to the currency's minor unit (0-dec JPY, 3-dec BHD)", () => {
    expect(parseMoneyToMinorUnits("5", 0)).toBe(5);
    expect(parseMoneyToMinorUnits("0.123", 3)).toBe(123);
  });

  it("accepts an integer string and a leading-dot decimal", () => {
    expect(parseMoneyToMinorUnits("7", 2)).toBe(700);
    expect(parseMoneyToMinorUnits(".5", 2)).toBe(50);
  });

  it("returns NaN for non-numeric, empty, or whitespace-only input", () => {
    expect(parseMoneyToMinorUnits("abc", 2)).toBeNaN();
    expect(parseMoneyToMinorUnits("", 2)).toBeNaN();
    expect(parseMoneyToMinorUnits("   ", 2)).toBeNaN();
  });

  // Inherited parseFloat behaviour, pinned so a future change is a conscious one:
  it("takes the leading number of a partly-numeric string (parseFloat semantics)", () => {
    expect(parseMoneyToMinorUnits("10abc", 2)).toBe(1000);
  });

  it("propagates Infinity (caught by the callers' Number.isFinite guard)", () => {
    expect(parseMoneyToMinorUnits("Infinity", 2)).toBe(Infinity);
  });

  it("passes a negative through (caller rejects < 0)", () => {
    expect(parseMoneyToMinorUnits("-2.50", 2)).toBe(-250);
  });

  // A sub-minor negative rounds to -0, which slips a `< 0` guard (Object.is(-0,0)
  // is false but -0 < 0 is false too). Documented so callers know the boundary.
  it("rounds a sub-minor negative to -0 (slips a naive < 0 check)", () => {
    expect(Object.is(parseMoneyToMinorUnits("-0.001", 2), -0)).toBe(true);
    expect(parseMoneyToMinorUnits("-0.001", 2) < 0).toBe(false);
  });

  // No display→parse round-trip here: since #650 the display string is
  // locale-formatted ("$1,000.00") and is never fed back into the parser —
  // the parser reads what the user TYPED into an input, not what a table shows.
});
