import { useFormat } from "../farm/useFormat";

// A farm-local calendar date in a list (#650): the label is the farm's own
// format (Settings → Date format, else the farm locale's short form), and
// the ISO day travels in `datetime` so the value stays machine-readable
// whatever the label looks like — the E2E harness selects rows by it.
export function FarmDate({ iso }: { iso: string }) {
  const fmt = useFormat();
  return <time dateTime={iso}>{fmt.date(iso)}</time>;
}
