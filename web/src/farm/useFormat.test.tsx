import { describe, it, expect } from "vitest";
import { renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { FarmContext } from "./FarmContext";
import { useFormat } from "./useFormat";
import { account, farmState } from "../test/fixtures";

// The one place a screen binds §4.5 formatting to the farm (#650): locale and
// date-format override come from /account, and a screen rendered before the
// farm resolves (or outside a provider) still formats — in the default locale.

const withFarm = (farm: ReturnType<typeof account> | null) =>
  ({ children }: { children: ReactNode }) => (
    <FarmContext.Provider value={farmState({ farm })}>{children}</FarmContext.Provider>
  );

describe("useFormat", () => {
  it("binds money, count and date to the farm's locale and override", () => {
    const farm = account({ locale: "de-DE", currencyCode: "EUR", dateFormatOverride: "dd.MM.yyyy" });
    const { result } = renderHook(() => useFormat(), { wrapper: withFarm(farm) });
    expect(result.current.money(123456, "EUR", 2)).toBe("1.234,56\u00a0€");
    expect(result.current.count(5074)).toBe("5.074");
    expect(result.current.date("2026-08-14")).toBe("14.08.2026");
  });

  it("uses the locale's own short date when the farm sets no override", () => {
    const farm = account({ locale: "es-MX", dateFormatOverride: null });
    const { result } = renderHook(() => useFormat(), { wrapper: withFarm(farm) });
    expect(result.current.date("2026-08-14")).toBe("14/08/2026");
  });

  it("formats in the default locale when no farm has resolved", () => {
    const { result } = renderHook(() => useFormat(), { wrapper: withFarm(null) });
    expect(result.current.money(123456, "USD", 2)).toBe("$1,234.56");
    expect(result.current.count(5074)).toBe("5,074");
    expect(result.current.date("2026-08-14")).toBe("08/14/2026");
  });

  it("formats in the default locale outside any provider", () => {
    const { result } = renderHook(() => useFormat());
    expect(result.current.money(1050, "USD", 2)).toBe("$10.50");
  });
});
