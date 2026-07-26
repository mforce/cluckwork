import { describe, it, expect, afterEach } from "vitest";
import { formatMoney } from "../api/cluckwork";
import { todayIso } from "../lib/dates";
import i18n from "../i18n";

afterEach(async () => { await i18n.changeLanguage("en"); });

describe("formatting is driven by the farm, not the UI language (#182, §4.5)", () => {
  it("money output is unchanged when the UI language changes", async () => {
    const before = formatMoney(1050, "USD", 2);
    i18n.addResourceBundle("xx", "common", {}, true, true);
    await i18n.changeLanguage("xx");
    expect(formatMoney(1050, "USD", 2)).toBe(before); // "10.50 USD"
  });

  it("date output is driven by the farm timezone, unchanged by UI language", async () => {
    const tz = "Asia/Tokyo";
    const before = todayIso(tz);
    await i18n.changeLanguage("xx");
    expect(todayIso(tz)).toBe(before);
  });
});
