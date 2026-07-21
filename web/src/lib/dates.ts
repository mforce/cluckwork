// Farm-local calendar date — NOT toISOString(), which is UTC and rolls to the
// wrong operational day for farms west/east of UTC in the evening/morning.
// (Browser-local ≈ farm-local for the MVP; true farm timezones are issue #35.)
export function todayIso(): string {
  const d = new Date();
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

// Whole weeks a flock has been placed, floored, never negative (a future
// placement date reads as age 0). `nowMs` is injectable for testing; callers
// pass a farm-local `placementDate` (YYYY-MM-DD), interpreted at local midnight.
export function ageWeeks(placementDate: string, nowMs: number = Date.now()): number {
  const placed = new Date(placementDate + "T00:00:00");
  const days = (nowMs - placed.getTime()) / 86_400_000;
  return Math.max(0, Math.floor(days / 7));
}
