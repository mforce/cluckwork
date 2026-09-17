import { useFormat } from "../farm/useFormat";

// A farm-local calendar date in a list (#650): the label is the farm's own
// format (Settings → Date format, else the farm locale's short form), and
// the ISO day travels in `datetime` so the value stays machine-readable
// whatever the label looks like — the E2E harness selects rows by it.
//
// `short` (#897 review) — a 2-digit year regardless of the farm's configured
// override, for the one column dense enough to need the width back (Flocks'
// Placed). Off by default: every other `FarmDate` caller keeps the farm's
// own choice unchanged.
export function FarmDate({ iso, short }: { iso: string; short?: boolean }) {
  const fmt = useFormat();
  return <time dateTime={iso}>{short ? fmt.dateShort(iso) : fmt.date(iso)}</time>;
}
