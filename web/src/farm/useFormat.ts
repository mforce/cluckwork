import { useMemo } from "react";
import { useFarm } from "./useFarm";
import { DEFAULT_LOCALE, formatCount, formatDate, formatMoney, formatTime } from "../lib/format";

// §4.5 formatting bound to the farm (#650): one hook, four formatters, so a
// screen never reads `farm.locale` itself and never reaches for the UI
// language. Before /account resolves — and outside a provider, in tests — the
// default locale applies; the figures are right either way, only the
// separators differ, and the provider's re-render swaps them in.
export function useFormat() {
  const { farm } = useFarm();
  const locale = farm?.locale ?? DEFAULT_LOCALE;
  const dateOverride = farm?.dateFormatOverride ?? null;
  // Farm-local, never the browser's — undefined when no farm has resolved
  // yet, the same degrade lib/relativeTime.ts's `timeZone` param uses.
  const timeZone = farm?.timeZoneId;
  return useMemo(() => ({
    money: (minorUnits: number, currencyCode: string, minorUnit: number) =>
      formatMoney(minorUnits, currencyCode, minorUnit, locale),
    count: (value: number, fractionDigits?: number) => formatCount(value, locale, fractionDigits),
    date: (isoDate: string) => formatDate(isoDate, locale, dateOverride),
    time: (iso: string | null) => (iso === null ? null : formatTime(iso, locale, timeZone)),
  }), [locale, dateOverride, timeZone]);
}
