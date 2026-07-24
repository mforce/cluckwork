// Farm-local calendar date (YYYY-MM-DD).
//
// `timeZone` is the farm's IANA zone from §4.5 settings, supplied by
// useFarmToday(). Since #35 the API decides "is this date in the future?"
// against the FARM's today, so a browser ahead of the farm would otherwise
// offer a date the server refuses, and one behind it would hide a legitimate
// one (#123).
//
// Omitted means browser-local — not a shortcut but the only answer available
// when the farm's zone is not known: before /account resolves, and after a
// load that failed. It is also what every date input did before this.
export function todayIso(timeZone?: string): string {
  const now = new Date();
  if (timeZone !== undefined) {
    try {
      // Formatted, not arithmetic: no offset table to keep, and DST is the
      // browser's problem. en-US with explicit numeric parts, so the result
      // does not depend on a locale's ordering or separators — only the parts
      // are read, and they are reassembled in ISO order here.
      const parts = new Intl.DateTimeFormat("en-US", {
        timeZone,
        year: "numeric",
        month: "2-digit",
        day: "2-digit",
      }).formatToParts(now);
      const at: Record<string, string> = {};
      for (const p of parts) at[p.type] = p.value;
      return `${at.year}-${at.month}-${at.day}`;
    } catch {
      // A zone id this browser's ICU does not know (the API's catalogue is the
      // server's, which can be newer). Browser-local is wrong by at most a day
      // near midnight; throwing here would take out every screen with a date
      // field, so it degrades instead.
    }
  }
  // NOT toISOString(), which is UTC and rolls to the wrong operational day for
  // farms west/east of UTC in the evening/morning.
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

// True when THIS browser can format in `timeZone`. The server's IANA table can
// be newer than the browser's, so a zone it accepts on save may be one the SPA
// cannot use — in which case todayIso() below falls back to browser-local and
// every date field quietly stops following the farm. The settings screen checks
// with this so the admin is told at the field instead (review of #123).
export function isKnownTimeZone(timeZone: string): boolean {
  try {
    new Intl.DateTimeFormat("en-US", { timeZone });
    return true;
  } catch {
    return false;
  }
}

// N days before a farm-local calendar date (YYYY-MM-DD in, YYYY-MM-DD out).
//
// Arithmetic on the DATE PARTS through UTC, never on a local Date: a farm-local
// day is a calendar square, not an instant. `new Date(y, m, d - n)` would work
// too — it normalizes, and it is what ReportsPage used before this — but it
// resolves through the RUNNER's zone, and this function's inputs and outputs
// are the FARM's calendar. Going through UTC keeps the browser's own offset,
// and any DST rule it carries, out of the arithmetic entirely.
export function daysBefore(isoDate: string, days: number): string {
  const [year, month, day] = isoDate.split("-").map(Number);
  const shifted = new Date(Date.UTC(year, month - 1, day - days));
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${shifted.getUTCFullYear()}-${pad(shifted.getUTCMonth() + 1)}-${pad(shifted.getUTCDate())}`;
}

// Whole weeks a flock has been placed, floored, never negative (a future
// placement date reads as age 0). `nowMs` is injectable for testing; callers
// pass a farm-local `placementDate` (YYYY-MM-DD), interpreted at local midnight.
export function ageWeeks(placementDate: string, nowMs: number = Date.now()): number {
  const placed = new Date(placementDate + "T00:00:00");
  const days = (nowMs - placed.getTime()) / 86_400_000;
  return Math.max(0, Math.floor(days / 7));
}
