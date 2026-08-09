import { ApiError } from "../api/client";

// The same three lines were copy-pasted into a dozen screens. Shared here for
// the screens #469 touches; the rest keep their local copy until they are
// edited for another reason.
export function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}
