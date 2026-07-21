import { describe, it, expect } from "vitest";
import { formatMoney } from "./cluckwork";

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
