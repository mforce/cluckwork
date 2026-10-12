import { ApiError } from "../../api/client";
import type { Flock } from "../../api/cluckwork";

// Capture targets active flocks plus depleted ones — a depleted flock still
// accepts backfilled entries up to its depletion date (the API gates exact
// dates), matching the Flocks screen's promise and the feed-usage picker.
// Archived flocks accept nothing and stay hidden. Every flock refresh on this
// page (initial load AND the new-flock dialog) must go through this filter.
export const capturable = (flocks: Flock[]) => flocks.filter((x) => x.status !== "Archived");

export function errorMessage(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// The scopes that own a dialog (#703). save/submit route to the page and are
// never superseded.
export const DIALOG_SCOPES = ["new-flock"] as const;
