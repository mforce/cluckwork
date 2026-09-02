// Farm-locale display formatting (§4.5, #650).
//
// Money, counts and calendar dates render through the FARM's locale, currency
// and date-format override — never the UI language. `users.language` picks the
// strings; `farms.locale` picks the separators, symbol placement and date
// order (specs §4.5 "Language never changes formatting"; pinned by
// i18n/formattingIndependence.test.ts). Screens reach these through
// farm/useFormat.ts, which binds the farm; the raw functions take the locale
// explicitly so nothing here can fall back to the browser's locale by accident.

export const DEFAULT_LOCALE = "en-US";

// A locale tag the server accepted but this browser's ICU rejects must not
// take out every screen that shows a number: Intl throws RangeError on a
// malformed tag, and an unknown-but-well-formed one already resolves to the
// default on its own.
function numberFormat(locale: string, options: Intl.NumberFormatOptions): Intl.NumberFormat {
  try {
    return new Intl.NumberFormat(locale, options);
  } catch {
    return new Intl.NumberFormat(DEFAULT_LOCALE, options);
  }
}

function dateFormat(locale: string, options: Intl.DateTimeFormatOptions): Intl.DateTimeFormat {
  try {
    return new Intl.DateTimeFormat(locale, options);
  } catch {
    return new Intl.DateTimeFormat(DEFAULT_LOCALE, options);
  }
}

// Minor units per the row's snapshotted currency (JPY has 0 decimals, BHD 3):
// the minor-unit count is the row's, never a hardcoded 2.
export function formatMoney(minorUnits: number, currencyCode: string, minorUnit: number, locale: string): string {
  const value = minorUnits / 10 ** minorUnit;
  return numberFormat(locale, {
    style: "currency",
    currency: currencyCode,
    minimumFractionDigits: minorUnit,
    maximumFractionDigits: minorUnit,
  }).format(value);
}

// Egg counts, bird counts, hen-days, quantities. Grouping per the locale; a
// fraction is kept as it arrives unless `fractionDigits` fixes it — a column
// of percentages reads "7.0" beside "6.9", not "7", so the report passes 1.
export function formatCount(value: number, locale: string, fractionDigits?: number): string {
  const digits = fractionDigits === undefined
    ? { maximumFractionDigits: 20 }
    : { minimumFractionDigits: fractionDigits, maximumFractionDigits: fractionDigits };
  return numberFormat(locale, digits).format(value);
}

const ISO_DATE = /^(\d{4})-(\d{2})-(\d{2})$/;

// The override is a .NET custom date format string — that is what the API
// validates it as (UpdateFarmSettingsValidator "usable .NET format string")
// and what the Settings presets are ("MM/dd/yyyy", "dd/MM/yyyy",
// "yyyy-MM-dd"). The date tokens are interpreted here; anything else — a
// quoted literal, a backslash-escaped character, a separator — passes through
// as itself. Time tokens are not in this set: nothing in the SPA renders a
// time through the override yet, and a date has none to render.
const TOKEN = /yyyy|yy|MMMM|MMM|MM|M|dddd|ddd|dd|d|'[^']*'|"[^"]*"|\\.|./g;

// A farm-local calendar date (YYYY-MM-DD) is a calendar square, not an
// instant: it is turned into a Date at UTC midnight and every Intl call reads
// it back in UTC, so the runner's own zone can never roll it a day.
//
// setUTCFullYear, not Date.UTC: the latter reads a year 0–99 as 1900–1999.
// Both silently normalise an impossible day (2026-02-30 → March 2), so the
// components are read back and compared — a date that does not survive the
// round trip is shown as it arrived rather than as a day the farm never had.
export function formatDate(isoDate: string, locale: string, override: string | null): string {
  const m = ISO_DATE.exec(isoDate);
  if (!m) return isoDate;
  const [, y, mo, d] = m;
  const date = new Date(0);
  date.setUTCFullYear(Number(y), Number(mo) - 1, Number(d));
  if (date.getUTCFullYear() !== Number(y) || date.getUTCMonth() !== Number(mo) - 1 || date.getUTCDate() !== Number(d)) {
    return isoDate;
  }

  if (override === null || override.trim() === "") {
    return dateFormat(locale, { timeZone: "UTC", year: "numeric", month: "2-digit", day: "2-digit" }).format(date);
  }

  const part = (options: Intl.DateTimeFormatOptions) => dateFormat(locale, { timeZone: "UTC", ...options }).format(date);
  return override.replace(TOKEN, (token) => {
    switch (token) {
      case "yyyy": return y;
      case "yy": return y.slice(2);
      case "MMMM": return part({ month: "long" });
      case "MMM": return part({ month: "short" });
      case "MM": return mo;
      case "M": return String(Number(mo));
      case "dddd": return part({ weekday: "long" });
      case "ddd": return part({ weekday: "short" });
      case "dd": return d;
      case "d": return String(Number(d));
      default:
        if (token.length >= 2 && (token[0] === "'" || token[0] === '"')) return token.slice(1, -1);
        if (token[0] === "\\") return token.slice(1);
        return token;
    }
  });
}
