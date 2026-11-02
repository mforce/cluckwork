import { ApiError } from "../api/client";
import { secondFactorMessage } from "./secondFactorMessage";

// The same three lines were copy-pasted into a dozen screens. Shared here for
// the screens #469 touches; the rest keep their local copy until they are
// edited for another reason.
//
// #1279 — step-up's second-factor answers read the same in every dialog.
export function errText(err: unknown): string {
  if (err instanceof ApiError) return secondFactorMessage(err) ?? err.message;
  return err instanceof Error ? err.message : String(err);
}
