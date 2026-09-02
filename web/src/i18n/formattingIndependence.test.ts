import { describe, it, expect, afterEach } from "vitest";
import { formatMoney } from "../lib/format";
import { todayIso } from "../lib/dates";
import i18n from "../i18n";

afterEach(async () => { await i18n.changeLanguage("en"); });

describe("formatting is driven by the farm, not the UI language (#182, §4.5)", () => {
  it("money output is unchanged when the UI language changes", async () => {
    const before = formatMoney(1050, "USD", 2, "en-US");
    i18n.addResourceBundle("xx", "common", {}, true, true);
    await i18n.changeLanguage("xx");
    expect(formatMoney(1050, "USD", 2, "en-US")).toBe(before); // "$10.50"
  });

  it("money output changes with the FARM locale, which is the only formatting input (#650)", async () => {
    // The positive control for the test above: the same amount under two farm
    // locales differs, so "unchanged across UI languages" is not vacuous.
    expect(formatMoney(123456, "EUR", 2, "de-DE")).not.toBe(formatMoney(123456, "EUR", 2, "en-US"));
    await i18n.changeLanguage("xx");
    expect(formatMoney(123456, "EUR", 2, "de-DE")).toBe("1.234,56\u00a0€");
  });

  it("date output is driven by the farm timezone, unchanged by UI language", async () => {
    const tz = "Asia/Tokyo";
    const before = todayIso(tz);
    await i18n.changeLanguage("xx");
    expect(todayIso(tz)).toBe(before);
  });
});
