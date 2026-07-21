import { describe, it, expect } from "vitest";
import { formatMoney, parseMoneyToMinorUnits } from "./cluckwork";

// formatMoney renders minor units using the currency's own minor-unit count —
// the value snapshotted on each row (F25) — never a hardcoded 2 decimals.
// Getting this wrong silently mis-displays money for 0- and 3-decimal currencies.
describe("formatMoney", () => {
  it("formats a 2-decimal currency", () => {
    expect(formatMoney(1050, "USD", 2)).toBe("10.50 USD");
  });

  it("renders zero at the currency's scale", () => {
    expect(formatMoney(0, "USD", 2)).toBe("0.00 USD");
  });

  it("formats a 0-decimal currency (JPY) with no fractional part", () => {
    expect(formatMoney(5, "JPY", 0)).toBe("5 JPY");
  });

  it("formats a 3-decimal currency (BHD)", () => {
    expect(formatMoney(123, "BHD", 3)).toBe("0.123 BHD");
  });

  it("keeps the sign on negative amounts (refunds/corrections)", () => {
    expect(formatMoney(-250, "USD", 2)).toBe("-2.50 USD");
  });

  it("does not group thousands (machine-stable, locale-free)", () => {
    expect(formatMoney(1000000, "EUR", 2)).toBe("10000.00 EUR");
  });
});

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

  // Negatives are excluded here on purpose: formatMoney renders them fine, but
  // the round-trip's point is the non-negative wire values callers actually send.
  it("round-trips with formatMoney for non-negative amounts", () => {
    for (const [minor, unit] of [
      [1050, 2],
      [5, 0],
      [123, 3],
      [1000000, 2],
      [0, 2],
    ] as const) {
      const display = formatMoney(minor, "USD", unit).split(" ")[0];
      expect(parseMoneyToMinorUnits(display, unit)).toBe(minor);
    }
  });
});
