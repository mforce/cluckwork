// Farm-local calendar date — NOT toISOString(), which is UTC and rolls to the
// wrong operational day for farms west/east of UTC in the evening/morning.
// (Browser-local ≈ farm-local for the MVP; true farm timezones are issue #35.)
export function todayIso(): string {
  const d = new Date();
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}
