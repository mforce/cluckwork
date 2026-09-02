import { describe, it, expect } from "vitest";
import { DEFAULT_LOCALE, formatCount, formatDate, formatMoney } from "./format";

// §4.5 display rule: money, counts and calendar dates render through the
// FARM's locale + currency + date-format override (#650). The UI language is
// never an input here — formattingIndependence.test.ts pins that side.

describe("formatMoney", () => {
  it("formats a 2-decimal currency with the locale's symbol and grouping", () => {
    expect(formatMoney(123456, "USD", 2, "en-US")).toBe("$1,234.56");
  });

  it("renders zero at the currency's scale", () => {
    expect(formatMoney(0, "USD", 2, "en-US")).toBe("$0.00");
  });

  it("formats a 0-decimal currency (JPY) with no fractional part", () => {
    expect(formatMoney(1235, "JPY", 0, "ja-JP")).toBe("￥1,235");
  });

  // Intl separates a code or symbol from the figure with U+00A0, which is the
  // point: the unit never wraps away from its number.
  it("formats a 3-decimal currency (BHD) at three places, never a hardcoded two", () => {
    expect(formatMoney(123, "BHD", 3, "en-US")).toBe("BHD\u00a00.123");
  });

  it("keeps the sign on negative amounts (refunds/corrections)", () => {
    expect(formatMoney(-250, "USD", 2, "en-US")).toBe("-$2.50");
  });

  it("uses the farm locale's separators, not the UI language's", () => {
    expect(formatMoney(1000000, "EUR", 2, "de-DE")).toBe("10.000,00\u00a0€");
  });

  it("falls back to the default locale when the farm's locale tag is malformed", () => {
    // A tag the server accepted but this browser's ICU rejects must not take
    // out every screen that shows money.
    expect(formatMoney(1050, "USD", 2, "not a locale!")).toBe(formatMoney(1050, "USD", 2, DEFAULT_LOCALE));
  });
});

describe("formatCount", () => {
  it("groups thousands per the farm locale", () => {
    expect(formatCount(5074, "en-US")).toBe("5,074");
    expect(formatCount(5074, "de-DE")).toBe("5.074");
  });

  it("leaves small integers alone", () => {
    expect(formatCount(327, "en-US")).toBe("327");
  });

  it("keeps a decimal value's fraction", () => {
    // Hen-day % and quantities can carry a fraction; grouping must not round it.
    expect(formatCount(101.3, "en-US")).toBe("101.3");
  });

  it("falls back to the default locale when the tag is malformed", () => {
    expect(formatCount(5074, "??")).toBe("5,074");
  });

  it("pads or rounds to a fixed fraction when one is asked for (hen-day % reads 7.0 beside 6.9)", () => {
    expect(formatCount(7, "en-US", 1)).toBe("7.0");
    expect(formatCount(101.34, "en-US", 1)).toBe("101.3");
    expect(formatCount(1234.5, "de-DE", 1)).toBe("1.234,5");
  });
});

describe("formatDate", () => {
  it("renders an ISO calendar date in the locale's numeric short form when no override is set", () => {
    expect(formatDate("2026-08-14", "en-US", null)).toBe("08/14/2026");
    expect(formatDate("2026-08-14", "es-MX", null)).toBe("14/08/2026");
  });

  it("is a calendar-square conversion: the day never shifts with the runner's timezone", () => {
    // 2026-08-14 is a date, not an instant; a Date parsed as local midnight and
    // formatted in UTC (or the reverse) would roll it to the 13th west of UTC.
    expect(formatDate("2026-01-01", "en-US", null)).toBe("01/01/2026");
    expect(formatDate("2026-12-31", "en-US", null)).toBe("12/31/2026");
  });

  it("honours the farm's .NET-style date-format override presets", () => {
    expect(formatDate("2026-08-14", "en-US", "MM/dd/yyyy")).toBe("08/14/2026");
    expect(formatDate("2026-08-14", "en-US", "dd/MM/yyyy")).toBe("14/08/2026");
    expect(formatDate("2026-08-14", "en-US", "yyyy-MM-dd")).toBe("2026-08-14");
  });

  it("expands the name and short-form tokens through the locale", () => {
    expect(formatDate("2026-08-04", "en-US", "d MMM yyyy")).toBe("4 Aug 2026");
    expect(formatDate("2026-08-04", "en-US", "dddd, MMMM d, yy")).toBe("Tuesday, August 4, 26");
    expect(formatDate("2026-08-04", "es-ES", "ddd d MMM")).toBe("mar 4 ago");
    expect(formatDate("2026-08-04", "en-US", "M/d/yyyy")).toBe("8/4/2026");
  });

  it("passes quoted literals and other characters through untouched", () => {
    expect(formatDate("2026-08-14", "en-US", "yyyy 'year' MM.dd")).toBe("2026 year 08.14");
    expect(formatDate("2026-08-14", "en-US", "dd\\d MM")).toBe("14d 08");
  });

  it("returns a non-ISO input unchanged rather than inventing a date", () => {
    expect(formatDate("", "en-US", null)).toBe("");
    expect(formatDate("2026-08", "en-US", null)).toBe("2026-08");
    expect(formatDate("not a date", "en-US", "dd/MM/yyyy")).toBe("not a date");
  });

  it("falls back to the default locale when the tag is malformed", () => {
    expect(formatDate("2026-08-14", "not a locale!", null)).toBe("08/14/2026");
    expect(formatDate("2026-08-14", "not a locale!", "MMM")).toBe("Aug");
  });
});
