import { useLatestLoad } from "../../components/useLatestLoad";
import { daysBefore, inclusiveDays, isIsoCalendarDate } from "../../lib/dates";

export function previousPeriod(from: string, to: string) {
  const days = inclusiveDays(from, to);
  const previousFrom = daysBefore(from, days);
  return days > 0 && isIsoCalendarDate(previousFrom)
    ? { from: previousFrom, to: daysBefore(from, 1), days }
    : null;
}

// Wait for the current reads to finish so comparisons do not add concurrent
// requests to the farm's report limit. A comparison failure stays local.
export function usePreviousPeriod<T>(
  from: string, to: string, current: T | null,
  read: (from: string, to: string, signal: AbortSignal) => Promise<T>,
) {
  const { data, loading } = useLatestLoad([from, to, current], async (signal) => {
    const range = previousPeriod(from, to);
    return current !== null && range !== null
      ? read(range.from, range.to, signal)
      : null;
  });
  return loading ? null : data;
}

export function periodChange(current: number | null, previous: number | null | undefined, rate = false) {
  if (current == null || previous == null || previous === 0) return null;
  return rate ? current - previous : (current - previous) / Math.abs(previous) * 100;
}
