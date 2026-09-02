import i18n from "../i18n";

// #653 — the RELATIVE phrase (this helper) uses the FARM clock; the EXACT
// INSTANT stays UTC, unchanged, in ProvenanceCell's `title` (#494's decision,
// untouched). Both are right about different things: "2 days ago" is a claim
// about the READER's day, and a reader in the hen house means their farm's
// day — the precise audit stamp is a different concern and stays displayed in
// UTC everywhere else it appears. Do not "fix" one half to match the other.
//
// Intl.RelativeTimeFormat was tried first (repo rule: framework facility
// before hand-rolled) and does not fit here. Its output is a fixed, built-in
// phrase in whatever locale ICU resolves — not one of this repo's i18next
// keys — so it would never reach the native-review pipeline the i18n
// translate-now policy requires for es/tl. Locale identity is also not
// reliable: on this box, `new Intl.RelativeTimeFormat("tl")` silently
// resolves to `fil` (`resolvedOptions().locale === "fil"`), inconsistent with
// the rest of the stack, which keys strictly on "tl" — and ICU's coverage of
// that alias is not guaranteed identical across browsers. It also carries no
// timezone awareness, so the farm-local day-boundary math below is needed
// regardless of whether it is used. Given that, i18next's own (already
// CLDR-correct) plural resolution via `count` — the same mechanism
// `equalsEggs_one` etc. already use — covers the pluralization job a
// hand-rolled table would otherwise need.

const DAY_MS = 86_400_000;

function farmLocalDateParts(ms: number, timeZone?: string): { y: number; m: number; d: number } {
  const parts = new Intl.DateTimeFormat("en-US", {
    timeZone,
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).formatToParts(new Date(ms));
  const at: Record<string, string> = {};
  for (const p of parts) at[p.type] = p.value;
  return { y: Number(at.year), m: Number(at.month), d: Number(at.day) };
}

// Calendar-day difference between two instants, computed on `timeZone`'s
// calendar (farm-local), not the device's — the same UTC-through-date-parts
// technique as lib/dates.ts's daysBefore, for the same reason: a calendar day
// is a square on the farm's calendar, not a device-local instant. Both parts
// land on exact UTC midnights, so the division below is always an exact
// integer — no rounding error to introduce.
function farmDayDiff(fromMs: number, toMs: number, timeZone?: string): number {
  const a = farmLocalDateParts(fromMs, timeZone);
  const b = farmLocalDateParts(toMs, timeZone);
  const aUtc = Date.UTC(a.y, a.m - 1, a.d);
  const bUtc = Date.UTC(b.y, b.m - 1, b.d);
  return (bUtc - aUtc) / DAY_MS;
}

// A translated "N units ago" phrase for an audit instant, on the FARM's
// calendar. `nowMs` is injectable so callers/tests can pin "now" instead of
// racing the real clock. `timeZone` is the farm's IANA zone
// (`Account.timeZoneId`, via `useFarm()`); omitted falls back to
// browser-local, the same degrade lib/dates.ts's `todayIso` uses when the
// farm hasn't loaded (or its zone read failed) — never an error, never a
// blank screen.
//
// A record's instant arriving in the FUTURE relative to `nowMs` is clock
// skew, not a real future event — these are audit trail instants the server
// wrote in the past, never scheduled ahead. It is clamped to "today" rather
// than rendered as "in N days", which would misleadingly imply a scheduled
// event that doesn't exist.
export function relativeTime(iso: string, timeZone: string | undefined, nowMs: number = Date.now()): string {
  const then = new Date(iso).getTime();
  const days = Math.max(0, farmDayDiff(then, nowMs, timeZone));

  if (days === 0) return i18n.t("relativeTime.today");
  if (days === 1) return i18n.t("relativeTime.yesterday");
  if (days < 7) return i18n.t("relativeTime.daysAgo", { count: days });
  if (days < 30) return i18n.t("relativeTime.weeksAgo", { count: Math.round(days / 7) });
  return i18n.t("relativeTime.monthsAgo", { count: Math.round(days / 30) });
}
