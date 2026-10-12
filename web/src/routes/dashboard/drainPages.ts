// The server clamps a list request at 500 rows.
const MAX_PAGE = 500;
// #915 — both of this panel's lists are DRAINED rather than truncated: the
// progress bar and the "N of M houses in" caption answer for the farm, which
// a first page cannot tell them, and every house has to be reachable now that
// no link carries the remainder. Below 500 houses this costs no extra request
// at all. The ceiling only has to make the loop terminate; at it the counts
// say "at least" instead of a figure they cannot stand behind.
const MAX_DRAIN_PAGES = 20;

// `signal` stops the WALK as well as the request in flight: leaving the
// Dashboard mid-drain used to leave the loop asking for every later page,
// dozens of reads nobody was waiting for.
export async function drainPages<T>(
  fetchPage: (offset: number, limit: number, signal: AbortSignal) => Promise<T[]>,
  signal: AbortSignal,
): Promise<{ rows: T[]; truncated: boolean }> {
  const rows: T[] = [];
  for (let page = 0; page < MAX_DRAIN_PAGES; page += 1) {
    if (signal.aborted) return { rows, truncated: false };
    const batch = await fetchPage(page * MAX_PAGE, MAX_PAGE, signal);
    rows.push(...batch);
    if (batch.length < MAX_PAGE) return { rows, truncated: false };
  }
  return { rows, truncated: true };
}
